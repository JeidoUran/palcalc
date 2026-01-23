using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Collections.Concurrent;

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

            // 6) Render
            var header = BuildHeader(playerName, target, target_gender, requiredPassiveIds, iv_hp, iv_atk, iv_def, ownedInstances.Count, results.Count, pruned.Count);
            await FollowupAsync(embed: header);

            if (pruned.Count == 0)
            {
                await FollowupAsync("😢 Aucune solution après pruning. (Essaie de baisser les contraintes, ou on ajuste les settings.)");
                return;
            }

            var bestPruned = pruned
                .OrderBy(x => x.NumTotalEggs)
                .ThenBy(x => x.NumTotalBreedingSteps)
                .ThenBy(x => x.BreedingEffort)
                .FirstOrDefault();

            var rawTop = results
                .OrderBy(x => x.NumTotalEggs)
                .ThenBy(x => x.NumTotalBreedingSteps)
                .ThenBy(x => x.BreedingEffort)
                .Take(30)
                .ToList();

            var pages = new List<string>();

            if (bestPruned != null)
                pages.Add(RenderPlan(bestPruned, "Solution #1 (prunée)"));

            int addedRaw = 0;
            foreach (var r in rawTop)
            {
                if (addedRaw >= 9) break;
                if (bestPruned != null && ReferenceEquals(r, bestPruned))
                    continue;

                pages.Add(RenderPlan(r, $"Solution #{pages.Count + 1} (raw)"));
                addedRaw++;
            }

            if (pages.Count == 0 && results.Count > 0)
                pages.Add(RenderPlan(results[0], "Solution #1 (raw)"));

            var session = new PagerSession
            {
                OwnerUserId = Context.User.Id,
                ChannelId = Context.Channel.Id,
                Header = header,
                Pages = pages,
                Index = 0
            };

            var firstSolution = BuildSolutionEmbed(session);
            var msg = await FollowupAsync(
                embeds: new[] { session.Header, firstSolution },
                components: BuildPagerComponents("TEMP")
            );

            var pagerKey = msg.Id.ToString();

            session = new PagerSession
            {
                OwnerUserId = session.OwnerUserId,
                ChannelId = session.ChannelId,
                MessageId = msg.Id,
                Header = session.Header,
                Pages = session.Pages,
                Index = session.Index
            };

            _pager[pagerKey] = session;
            await msg.ModifyAsync(m => m.Components = BuildPagerComponents(pagerKey));
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
        var tmpDir = Path.Combine(Path.GetTempPath(), "palcalc-discordbot");
        Directory.CreateDirectory(tmpDir);

        var safe = SanitizeFile(playerName);
        var outPath = Path.Combine(tmpDir, $"owned.{safe}.json");
        var tmpPath = Path.Combine(tmpDir, $"owned.{safe}.tmp");

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

        var fileLock = GetOwnedLock(safe);

        lock (fileLock)
        {
            File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            File.Move(tmpPath, outPath);
        }

        _log.LogInformation("Dump-owned OK: {Path} bytes={Bytes}", outPath, new FileInfo(outPath).Length);
        return outPath;
    }

    private static readonly ConcurrentDictionary<string, object> _ownedLocks = new(StringComparer.OrdinalIgnoreCase);
    private static object GetOwnedLock(string playerNameSafe)
        => _ownedLocks.GetOrAdd(playerNameSafe, _ => new object());

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
        var raw = CaptureBreedingRenderer(plan);
        var pretty = PrettyFormatBreedingPlan(raw);

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

    // =========================
    // Pretty formatter (TREE-BASED, COMPOSITE-safe)
    // =========================

    private sealed class LineNode
    {
        public int Id { get; init; }
        public int Indent { get; init; }
        public string Raw { get; init; } = "";
        public string Text { get; init; } = "";
        public List<LineNode> Children { get; } = new();

        public override string ToString() => $"{Indent}:{Text}";
    }

    private static List<LineNode> BuildIndentTree(List<string> rawLines)
    {
        var roots = new List<LineNode>();
        var stack = new Stack<LineNode>();
        int id = 0;

        foreach (var raw in rawLines)
        {
            var indent = CountIndent(raw);
            var txt = raw.TrimEnd();
            var node = new LineNode
            {
                Id = ++id,
                Indent = indent,
                Raw = raw,
                Text = txt.Trim()
            };

            while (stack.Count > 0 && indent <= stack.Peek().Indent)
                stack.Pop();

            if (stack.Count == 0)
                roots.Add(node);
            else
                stack.Peek().Children.Add(node);

            stack.Push(node);
        }

        return roots;
    }

    private static IEnumerable<LineNode> Walk(LineNode n)
    {
        yield return n;
        foreach (var c in n.Children)
            foreach (var x in Walk(c))
                yield return x;
    }

    private static IEnumerable<LineNode> Walk(IEnumerable<LineNode> roots)
    {
        foreach (var r in roots)
            foreach (var n in Walk(r))
                yield return n;
    }

    private static LineNode? FindChild(LineNode n, Func<LineNode, bool> pred)
        => n.Children.FirstOrDefault(pred);

    private static IEnumerable<LineNode> FindChildren(LineNode n, Func<LineNode, bool> pred)
        => n.Children.Where(pred);

    private string PrettyFormatBreedingPlan(string raw)
    {
        // normalize lines (keep indentation!)
        var rawLines = raw.Replace("\r\n", "\n")
            .Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.TrimEnd())
            .ToList();

        _passives.TryRefreshIfStale();

        var frByInternal = LoadPassiveLocFr();
        var db = PalDB.LoadEmbedded();
        var internalByEnglish = BuildInternalByEnglishName(db);

        var roots = BuildIndentTree(rawLines);

        var pals = new Dictionary<string, PalCard>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<BreedStep>();

        // 1) collect all nodes "- OWNED ..." / "- BRED ..." as cards
        foreach (var node in Walk(roots))
        {
            var t = node.Text;

            if (t.StartsWith("(COMPOSITE)", StringComparison.OrdinalIgnoreCase))
                continue;

            if (t.Contains("OPPOSITE_WILDCARD", StringComparison.OrdinalIgnoreCase) &&
                t.Contains("candidates=", StringComparison.OrdinalIgnoreCase))
            {
                // debug spam line, ignore
                continue;
            }

            if (t.StartsWith("- OWNED ", StringComparison.OrdinalIgnoreCase))
            {
                // ignore wrapper "(COMPOSITE)" own line as a pal card (it is not a real pal instance)
                if (t.StartsWith("- OWNED (COMPOSITE)", StringComparison.OrdinalIgnoreCase))
                    continue;

                var owned = ParseOwnedLine(t);
                var key = PalKey(owned.palName, owned.gender, node.Id);

                var loc = LocalizeLocation(owned.location);
                var header = $"{EmojiForPal(owned.palName)} **{owned.palName}** {FormatGender(owned.gender)}" +
                             $"{(string.IsNullOrWhiteSpace(loc) ? "" : $" — `{loc}`")}";

                UpsertPalCard(pals, key, header);

                var actual = ExtractActualFromNode(node, frByInternal, internalByEnglish);
                if (actual is { Count: > 0 })
                    pals[key].ActualPassives = actual;

                // store node->key mapping for later resolution
                _nodeKey[node.Id] = key;
            }
            else if (t.StartsWith("- BRED ", StringComparison.OrdinalIgnoreCase))
            {
                var info = ParseBredLine(t);
                var key = PalKey(info.palName, info.gender, node.Id);

                var header = $"{EmojiForPal(info.palName)} **{info.palName}** {FormatGender(info.gender)}{FormatBredMeta(info.steps, info.eggs, info.totalEggs)}";
                UpsertPalCard(pals, key, header);

                var actual = ExtractActualFromNode(node, frByInternal, internalByEnglish);
                if (actual is { Count: > 0 })
                    pals[key].ActualPassives = actual;

                _nodeKey[node.Id] = key;
            }
        }

        // 2) build steps by reading each "- BRED ..." node's "parents:"
        foreach (var bredNode in Walk(roots).Where(n => n.Text.StartsWith("- BRED ", StringComparison.OrdinalIgnoreCase)))
        {
            // output key
            if (!_nodeKey.TryGetValue(bredNode.Id, out var outKey))
                continue;

            var parentsNode = FindChild(bredNode, c => c.Text.Equals("parents:", StringComparison.OrdinalIgnoreCase));
            if (parentsNode == null)
                continue;

            var parentKeys = new List<string>(2);

            foreach (var entry in parentsNode.Children)
            {
                var resolved = ResolveParentKey(entry);
                if (resolved == null)
                    continue;

                parentKeys.Add(resolved);
                if (parentKeys.Count == 2)
                    break;
            }

            if (parentKeys.Count == 2)
                steps.Add(new BreedStep(parentKeys[0], parentKeys[1], outKey));
        }

        // 3) order steps ingredients -> target
        var ordered = TopoOrderSteps(steps);

        if (ordered.Count == 0)
            return "😶 Aucun step détecté (format renderer inattendu ?)";

        // 4) render
        var sb = new StringBuilder();
        sb.AppendLine("**Étapes (ingrédients → cible)**");
        sb.AppendLine();

        var producedAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < ordered.Count; i++)
        {
            var st = ordered[i];
            var stepNo = i + 1;

            sb.AppendLine($"**Étape {stepNo}**");

            AppendPalBlock(sb, pals, st.ParentAKey, producedAt, indentLevel: 1);
            AppendPalBlock(sb, pals, st.ParentBKey, producedAt, indentLevel: 1);

            var outCard = pals.TryGetValue(st.OutputKey, out var oc) ? oc : null;
            var outLabel = outCard?.ShortLabel ?? st.OutputKey;

            sb.AppendLine($"{Indent(1)}**= {pals[st.ParentAKey].ShortLabel} + {pals[st.ParentBKey].ShortLabel} → {outLabel}**");

            producedAt[st.OutputKey] = stepNo;

            if (outCard != null)
            {
                sb.AppendLine($"{Indent(1)}{outCard.HeaderLineRawWithoutEmoji ?? outCard.HeaderLine}");
                if (outCard.ActualPassives is { Count: > 0 })
                    sb.AppendLine($"{Indent(2)}{EmojiReal()} Passifs : **{string.Join(", ", outCard.ActualPassives)}**");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();

        // -------- local helpers (tree based) --------

        string? ResolveParentKey(LineNode entry)
        {
            // parent entry can be:
            // - OWNED ...
            // - BRED ...
            // - OWNED (COMPOSITE) ...  -> pick first real child OWNED/BRED
            var t = entry.Text;

            if (t.StartsWith("- OWNED (COMPOSITE)", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("(COMPOSITE)", StringComparison.OrdinalIgnoreCase))
            {
                // descend: find first descendant that is a real OWNED/BRED (NOT composite)
                foreach (var d in Walk(entry))
                {
                    if (d.Id == entry.Id) continue;

                    var dt = d.Text;
                    if (dt.StartsWith("- OWNED (COMPOSITE)", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (dt.StartsWith("- OWNED ", StringComparison.OrdinalIgnoreCase) ||
                        dt.StartsWith("- BRED ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_nodeKey.TryGetValue(d.Id, out var k))
                            return k;
                    }
                }

                return null;
            }

            if (t.StartsWith("- OWNED ", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("- BRED ", StringComparison.OrdinalIgnoreCase))
            {
                return _nodeKey.TryGetValue(entry.Id, out var k) ? k : null;
            }

            return null;
        }

        List<string>? ExtractActualFromNode(LineNode n,
            Dictionary<string, string> frByInternal2,
            Dictionary<string, string> internalByEnglish2)
        {
            // actual line is usually a child like: "actual : Diamond Body"
            foreach (var c in n.Children)
            {
                var tx = c.Text;
                if (!tx.StartsWith("actual", StringComparison.OrdinalIgnoreCase))
                    continue;

                var idx = tx.IndexOf(':');
                var val = (idx >= 0 ? tx[(idx + 1)..] : "").Trim();
                if (string.IsNullOrWhiteSpace(val))
                    return null;

                var tokens = val.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Select(x => LocalizePassiveToken(x, frByInternal2, internalByEnglish2))
                    .ToList();

                return tokens;
            }

            return null;
        }
    }

    // nodeId -> internal key (unique)
    private readonly Dictionary<int, string> _nodeKey = new();

    private sealed class PalCard
    {
        public string HeaderLine { get; set; } = "";
        public string ShortLabel { get; set; } = "";
        public List<string>? ActualPassives { get; set; }
        public string? HeaderLineRawWithoutEmoji { get; set; }
    }

    private sealed record BreedStep(string ParentAKey, string ParentBKey, string OutputKey);

    private static string PalKey(string palName, string? gender, int nodeId)
        => $"{palName}|{(string.IsNullOrWhiteSpace(gender) ? "?" : gender.ToUpperInvariant())}|{nodeId}";

    private void UpsertPalCard(Dictionary<string, PalCard> pals, string key, string header)
    {
        var parts = key.Split('|');
        var name = parts[0];
        var gender = parts.Length > 1 ? parts[1] : "?";
        var shortLabel = $"{name}{FormatGender(gender)}";

        if (!pals.TryGetValue(key, out var card))
        {
            card = new PalCard();
            pals[key] = card;
        }

        card.HeaderLine = header;
        card.ShortLabel = shortLabel;
        card.HeaderLineRawWithoutEmoji = header;
    }

    private static string FormatBredMeta(int? steps, int? eggs, int? totalEggs)
    {
        var bits = new List<string>();

        if (steps.HasValue) bits.Add($"{steps.Value} attempt(s)");
        if (eggs.HasValue) bits.Add($"~{eggs.Value}<:egg:1464196335430402122>");
        if (totalEggs.HasValue) bits.Add($"total ~{totalEggs.Value}<:egg:1464196335430402122>");

        return bits.Count == 0 ? "" : $"  ({string.Join(" • ", bits)})";
    }

    private static List<BreedStep> TopoOrderSteps(List<BreedStep> steps)
    {
        if (steps.Count <= 1) return steps;

        // outputKey -> step index
        var producer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < steps.Count; i++)
            if (!producer.ContainsKey(steps[i].OutputKey))
                producer[steps[i].OutputKey] = i;

        var indeg = new int[steps.Count];
        var adj = new List<int>[steps.Count];
        for (int i = 0; i < steps.Count; i++)
            adj[i] = new List<int>();

        // edge: if step j uses as input an output produced by step i, then i -> j
        for (int j = 0; j < steps.Count; j++)
        {
            var s = steps[j];
            foreach (var input in new[] { s.ParentAKey, s.ParentBKey })
            {
                if (!producer.TryGetValue(input, out var i))
                    continue;
                if (i == j) continue;

                adj[i].Add(j);
                indeg[j]++;
            }
        }

        var q = new Queue<int>();
        for (int i = 0; i < steps.Count; i++)
            if (indeg[i] == 0) q.Enqueue(i);

        var ordered = new List<BreedStep>(steps.Count);

        while (q.Count > 0)
        {
            var i = q.Dequeue();
            ordered.Add(steps[i]);

            foreach (var j in adj[i])
            {
                indeg[j]--;
                if (indeg[j] == 0)
                    q.Enqueue(j);
            }
        }

        return ordered.Count == steps.Count ? ordered : steps;
    }

    private static void AppendPalBlock(
        StringBuilder sb,
        Dictionary<string, PalCard> pals,
        string key,
        Dictionary<string, int> producedAt,
        int indentLevel)
    {
        if (!pals.TryGetValue(key, out var card))
        {
            sb.AppendLine($"{Indent(indentLevel)}• {key}");
            return;
        }

        var producedHint = producedAt.TryGetValue(key, out var stepNo)
            ? $" _(résultat étape {stepNo})_"
            : "";

        sb.AppendLine($"{Indent(indentLevel)}- {card.HeaderLine}{producedHint}");

        if (card.ActualPassives is { Count: > 0 })
            sb.AppendLine($"{Indent(indentLevel + 1)}{EmojiRealStatic()} Passifs : **{string.Join(", ", card.ActualPassives)}**");
    }

    private static string Indent(int level) => new string(' ', level * 2);

    private static string EmojiRealStatic() => "<:passive_rank_arrow_03:1464196763891138684>";

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
        var t = line.Substring("- BRED ".Length).Trim();

        int? eggs = TryParseIntAfter(t, "eggs");
        int? total = TryParseIntAfter(t, "totalEggs");
        int? steps = TryParseIntAfter(t, "steps");

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
        var i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += key.Length;

        if (i >= text.Length) return null;

        if (text[i] is '=' or '~' or ':') i++;

        int j = i;
        while (j < text.Length && char.IsDigit(text[j])) j++;

        if (j == i) return null;

        var slice = text.Substring(i, j - i);
        return int.TryParse(slice, out var v) ? v : null;
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

    private string EmojiReal() => "<:passive_rank_arrow_03:1464196763891138684>";
    private string EmojiInherit() => "<:passive_rank_arrow_04:1464197339559231572>";

    private string LocalizePassiveToken(
        string token,
        Dictionary<string, string> frByInternal,
        Dictionary<string, string> internalByEnglish)
    {
        token = token.Trim();

        if (token.Equals("(Random)", StringComparison.OrdinalIgnoreCase))
            return "🎲 aléatoire";

        if (frByInternal.TryGetValue(token, out var fr1))
            return fr1;

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
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ps in db.PassiveSkills)
        {
            if (string.IsNullOrWhiteSpace(ps.InternalName))
                continue;

            var en = (ps.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(en))
                continue;

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

        s = s.Replace("DimensionalPalStorage", "Stockage dimensionnel", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("Dimensional Pal Storage", "Stockage dimensionnel", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("GlobalPalBox", "Boîte à Pals globale", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("Party", "Équipe", StringComparison.OrdinalIgnoreCase);

        s = s.Replace("Palbox", "Boîte à Pals", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("MarketStall", "Marché aux puces", StringComparison.OrdinalIgnoreCase);

        s = Regex.Replace(s, @"\s+Onglet\s+", ", Onglet ", RegexOptions.IgnoreCase);
        return s;
    }

    // =========================
    // Pager
    // =========================

    private sealed class PagerSession
    {
        public ulong OwnerUserId { get; init; }
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public ulong ChannelId { get; init; }
        public ulong MessageId { get; init; }
        public Embed Header { get; init; } = default!;
        public List<string> Pages { get; init; } = new();
        public int Index { get; set; }
    }

    private static readonly ConcurrentDictionary<string, PagerSession> _pager = new();
    private static readonly TimeSpan PagerTtl = TimeSpan.FromMinutes(15);

    private static string TruncateForEmbed(string s, int max = 3800)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1) + "…";
    }

    private Embed BuildSolutionEmbed(PagerSession sess)
    {
        var idx = sess.Index;
        var title = $"Solution {idx + 1}/{sess.Pages.Count}";
        return new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(TruncateForEmbed(PreserveIndentForEmbed(sess.Pages[idx])))
            .WithColor(new Color(88, 101, 242))
            .Build();
    }

    private static MessageComponent BuildPagerComponents(string key)
    {
        var cb = new ComponentBuilder()
            .WithButton("◀", $"palcalc:prev:{key}", ButtonStyle.Secondary)
            .WithButton("▶", $"palcalc:next:{key}", ButtonStyle.Secondary)
            .WithButton("Fermer", $"palcalc:close:{key}", ButtonStyle.Danger);

        return cb.Build();
    }

    private bool IsExpired(PagerSession s)
        => (DateTime.UtcNow - s.CreatedUtc) > PagerTtl;

    [ComponentInteraction("palcalc:prev:*")]
    public async Task PagerPrevAsync(string pagerKey)
    {
        await DeferAsync();

        if (!_pager.TryGetValue(pagerKey, out var sess))
            return;

        if (IsExpired(sess))
        {
            _pager.TryRemove(pagerKey, out _);
            await ModifyPagerMessageAsync(sess, pagerKey, content: "⏳ Session expirée.", close: true);
            return;
        }

        if (Context.User.Id != sess.OwnerUserId)
        {
            await FollowupAsync("Pas ta navigation 🙂", ephemeral: true);
            return;
        }

        sess.Index = (sess.Index - 1 + sess.Pages.Count) % sess.Pages.Count;
        await ModifyPagerMessageAsync(sess, pagerKey);
    }

    [ComponentInteraction("palcalc:next:*")]
    public async Task PagerNextAsync(string pagerKey)
    {
        await DeferAsync();

        if (!_pager.TryGetValue(pagerKey, out var sess))
            return;

        if (IsExpired(sess))
        {
            _pager.TryRemove(pagerKey, out _);
            await ModifyPagerMessageAsync(sess, pagerKey, content: "⏳ Session expirée.", close: true);
            return;
        }

        if (Context.User.Id != sess.OwnerUserId)
        {
            await FollowupAsync("Pas ta navigation 🙂", ephemeral: true);
            return;
        }

        sess.Index = (sess.Index + 1) % sess.Pages.Count;
        await ModifyPagerMessageAsync(sess, pagerKey);
    }

    [ComponentInteraction("palcalc:close:*")]
    public async Task PagerCloseAsync(string pagerKey)
    {
        await DeferAsync();

        if (!_pager.TryGetValue(pagerKey, out var sess))
            return;

        if (Context.User.Id != sess.OwnerUserId)
        {
            await FollowupAsync("Pas ta session 🙂", ephemeral: true);
            return;
        }

        _pager.TryRemove(pagerKey, out _);
        await ModifyPagerMessageAsync(sess, pagerKey, content: "✅ Fermé.", close: true);
    }

    private async Task<IUserMessage?> GetPagerMessageAsync(PagerSession sess)
    {
        if (Context.Client.GetChannel(sess.ChannelId) is not IMessageChannel ch)
            return null;

        var m = await ch.GetMessageAsync(sess.MessageId);
        return m as IUserMessage;
    }

    private async Task<bool> ModifyPagerMessageAsync(PagerSession sess, string pagerKey, string? content = null, bool close = false)
    {
        var msg = await GetPagerMessageAsync(sess);
        if (msg == null) return false;

        await msg.ModifyAsync(m =>
        {
            m.Content = content;
            m.Embeds = close
                ? new[] { sess.Header }
                : new[] { sess.Header, BuildSolutionEmbed(sess) };

            m.Components = close
                ? new ComponentBuilder().Build()
                : BuildPagerComponents(pagerKey);
        });

        return true;
    }

    private static string PreserveIndentForEmbed(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        return Regex.Replace(
            s,
            @"(?m)^( +)",
            m => new string('\u00A0', m.Value.Length)
        );
    }

    private static int CountIndent(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }
}
