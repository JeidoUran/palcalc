using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

using Discord;
using Discord.Interactions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using PalCalc.Model;
using PalCalc.Solver;
using PalCalc.Solver.PalReference;
using PalCalc.Solver.ResultPruning;

namespace PalCalc.DiscordBot;

public enum TargetGender
{
    Free,
    Male,
    Female
}

public sealed class PalCalcModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<PalCalcModule> _log;
    private readonly PlayerIndexService _players;
    private readonly EmojiRegistry _emojis;
    private readonly PassiveIndexService _passives;
    public PalCalcModule(
        IConfiguration cfg,
        ILogger<PalCalcModule> log,
        PlayerIndexService players,
        PassiveIndexService passives,
        EmojiRegistry emojis)
    {
        _cfg = cfg;
        _log = log;
        _players = players;
        _passives = passives;
        _emojis = emojis;
    }

    [SlashCommand("palcalc", "Trouve des solutions de breeding à partir des données du serveur")]
    public async Task PalCalcAsync(
        [Autocomplete(typeof(PlayerAutocompleteHandler))]
        [Summary(description: "Joueur")]
        string player, // UID (value de l'autocomplete)
        [Summary("target_pal", "Pal cible")]
        [Autocomplete(typeof(PalAutocompleteHandler))]
        string targetPalInternal, // InternalName (value de l'autocomplete)
        [Summary(description: "Genre cible")] TargetGender target_gender = TargetGender.Free,

        [Summary(description: "Passif #1")]
        [Autocomplete(typeof(PassiveAutocompleteHandler))]
        string? passive_1 = null,

        [Summary(description: "Passif #2")]
        [Autocomplete(typeof(PassiveAutocompleteHandler))]
        string? passive_2 = null,

        [Summary(description: "Passif #3")]
        [Autocomplete(typeof(PassiveAutocompleteHandler))]
        string? passive_3 = null,

        [Summary(description: "Passif #4")]
        [Autocomplete(typeof(PassiveAutocompleteHandler))]
        string? passive_4 = null,

        [Summary(description: "IV HP min (0-100)")] int iv_hp = 0,
        [Summary(description: "IV ATK min (0-100)")] int iv_atk = 0,
        [Summary(description: "IV DEF min (0-100)")] int iv_def = 0
    )
    {
        await DeferAsync();

        // ---- config ----
        var saveDir = _cfg["PalCalc:SaveDir"];
        if (string.IsNullOrWhiteSpace(saveDir))
        {
            await FollowupAsync("❌ Config manquante: `PalCalc:SaveDir`");
            return;
        }

        // BotCLI project path: par défaut relative à la solution
        var botCliProject = _cfg["PalCalc:BotCliProject"] ?? "PalCalc.BotCLI";

        // ---- resolve player display name (utile pour BotCLI) ----
        var playerName = ResolvePlayerNameFromUid(player) ?? player; // fallback
        _log.LogInformation("palcalc request: playerUid={Uid} playerName={Name} target={Target}", player, playerName, targetPalInternal);

        // ---- build required passives ----
        var requiredPassiveIds = new[] { passive_1, passive_2, passive_3, passive_4 }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            // 1) Dump owned -> JSON (via BotCLI)
            var ownedJsonPath = await RunBotCliDumpOwnedAsync(botCliProject, saveDir, playerName);

            // 2) Load db + owned instances
            var db = PalDB.LoadEmbedded();
            var gameSettings = new GameSettings();

            var ownedInstances = SolverMapping.LoadOwnedPalsFromOwnedJson(ownedJsonPath, db);

            // 3) Build Pal spec
            var target = db.Pals.FirstOrDefault(p => p.InternalName.Equals(targetPalInternal, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                await FollowupAsync($"❌ Pal inconnu dans la DB: `{targetPalInternal}`");
                return;
            }

            var passivesByInternal = db.PassiveSkills
                .Where(ps => !string.IsNullOrWhiteSpace(ps.InternalName))
                .ToDictionary(ps => ps.InternalName!, StringComparer.OrdinalIgnoreCase);

            _passives.TryRefreshIfStale();
            var passiveIdByFrName = _passives.GetCachedFast()
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);


            var requiredPassives = new List<PassiveSkill>();

            foreach (var raw in requiredPassiveIds)
            {
                // raw peut être InternalName (OK) OU label FR (selon ce que Discord renvoie)
                var key = raw;

                if (!passivesByInternal.ContainsKey(key) && passiveIdByFrName.TryGetValue(key, out var mapped))
                    key = mapped;

                if (passivesByInternal.TryGetValue(key, out var ps))
                    requiredPassives.Add(ps);
                else
                    _log.LogWarning("Unknown passive: {Passive}", raw);
            }


            var requiredGender = target_gender switch
            {
                TargetGender.Male => PalGender.MALE,
                TargetGender.Female => PalGender.FEMALE,
                _ => PalGender.WILDCARD
            };

            var spec = new PalCalc.Solver.PalSpecifier
            {
                Pal = target,
                RequiredGender = requiredGender,
                IV_HP = Clamp(iv_hp),
                IV_Attack = Clamp(iv_atk),
                IV_Defense = Clamp(iv_def),
                RequiredPassives = requiredPassives,
                OptionalPassives = new()
            };

            // 4) Solver settings + solve
            var pruning = PruningRulesBuilder.Default;
            var settings = CreateBreedingSolverSettings(db, gameSettings, ownedInstances, pruning);

            var solver = new BreedingSolver(settings);
            var controller = new SolverStateController();

            var results = solver.SolveFor(spec, controller);

            // 5) Prune + pick top
            var pruner = pruning.BuildAggregate(controller.CancellationToken);
            var cached = new CachedResultData(results);
            var pruned = pruner.Apply(results, cached).ToList();

            // 6) Render (top 3) en code blocks (Discord 2000 chars)
            var header = BuildHeader(playerName, target, target_gender, requiredPassiveIds, iv_hp, iv_atk, iv_def, ownedInstances.Count, results.Count, pruned.Count);
            await FollowupAsync(embed: header);

            if (pruned.Count == 0)
            {
                await FollowupAsync("😢 Aucune solution après pruning. (Essaie de baisser les contraintes, ou on ajuste les settings.)");
                return;
            }

            var top = pruned
                .OrderBy(x => x.NumTotalEggs)
                .ThenBy(x => x.NumTotalBreedingSteps)
                .ThenBy(x => x.BreedingEffort)
                .Take(3)
                .ToList();

            for (int i = 0; i < top.Count; i++)
            {
                var text = RenderPlan(top[i], $"Solution #{i + 1}");
                foreach (var chunk in ChunkForDiscord(text, 1900))
                    await FollowupAsync(chunk);

            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "palcalc failed");
            await FollowupAsync($"❌ Erreur: `{ex.GetType().Name}` — {ex.Message}");
        }
    }

    // =========================
    // BotCLI integration
    // =========================

    private async Task<string> RunBotCliDumpOwnedAsync(string botCliProject, string saveDir, string playerName)
    {
        // On exécute: dotnet run --project PalCalc.BotCLI -- dump-owned --saveDir "<...>" --player "<...>" --json
        // Puis on écrit stdout dans un fichier temporaire.
        var tmpDir = Path.Combine(Path.GetTempPath(), "palcalc-discordbot");
        Directory.CreateDirectory(tmpDir);

        var outPath = Path.Combine(tmpDir, $"owned.{SanitizeFile(playerName)}.{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"run --project {Quote(botCliProject)} -- " +
                $"dump-owned --saveDir {Quote(saveDir)} --player {Quote(playerName)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        if (!p.Start())
            throw new Exception("Failed to start BotCLI process.");

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // Timeout raisonnable (tweak si besoin)
        var timeoutMs = 60_000;
        var exited = await Task.Run(() => p.WaitForExit(timeoutMs));
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("BotCLI dump-owned timed out.");
        }

        if (p.ExitCode != 0)
        {
            var err = stderr.ToString();
            if (string.IsNullOrWhiteSpace(err)) err = "(no stderr)";
            throw new Exception($"BotCLI failed (exit {p.ExitCode}): {TrimForLog(err, 2000)}");
        }

        var json = stdout.ToString();
        if (string.IsNullOrWhiteSpace(json))
            throw new Exception("BotCLI returned empty JSON.");

        File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _log.LogInformation("Dump-owned OK: {Path} bytes={Bytes}", outPath, new FileInfo(outPath).Length);

        return outPath;
    }

    private string? ResolvePlayerNameFromUid(string uid)
    {
        try
        {
            var cached = _players.GetCachedPlayersFast();
            var hit = cached.FirstOrDefault(p => p.Id.Equals(uid, StringComparison.OrdinalIgnoreCase));
            return hit?.Name;
        }
        catch { return null; }
    }

    // =========================
    // Solver settings (copié de ton BotCLI)
    // =========================

    private static BreedingSolverSettings CreateBreedingSolverSettings(
        PalDB db,
        GameSettings gameSettings,
        List<PalInstance> ownedInstances,
        object pruningRules
    )
    {
        const int maxBreedingSteps = 10;
        const int maxSolverIterations = 20;
        const int maxWildPals = 0;

        const int maxInputIrrelevantPassives = 3;
        const int maxBredIrrelevantPassives = 1;

        var maxEffort = TimeSpan.FromDays(1);
        var maxThreads = Environment.ProcessorCount;

        const int maxSurgeryCost = 0;
        var allowedSurgeryPassives = new List<PassiveSkill>();
        const bool useGenderReversers = false;

        var allowedWildPals = new List<Pal>();
        var bannedBredPals = new List<Pal>();

        var t = typeof(BreedingSolverSettings);
        var ctor = t.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];

        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var name = p.Name?.ToLowerInvariant() ?? "";

            if (p.ParameterType == typeof(PalDB)) { args[i] = db; continue; }
            if (p.ParameterType == typeof(GameSettings)) { args[i] = gameSettings; continue; }
            if (p.ParameterType == typeof(List<PalInstance>)) { args[i] = ownedInstances; continue; }

            if (p.ParameterType.IsAssignableFrom(pruningRules.GetType())) { args[i] = pruningRules; continue; }

            if (name == "maxbreedingsteps") { args[i] = maxBreedingSteps; continue; }
            if (name == "maxsolveriterations") { args[i] = maxSolverIterations; continue; }
            if (name == "maxwildpals") { args[i] = maxWildPals; continue; }

            if (name == "allowedwildpals") { args[i] = allowedWildPals; continue; }
            if (name == "bannedbredpals") { args[i] = bannedBredPals; continue; }

            if (name == "maxinputirrelevantpassives") { args[i] = maxInputIrrelevantPassives; continue; }
            if (name == "maxbredirrelevantpassives") { args[i] = maxBredIrrelevantPassives; continue; }

            if (name == "maxeffort") { args[i] = maxEffort; continue; }
            if (name == "maxthreads") { args[i] = maxThreads; continue; }

            if (name == "maxsurgerycost") { args[i] = maxSurgeryCost; continue; }
            if (name == "allowedsurgerypassives") { args[i] = allowedSurgeryPassives; continue; }
            if (name == "usegenderreversers") { args[i] = useGenderReversers; continue; }

            if (p.HasDefaultValue) { args[i] = p.DefaultValue; continue; }
            if (p.ParameterType.IsValueType) { args[i] = Activator.CreateInstance(p.ParameterType); continue; }
            args[i] = null;
        }

        return (BreedingSolverSettings)ctor.Invoke(args);
    }

    // =========================
    // Rendering / helpers
    // =========================

    private static Embed BuildHeader(
        string playerName,
        Pal target,
        TargetGender gender,
        List<string> requiredPassiveIds,
        int ivHp, int ivAtk, int ivDef,
        int ownedCount,
        int rawCount,
        int prunedCount)
    {
        string genderLabel = gender switch
        {
            TargetGender.Male => "Male",
            TargetGender.Female => "Female",
            _ => "Free"
        };

        var passivesLabel = requiredPassiveIds.Count == 0
            ? "—"
            : string.Join(", ", requiredPassiveIds);

        return new EmbedBuilder()
            .WithTitle("PalCalc — Breeding Solver")
            .AddField("Joueur", playerName, inline: true)
            .AddField("Cible", $"{target.Name ?? target.InternalName} ({genderLabel})", inline: true)
            .AddField("Passifs requis", passivesLabel, inline: false)
            .AddField("IV mins", $"HP {Clamp(ivHp)}/100 • ATK {Clamp(ivAtk)}/100 • DEF {Clamp(ivDef)}/100", inline: false)
            .AddField("Owned", ownedCount.ToString(), inline: true)
            .AddField("Résultats", $"raw={rawCount} • pruned={prunedCount}", inline: true)
            .WithColor(new Color(88, 101, 242))
            .Build();
    }

    private string RenderPlan(object plan, string title)
    {
        // 1) Capture output brut
        var raw = CaptureBreedingRenderer(plan);

        // 2) Pretty format
        var pretty = PrettyFormatBreedingPlan(raw);

        // 3) Header + body
        var sb = new StringBuilder();
        sb.AppendLine($"<:egg:1464196335430402122> {title}");
        sb.AppendLine();
        sb.AppendLine(pretty);

        return sb.ToString().TrimEnd();
    }

    private static string CaptureBreedingRenderer(object plan)
    {
        var oldOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            BreedingPlanRenderer.Print((IPalReference)plan);

            Console.Out.Flush();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(oldOut);
        }
    }

    private string PrettyFormatBreedingPlan(string raw)
    {
        // Normalize lines
        var lines = raw.Replace("\r\n", "\n")
                      .Split('\n')
                      .Select(l => l.TrimEnd())
                      .Where(l => !string.IsNullOrWhiteSpace(l))
                      .ToList();

        _passives.TryRefreshIfStale();

        // 1) dictionnaire clé interne -> FR depuis ton JSON (la vraie source)
        var frByInternal = LoadPassiveLocFr();

        // 2) dictionnaire nom anglais lisible -> clé interne, via PalDB
        // (cache simple par appel; si tu veux optimiser, on le met en champ plus tard)
        var db = PalDB.LoadEmbedded();
        var internalByEnglish = BuildInternalByEnglishName(db);

        // On va reconstruire un arbre “à l’ancienne” en suivant l’indentation (les lignes ont souvent des espaces)
        // Mais ici on prend une approche simple: on utilise les préfixes connus.

        var sb = new StringBuilder();

        const int INDENT_RESULT = 0;   // Pal final
        const int INDENT_OWNED  = 2;   // Pal possédé
        const int INDENT_PASSIVE = 4;  // Passifs d’un Pal

        int indent = 0;
        void Line(string s, int extraIndent = 0)
        {
            sb.Append(' ', Math.Max(0, (indent + extraIndent) * 2));
            sb.AppendLine(s);
        }

        string? currentStepHeader = null;

        foreach (var l in lines)
        {
            var t = l.Trim();

            // Lignes qu'on ignore (trop "bruit")
            if (t.Equals("parents:", StringComparison.OrdinalIgnoreCase))
                continue;

            // Step lines
            // Exemple: "- BRED Jormuntide Ignis WILDCARD steps=3 eggs=3 totalEggs=14"
            if (t.StartsWith("- BRED ", StringComparison.OrdinalIgnoreCase))
            {
                var info = ParseBredLine(t);
                var palEmoji = EmojiForPal(info.palName);
                // Un BRED peut être la cible finale OU une sous-étape.
                // On met un header d’étape si totalEggs/eggs présents.
                var eggsTxt = info.eggs.HasValue ? $"~{info.eggs.Value}<:egg:1464196335430402122>" : null;
                var totalTxt = info.totalEggs.HasValue ? $" (total ~{info.totalEggs.Value}<:egg:1464196335430402122>)" : null;
                var stepsTxt = info.steps.HasValue ? $" • {info.steps.Value} étape(s)" : "";

                // Si c’est le premier BRED, on le traite comme résultat final
                if (currentStepHeader == null)
                {
                    var eggPart = eggsTxt != null ? $" • {eggsTxt}" : "";
                    var totalPart = totalTxt ?? "";
                    Line($"{palEmoji} **{info.palName}** {FormatGender(info.gender)}{stepsTxt}{eggPart}{totalPart}", INDENT_RESULT);
                    currentStepHeader = info.palName;
                }
                else
                {
                    Line($"🥚 **Sous-breed : {info.palName}** {FormatGender(info.gender)}{stepsTxt} • {eggsTxt}{totalTxt}");
                    indent = 2;
                }

                continue;
            }

            // Owned lines
            // Exemple: "- OWNED Anubis FEMALE [DimensionalPalStorage Onglet 20 en (3,5)]"
            if (t.StartsWith("- OWNED ", StringComparison.OrdinalIgnoreCase))
            {
                var owned = ParseOwnedLine(t);
                var palEmoji = EmojiForPal(owned.palName);

                var loc = LocalizeLocation(owned.location);

                Line($"{palEmoji} **{owned.palName}** {FormatGender(owned.gender)}{(string.IsNullOrWhiteSpace(loc) ? "" : $" — `{loc}`")}",
                    INDENT_OWNED);

                continue;
            }

            // passives
            // Exemple: "passives: Demon God, Diamond Body, (Random)"
            if (t.StartsWith("passives:", StringComparison.OrdinalIgnoreCase))
            {
                var pass = t.Substring("passives:".Length).Trim();

                // Découpe "A, B, (Random)" -> tokens
                var tokens = pass.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => x.Trim())
                                .Select(x => LocalizePassiveToken(x, frByInternal, internalByEnglish))
                                .ToList();

                Line($"🧬 Passifs attendus : **{string.Join(", ", tokens)}**", extraIndent: 0);
                continue;
            }

            // eff / actual
            // eff / héritage → volontairement ignoré pour lisibilité
            if (t.StartsWith("eff", StringComparison.OrdinalIgnoreCase))
                continue;

            if (t.StartsWith("actual", StringComparison.OrdinalIgnoreCase))
            {
                // "actual : Demon God"
                var val = t.Split(':', 2).LastOrDefault()?.Trim() ?? t;

                var tokens = val.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => x.Trim())
                                .Select(x => LocalizePassiveToken(x, frByInternal, internalByEnglish))
                                .ToList();

                Line(
                    $"{EmojiReal()} Passifs : **{string.Join(", ", tokens)}**",
                    INDENT_PASSIVE
                );

                continue;
            }

            // fallback: si on tombe sur un truc inconnu, on l’affiche proprement sans casser
            // mais on évite de spammer si ça ressemble à un séparateur
            if (t.All(c => c == '=' || c == '-' || c == '_'))
                continue;

            Line($"• {t}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatGender(string? g)
    {
        if (string.IsNullOrWhiteSpace(g)) return "";
        return g.ToUpperInvariant() switch
        {
            "MALE" => " ♂",
            "FEMALE" => " ♀",
            "WILDCARD" => " 🔀",
            "OPPOSITE_WILDCARD" => " 🔁",
            _ => $" ({g})"
        };
    }

    private static (string palName, string? gender, int? steps, int? eggs, int? totalEggs) ParseBredLine(string line)
    {
        // "- BRED <name> <gender> steps=3 eggs=3 totalEggs=14"
        var t = line.Substring("- BRED ".Length).Trim();

        int? eggs = TryParseIntAfter(t, "eggs");
        int? total = TryParseIntAfter(t, "totalEggs");
        int? steps = TryParseIntAfter(t, "steps");

        // gender est souvent le dernier token "MALE/FEMALE/WILDCARD/OPPOSITE_WILDCARD"
        // on coupe avant "steps="
        var beforeStats = t;
        var idxStats = t.IndexOf(" steps=", StringComparison.OrdinalIgnoreCase);
        if (idxStats < 0) idxStats = t.IndexOf(" eggs=", StringComparison.OrdinalIgnoreCase);
        if (idxStats < 0) idxStats = t.IndexOf(" totalEggs=", StringComparison.OrdinalIgnoreCase);
        if (idxStats > 0) beforeStats = t.Substring(0, idxStats).Trim();

        var parts = beforeStats.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        string? gender = null;

        if (parts.Count > 1)
        {
            var last = parts[^1].ToUpperInvariant();
            if (last is "MALE" or "FEMALE" or "WILDCARD" or "OPPOSITE_WILDCARD")
            {
                gender = last;
                parts.RemoveAt(parts.Count - 1);
            }
        }

        var palName = string.Join(' ', parts);

        return (palName, gender, steps, eggs, total);
    }

    private static (string palName, string? gender, string? location) ParseOwnedLine(string line)
    {
        // "- OWNED <name> FEMALE [Location ...]"
        var t = line.Substring("- OWNED ".Length).Trim();

        string? location = null;
        var open = t.IndexOf('[');
        var close = t.LastIndexOf(']');
        if (open >= 0 && close > open)
        {
            location = t.Substring(open + 1, close - open - 1).Trim();
            t = t.Substring(0, open).Trim();
        }

        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        string? gender = null;

        if (parts.Count > 1)
        {
            var last = parts[^1].ToUpperInvariant();
            if (last is "MALE" or "FEMALE" or "WILDCARD" or "OPPOSITE_WILDCARD")
            {
                gender = last;
                parts.RemoveAt(parts.Count - 1);
            }
        }

        var palName = string.Join(' ', parts);
        return (palName, gender, location);
    }

    private static int? TryParseIntAfter(string text, string key)
    {
        // accepte "key=123" OU "key~123"
        var i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += key.Length;

        if (i >= text.Length) return null;

        // saute '=' ou '~' ou ':' si jamais
        if (text[i] is '=' or '~' or ':') i++;

        int j = i;
        while (j < text.Length && char.IsDigit(text[j])) j++;

        if (j == i) return null;

        var slice = text.Substring(i, j - i);
        return int.TryParse(slice, out var v) ? v : null;
    }

    private static IEnumerable<string> ChunkForDiscord(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > max)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            sb.AppendLine(line);
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static int Clamp(int v) => v < 0 ? 0 : (v > 100 ? 100 : v);

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";
    private static string TrimForLog(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    private static string SanitizeFile(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var arr = s.ToCharArray();
        for (int i = 0; i < arr.Length; i++)
            if (bad.Contains(arr[i])) arr[i] = '_';
        return new string(arr);
    }

    private string EmojiForPal(string palLocalizedName)
    {
        var key = EmojiNameNormalizer.Normalize(palLocalizedName);
        return _emojis.TryGet(key) ?? "";
    }

    private string EmojiReal() => _emojis.TryGet("passive_rank_arrow_03") ?? "";
    private string EmojiInherit() => _emojis.TryGet("passive_rank_arrow_04") ?? "";

    private string LocalizePassiveToken(
        string token,
        Dictionary<string, string> frByInternal,
        Dictionary<string, string> internalByEnglish)
    {
        token = token.Trim();

        if (token.Equals("(Random)", StringComparison.OrdinalIgnoreCase))
            return "🎲 aléatoire";

        // 1) si c'est déjà une clé interne (ex: Nocturnal, PAL_ALLAttack_up3)
        if (frByInternal.TryGetValue(token, out var fr1))
            return fr1;

        // 2) si c'est un nom anglais lisible (ex: "Diamond Body")
        if (internalByEnglish.TryGetValue(token, out var internalKey) &&
            frByInternal.TryGetValue(internalKey, out var fr2))
            return fr2;

        return token;
    }
    private Dictionary<string, string> LoadPassiveLocFr()
    {
        var path = _cfg["PalCalc:LocalizationFile"] ?? "localization.fr.json";

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("passives", out var passivesEl) ||
                passivesEl.ValueKind != JsonValueKind.Object)
                return new(StringComparer.OrdinalIgnoreCase);

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in passivesEl.EnumerateObject())
            {
                var key = prop.Name;
                var val = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val))
                    dict[key] = val.Trim();
            }
            return dict;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load passives localization from {Path}", path);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private Dictionary<string, string> BuildInternalByEnglishName(PalDB db)
    {
        // map "Diamond Body" (anglais) -> "Deffence_up3" (internal key)
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ps in db.PassiveSkills)
        {
            if (string.IsNullOrWhiteSpace(ps.InternalName))
                continue;

            // Selon les versions de PalDB, le champ "Name" peut être l'anglais lisible
            var en = (ps.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(en))
                continue;

            // si doublon, on garde le premier (ça suffit)
            if (!dict.ContainsKey(en))
                dict[en] = ps.InternalName!;
        }

        return dict;
    }

    private string LocalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return "";

        var s = location.Trim();

        // Le renderer peut te sortir "DimensionalPalStorage ..."
        s = s.Replace("DimensionalPalStorage", "Stockage de Pals dimensionnel", StringComparison.OrdinalIgnoreCase);

        // Le JSON peut avoir "Dimensional Pal Storage (Selene), ..."
        s = s.Replace("Dimensional Pal Storage", "Stockage de Pals dimensionnel", StringComparison.OrdinalIgnoreCase);

        s = s.Replace("GlobalPalBox", "Boîte à Pals globale", StringComparison.OrdinalIgnoreCase);

        s = s.Replace("Party", "Équipe", StringComparison.OrdinalIgnoreCase);

        // Si jamais t’as encore des vieux tokens qui pop (optionnel)
        s = s.Replace("Palbox", "Boîte à Pals", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("MarketStall", "Marché aux puces", StringComparison.OrdinalIgnoreCase);

        s = Regex.Replace(
            s,
            @"\s+Onglet\s+",
            ", Onglet ",
            RegexOptions.IgnoreCase
        );

        return s;
    }

}
