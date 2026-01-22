using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;

using PalCalc.SaveReader;                // IFileSource + SingleFileSource (déjà dans le projet)
using PalCalc.SaveReader.FArchive;
using PalCalc.SaveReader.FArchive.Custom;
using PalCalc.SaveReader.GVAS;

using PalCalc.Model;
using PalCalc.SaveReader.SaveFile;
using PalCalc.SaveReader.SaveFile.Support.Level;

using PalCalc.Solver;
using PalCalc.Solver.ResultPruning;
using PalCalc.Solver.PalReference;

static class Program
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);

        var argsList = args.ToList();

        bool HasFlag(string name) => argsList.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        string? GetArg(string name)
        {
            var i = argsList.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= argsList.Count) return null;
            return argsList[i + 1];
        }

        if (argsList.Count == 0 || HasFlag("--help") || HasFlag("-h"))
        {
            PrintHelp();
            return 0;
        }

        string cmd = argsList[0];

        bool requiresSaveDir =
          cmd.Equals("list-players", StringComparison.OrdinalIgnoreCase) ||
          cmd.Equals("resolve-player", StringComparison.OrdinalIgnoreCase) ||
          cmd.Equals("dump-owned", StringComparison.OrdinalIgnoreCase);

        var saveDir = GetArg("--saveDir");
        var json = HasFlag("--json");
        var debug = HasFlag("--debug");

        if (requiresSaveDir)
        {
            if (string.IsNullOrWhiteSpace(saveDir))
            {
                Console.Error.WriteLine("Missing --saveDir");
                return 1;
            }

            var levelPath = Path.Combine(saveDir, "Level.sav");
            if (!File.Exists(levelPath))
            {
                Console.Error.WriteLine($"Level.sav not found: {levelPath}");
                return 2;
            }
        }

        try
        {

            if (cmd.Equals("debug-model", StringComparison.OrdinalIgnoreCase))
            {
                DebugModel();
                return 0;
            }

            if (cmd.Equals("list-players", StringComparison.OrdinalIgnoreCase))
            {
                var players = LoadPlayersFromLevel(saveDir);

                var payload = players
                    .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new { id = p.id, name = p.name })
                    .ToList();

                if (json) Console.WriteLine(JsonSerializer.Serialize(payload, JsonOpts));
                else foreach (var p in payload) Console.WriteLine($"{p.name} ({p.id})");

                return 0;
            }

            if (cmd.Equals("resolve-player", StringComparison.OrdinalIgnoreCase))
            {
                var playerName = GetArg("--player");
                if (string.IsNullOrWhiteSpace(playerName))
                {
                    Console.Error.WriteLine("Missing --player");
                    return 1;
                }

                var players = LoadPlayersFromLevel(saveDir);
                var match = players.FirstOrDefault(p => p.name.Equals(playerName, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrWhiteSpace(match.id))
                {
                    Console.Error.WriteLine($"Player not found: {playerName}");
                    return 2;
                }

                var savPath = Path.Combine(saveDir, "Players", PlayerGuidToFilename(match.id));
                Console.WriteLine(savPath);
                return 0;
            }

            if (cmd.Equals("dump-owned", StringComparison.OrdinalIgnoreCase))
            {
                var playerName = GetArg("--player");
                if (string.IsNullOrWhiteSpace(playerName))
                {
                    Console.Error.WriteLine("Missing --player");
                    return 1;
                }

                var owned = DumpOwned(saveDir, playerName, debug);

                if (json) Console.WriteLine(JsonSerializer.Serialize(owned, JsonOpts));
                else foreach (var o in owned) Console.WriteLine(JsonSerializer.Serialize(o, JsonOpts));

                return 0;
            }

            if (cmd.Equals("export-localization", StringComparison.OrdinalIgnoreCase))
            {
                var outPath = GetArg("--out");
                if (string.IsNullOrWhiteSpace(outPath))
                {
                    Console.Error.WriteLine("Missing --out");
                    return 1;
                }

                var lang = GetArg("--lang") ?? "fr";

                LocalizationExport.WriteLocalizationFile(outPath, lang, JsonOpts);

                if (!json)
                    Console.WriteLine($"Wrote: {outPath}");

                return 0;
            }

            if (cmd.Equals("solve-breed", StringComparison.OrdinalIgnoreCase))
            {
                RunSolveBreed(argsList);
                return 0;
            }

            Console.Error.WriteLine($"Unknown command: {cmd}");
            PrintHelp();
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error:");
            Console.Error.WriteLine(ex);
            return 10;
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
Usage:
  PalCalc.BotCLI list-players --saveDir "<worldSaveDir>" [--json]
  PalCalc.BotCLI resolve-player --saveDir "<worldSaveDir>" --player "<pseudo>"
  PalCalc.BotCLI dump-owned --saveDir "<worldSaveDir>" --player "<pseudo>" [--json] [--debug]
  PalCalc.BotCLI export-localization --out "<file.json>" [--lang fr]
  PalCalc.BotCLI solve-breed --ownedJson "<owned.json>" --target "<InternalName>" [--required "A,B"] [--optional "C,D"] [--minIvHp 0] [--minIvAtk 0] [--minIvDef 0] [--gender male|female|any]
  PalCalc.BotCLI debug-model

Examples:
  dotnet run --project PalCalc.BotCLI -- list-players --saveDir "C:\...\SaveGames\0\<WORLDID>" --json
  dotnet run --project PalCalc.BotCLI -- resolve-player --saveDir "C:\...\SaveGames\0\<WORLDID>" --player "Selene"
  dotnet run --project PalCalc.BotCLI -- dump-owned --saveDir "C:\...\SaveGames\0\<WORLDID>" --player "Selene" --json --debug
""");
    }

    // =====================================================================
    // list-players (basé sur Level.sav)
    // =====================================================================

    static List<(string id, string name)> LoadPlayersFromLevel(string saveDir)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var levelPath = Path.Combine(saveDir, "Level.sav");
        if (!File.Exists(levelPath))
            throw new FileNotFoundException("Level.sav not found", levelPath);

        var tmpDir = Path.Combine(saveDir, "_palcalc_tmp");
        Directory.CreateDirectory(tmpDir);

        var tmpLevel = Path.Combine(tmpDir, $"Level.snapshot.{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.sav");

        const int attempts = 5;
        Exception? last = null;

        for (int a = 0; a < attempts; a++)
        {
            try
            {
                File.Copy(levelPath, tmpLevel, overwrite: true);

                long s1 = new FileInfo(tmpLevel).Length;
                System.Threading.Thread.Sleep(150);
                long s2 = new FileInfo(tmpLevel).Length;

                if (s1 != s2 || s2 < 64)
                    throw new IOException($"Snapshot unstable (size {s1} -> {s2}). Server is probably writing.");

                CompressedSAV.WithDecompressedSave(tmpLevel, stream =>
                {
                    using var fa = new FArchiveReader(stream, PalWorldTypeHints.Hints, archivePreserve: true);
                    var gvas = GvasFile.FromFArchive(fa, []);

                    if (gvas.Properties == null || !gvas.Properties.TryGetValue("worldSaveData", out var worldProp))
                        return;

                    var worldDict = AsDict(GetValue(worldProp));
                    if (worldDict == null || !worldDict.TryGetValue("CharacterSaveParameterMap", out var mapProp))
                        return;

                    var mapVal = GetValue(mapProp);
                    if (mapVal is not IDictionary dict)
                        return;

                    foreach (DictionaryEntry de in dict)
                    {
                        var keyDict = AsDict(de.Key);
                        var uid = ExtractPlayerUidFromKeyDict(keyDict);

                        var valDict = AsDict(de.Value);
                        var (nickname, isPlayer) = ExtractNicknameAndIsPlayerFromValueDict(valDict);

                        if (!isPlayer) continue;
                        if (string.IsNullOrWhiteSpace(uid)) continue;
                        if (string.IsNullOrWhiteSpace(nickname)) continue;

                        if (!results.ContainsKey(uid))
                            results[uid] = nickname;
                    }
                });

                break; // success
            }
            catch (Exception ex)
            {
                last = ex;
                try { if (File.Exists(tmpLevel)) File.Delete(tmpLevel); } catch { }
                System.Threading.Thread.Sleep(200);
            }
        }

        try { if (File.Exists(tmpLevel)) File.Delete(tmpLevel); } catch { }

        if (results.Count == 0 && last != null)
            throw new Exception("Failed to read players from Level.sav snapshot after retries.", last);

        return results.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    static string? ExtractPlayerUidFromKeyDict(Dictionary<string, object>? keyDict)
    {
        if (keyDict == null) return null;
        if (!keyDict.TryGetValue("PlayerUId", out var puidObj)) return null;

        var puidVal = GetValue(puidObj);

        var direct = TryGetString(puidVal) ?? TryGetString(puidObj);
        if (IsGuidish(direct)) return NormalizeGuidish(direct!);

        var d = AsDict(puidVal) ?? AsDict(puidObj);
        if (d != null)
        {
            foreach (var k in new[] { "ID", "Id", "Guid", "Value" })
            {
                if (!d.TryGetValue(k, out var v)) continue;
                var s = TryGetString(GetValue(v) ?? v);
                if (IsGuidish(s)) return NormalizeGuidish(s!);
            }
        }

        var deep = FindInTree(puidObj, "ID") ?? FindInTree(puidObj, "PlayerUId") ?? FindInTree(puidObj, "Guid");
        var deepS = TryGetString(GetValue(deep) ?? deep);
        if (IsGuidish(deepS)) return NormalizeGuidish(deepS!);

        return null;
    }

    static (string? nickname, bool isPlayer) ExtractNicknameAndIsPlayerFromValueDict(Dictionary<string, object>? valDict)
    {
        if (valDict == null) return (null, false);
        if (!valDict.TryGetValue("RawData", out var rawObj) || rawObj == null)
            return (null, false);

        var rawDataDict =
            AsDict(GetProp(rawObj, "Data"))
            ?? AsDict(GetField(rawObj, "Data"));

        if (rawDataDict == null)
        {
            var dataNode = FindInTree(rawObj, "Data");
            rawDataDict = AsDict(GetValue(dataNode) ?? dataNode);
        }

        if (rawDataDict == null)
            return (null, false);

        if (!rawDataDict.TryGetValue("SaveParameter", out var saveParamObj) || saveParamObj == null)
            return (null, false);

        var saveParamVal = GetValue(saveParamObj) ?? saveParamObj;
        var spDict =
            AsDict(saveParamVal)
            ?? AsDict(GetProp(saveParamVal, "Value"))
            ?? AsDict(GetField(saveParamVal, "Value"));

        if (spDict == null)
        {
            var nnNode = FindInTree(saveParamObj, "NickName") ?? FindInTree(saveParamObj, "FilteredNickName");
            var ipNode = FindInTree(saveParamObj, "IsPlayer");
            var nn = TryGetString(GetValue(nnNode) ?? nnNode);
            var ip = TryGetBool(GetValue(ipNode) ?? ipNode);
            return (nn, ip);
        }

        spDict.TryGetValue("NickName", out var nnObj);
        spDict.TryGetValue("FilteredNickName", out var fnObj);
        spDict.TryGetValue("IsPlayer", out var ipObj);

        var nickname = TryGetString(nnObj) ?? TryGetString(fnObj);
        var isPlayer = TryGetBool(ipObj);

        return (nickname, isPlayer);
    }

    // =====================================================================
    // dump-owned
    // =====================================================================

    static List<object> DumpOwned(string worldDir, string playerName, bool debug)
    {
        var localization = LocalizationLoader.Load("localization.fr.json");
        var players = LoadPlayersFromLevel(worldDir);
        var target = players.FirstOrDefault(p => p.name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(target.id))
            throw new Exception($"Player not found: {playerName}");

        var playerGuid = Guid.Parse(target.id);

        // Snapshot stable (Level + Players + _dps)
        // On copie tout Players/*.sav pour garder le mapping party/palbox correct pour tous (pas cher, et fiable)
        var snapshotDir = StableWorldSnapshot.Create(worldDir, includeAllPlayers: true, onlyPlayerHex32NoDashesUpper: null, debug: debug);
        var save = new StandardSaveGame(snapshotDir);

        // Tu continues à lire le GVAS comme avant (ça te sert pour charsMap + locmap)
        var level = save.Level.ParseGvas(true);

        var charsMap = level
            .Collect(".worldSaveData.CharacterSaveParameterMap")
            .Cast<MapProperty>()
            .Single()
            .Value;

        var instanceLocationMap = BuildInstanceLocationMap(level);

        // Mapping fiable basé sur RawLevelSaveData + PlayerMeta (comme PalCalc UI)
        var containerInfoMap = BuildContainerInfoMap(save, debug);

        var instances = new List<QuickChar>();

        int nullInstanceIds = 0;
        int matchedLoc = 0;

        foreach (var kvp in charsMap)
        {
            var instanceId = ExtractInstanceIdFromCharMapKey(kvp.Key);
            if (!instanceId.HasValue) nullInstanceIds++;

            if (kvp.Value is not Dictionary<string, object> valueDict) continue;
            if (!valueDict.TryGetValue("RawData", out var rawDataObj)) continue;
            if (rawDataObj is not CharacterDataProperty cdp) continue;
            if (cdp.Data == null) continue;
            if (!cdp.Data.TryGetValue("SaveParameter", out var saveParamObj)) continue;
            if (saveParamObj is not StructProperty saveParamSp) continue;
            if (saveParamSp.Value is not Dictionary<string, object> sp) continue;

            bool isPlayer = sp.TryGetValue("IsPlayer", out var ipObj) && TryGetBool(ipObj);

            string? nickName = null;
            if (sp.TryGetValue("NickName", out var nnObj)) nickName = TryGetString(nnObj);
            if (string.IsNullOrWhiteSpace(nickName) && sp.TryGetValue("FilteredNickName", out var fnObj)) nickName = TryGetString(fnObj);

            string? characterId = sp.TryGetValue("CharacterID", out var cidObj) ? TryGetString(cidObj) : null;

            Guid? owner = null;
            if (sp.TryGetValue("OwnerPlayerUId", out var opObj))
            {
                var s = TryGetString(GetValue(opObj) ?? opObj);
                if (Guid.TryParse(s, out var g)) owner = g;
            }

            string? gender = sp.TryGetValue("Gender", out var gObj) ? TryGetString(gObj) : null;

            int levelInt = 1;
            if (sp.TryGetValue("Level", out var lvlObj))
                int.TryParse(TryGetString(lvlObj), out levelInt);

            Guid? containerId = null;
            int slotIndex = -1;

            if (instanceId.HasValue && instanceLocationMap.TryGetValue(instanceId.Value, out var loc))
            {
                containerId = loc.containerId;
                slotIndex = loc.slotIndex;
                matchedLoc++;
            }

            int? thp = ReadNullableInt(sp, "Talent_HP");
            int? tshot = ReadNullableInt(sp, "Talent_Shot");
            int? tmelee = ReadNullableInt(sp, "Talent_Melee");
            int? tdef = ReadNullableInt(sp, "Talent_Defense");

            var passives = new List<string>();
            if (sp.TryGetValue("PassiveSkillList", out var psObj))
            {
                if (psObj is ArrayProperty ap && ap.TypedMeta.ArrayType is "NameProperty" or "EnumProperty")
                    passives.AddRange(ap.StringValues.Where(x => !string.IsNullOrWhiteSpace(x)));
                else
                {
                    var node = FindInTree(psObj, "PassiveSkillList");
                    if (node is ArrayProperty ap2 && ap2.TypedMeta.ArrayType is "NameProperty" or "EnumProperty")
                        passives.AddRange(ap2.StringValues.Where(x => !string.IsNullOrWhiteSpace(x)));
                }
            }

            instances.Add(new QuickChar
            {
                IsPlayer = isPlayer,
                NickName = nickName,
                CharacterId = characterId,
                InstanceId = instanceId,
                Owner = owner,
                Gender = gender,
                Level = levelInt,
                ContainerId = containerId,
                SlotIndex = slotIndex,
                TalentHp = thp,
                TalentShot = tshot,
                TalentMelee = tmelee,
                TalentDefense = tdef,
                Passives = passives
            });
        }

        var owned = instances
            .Where(i => !i.IsPlayer)
            .Where(i => i.Owner.HasValue && i.Owner.Value == playerGuid)
            .ToList();

        var byInstanceId = instances
            .Where(i => i.InstanceId.HasValue)
            .GroupBy(i => i.InstanceId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var byCharacterId = instances
            .Where(i => !string.IsNullOrWhiteSpace(i.CharacterId))
            .GroupBy(i => i.CharacterId!)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Ajout DPS (Dimensional Pal Storage)
        var ownedDps = DpsSupport.ReadOwnedDpsPals(save, playerGuid, playerName, byInstanceId, byCharacterId, debug);

        if (debug)
        {
            Console.Error.WriteLine($"InstanceIds null: {nullInstanceIds}");
            Console.Error.WriteLine($"InstanceIds matched to locmap: {matchedLoc}");
            Console.Error.WriteLine($"LocMap size: {instanceLocationMap.Count}");
            Console.Error.WriteLine($"Owned count: {owned.Count}");
            Console.Error.WriteLine("Owned sample containerIds:");
            foreach (var p in owned.Take(10))
                Console.Error.WriteLine($"- {p.CharacterId} cid={p.ContainerId} slot={p.SlotIndex}");

            Console.Error.WriteLine("ContainerInfo sample:");
            foreach (var kv in containerInfoMap.Take(20))
                Console.Error.WriteLine($"- {kv.Key} => {kv.Value.Kind} max={kv.Value.MaxEntries?.ToString() ?? "?"} owner={kv.Value.OwnerName ?? kv.Value.OwnerId ?? "?"} base={kv.Value.BaseId ?? "-"} pos={(kv.Value.Pos.HasValue ? $"{kv.Value.Pos.Value.X:0.##},{kv.Value.Pos.Value.Y:0.##},{kv.Value.Pos.Value.Z:0.##}" : "-")}");

            var unknownOwned = owned
                .Where(p => p.ContainerId.HasValue && p.ContainerId.Value != Guid.Empty)
                .Where(p => GetContainerKind(p.ContainerId, containerInfoMap) == "Unknown")
                .ToList();

            if (unknownOwned.Count == 0)
            {
                Console.Error.WriteLine("[unknown] none (owned)");
            }
            else
            {
                Console.Error.WriteLine($"[unknown] ownedPals={unknownOwned.Count}");

                foreach (var grp in unknownOwned.GroupBy(p => p.ContainerId!.Value).OrderByDescending(g => g.Count()))
                {
                    var cid = grp.Key;

                    containerInfoMap.TryGetValue(cid, out var info);
                    Console.Error.WriteLine($"[unknown] container={cid} pals={grp.Count()} maxEntries={info?.MaxEntries?.ToString() ?? "?"}");

                    foreach (var ex in grp.OrderBy(p => p.SlotIndex).Take(8))
                        Console.Error.WriteLine($"    - charId={ex.CharacterId} inst={ex.InstanceId} slot={ex.SlotIndex} nick={ex.NickName}");
                }
            }
        }

        var output = new List<object>();
        var allOwned = owned.Concat(ownedDps).ToList();
        
        foreach (var pal in allOwned
            .OrderBy(p => !string.IsNullOrWhiteSpace(p.VirtualContainerKind) ? p.VirtualContainerKind : GetContainerKind(p.ContainerId, containerInfoMap))
            .ThenBy(p => p.ContainerId?.ToString() ?? "")
            .ThenBy(p => p.SlotIndex))
        {
            var loc = ResolveLocationText(pal, containerInfoMap);
            var kind = !string.IsNullOrWhiteSpace(pal.VirtualContainerKind)
                ? pal.VirtualContainerKind
                : GetContainerKind(pal.ContainerId, containerInfoMap);

            // Pal
            var displayPalName = LocalizationLoader.LocalizePal(localization, pal.CharacterId);

            // Passifs (exemple)
            var localizedPassives = pal.Passives.Select(p => new
            {
                id = p,
                name = LocalizationLoader.LocalizePassive(localization, p)
            });

            output.Add(new
            {
                nickname = pal.NickName,
                characterId = pal.CharacterId,
                displayName = displayPalName,
                instanceId = pal.InstanceId?.ToString(),
                level = pal.Level,
                gender = pal.Gender,
                owner = pal.Owner?.ToString(),

                containerId = pal.ContainerId?.ToString(),
                containerKind = kind,

                slotIndex = pal.SlotIndex,
                location = loc,

                iv = new { hp = pal.TalentHp, melee = pal.TalentMelee, shot = pal.TalentShot, def = pal.TalentDefense },
                passives = pal.Passives,
                displayPassives = localizedPassives
            });
        }

        return output;
    }

    static Dictionary<Guid, ContainerInfo> BuildContainerInfoMap(StandardSaveGame save, bool debug)
    {
        var raw = save.Level.ReadRawCharacterData();

        var playerMetas = save.Players
            .Select(pf => pf.ReadPlayerContent())
            .Where(pm => pm != null)
            .ToList()!;

        var playerNamesById = raw.Characters
            .Where(c => c.IsPlayer && c.PlayerId != null)
            .GroupBy(c => c.PlayerId!.ToString())
            .ToDictionary(
                g => g.Key,
                g => g.First().NickName ?? g.Key
            );

        var guildNamesById = raw.Groups
            .Where(g => !string.IsNullOrWhiteSpace(g.Id))
            .ToDictionary(
                g => g.Id!,
                g => g.Name ?? g.Id!
            );

        var map = new Dictionary<Guid, ContainerInfo>();

        if (debug)
        {
            Console.Error.WriteLine($"[players] metas={playerMetas.Count}");
            foreach (var pm in playerMetas.Take(10))
                Console.Error.WriteLine($"[players] id={pm.PlayerId} party={pm.PartyContainerId} palbox={pm.PalboxContainerId}");
        }

        foreach (var c in raw.ContainerContents)
        {
            if (string.IsNullOrWhiteSpace(c.Id)) continue;
            if (!Guid.TryParse(c.Id, out var cid))
                continue;

            var maxEntries = c.MaxEntries;

            // 1) Party ?
            var partyOwner = playerMetas.FirstOrDefault(p => p.PartyContainerId == c.Id);
            if (partyOwner != null)
            {
                var pid = partyOwner.PlayerId.ToString();
                map[cid] = new ContainerInfo
                {
                    Kind = "PlayerParty",
                    OwnerId = pid,
                    OwnerName = playerNamesById.GetValueOrDefault(pid, pid),
                    MaxEntries = maxEntries
                };
                continue;
            }

            // 2) Palbox ?
            var palboxOwner = playerMetas.FirstOrDefault(p => p.PalboxContainerId == c.Id);
            if (palboxOwner != null)
            {
                var pid = palboxOwner.PlayerId.ToString();
                map[cid] = new ContainerInfo
                {
                    Kind = "Palbox",
                    OwnerId = pid,
                    OwnerName = playerNamesById.GetValueOrDefault(pid, pid),
                    MaxEntries = maxEntries
                };
                continue;
            }

            // 3) Base ?
            var matchingBase = raw.Bases.FirstOrDefault(b => b.ContainerId.ToString() == c.Id);
            if (matchingBase != null)
            {
                var gid = matchingBase.OwnerGroupId.ToString();
                map[cid] = new ContainerInfo
                {
                    Kind = "Base",
                    BaseId = matchingBase.Id,
                    OwnerId = gid,
                    OwnerName = guildNamesById.GetValueOrDefault(gid, gid),
                    Pos = ((float)matchingBase.Position.x, (float)matchingBase.Position.y, (float)matchingBase.Position.z),
                    MaxEntries = maxEntries
                };
                continue;
            }

            // 4) Viewing cage ?
            var matchingCage = raw.MapObjects.FirstOrDefault(m =>
                m.ObjectId == GvasMapObject.ViewingCageObjectId &&
                m.PalContainerId != null &&
                m.PalContainerId.ToString() == c.Id);

            if (matchingCage != null)
            {
                var cageBase = raw.Bases.FirstOrDefault(b => matchingCage.OwnerBaseId.ToString() == b.Id);

                string? gid = (cageBase != null && cageBase.OwnerGroupId != Guid.Empty) ? cageBase.OwnerGroupId.ToString() : null;
                (float X, float Y, float Z)? pos = cageBase != null
                    ? ((float)cageBase.Position.x, (float)cageBase.Position.y, (float)cageBase.Position.z)
                    : null;

                map[cid] = new ContainerInfo
                {
                    Kind = "ViewingCage",
                    BaseId = cageBase?.Id ?? matchingCage.OwnerBaseId.ToString(),
                    OwnerId = gid,
                    OwnerName = gid == null ? null : guildNamesById.GetValueOrDefault(gid, gid),
                    Pos = pos,
                    MaxEntries = maxEntries
                };
                continue;
            }

            // 5) Si PalCalc ne remonte pas l’objectId de l’étal, fallback ciblé :
            //    Unknown + container size == 5 => Étal de vente (chez toi confirmé).
            if (maxEntries == 5 && c.NumEntries > 0)
            {
                map[cid] = new ContainerInfo
                {
                    Kind = "MarketStall",
                    MaxEntries = maxEntries
                };
                continue;
            }

            map[cid] = new ContainerInfo { Kind = "Unknown", MaxEntries = maxEntries };
        }

        if (debug)
        {
            var kinds = map.Values
                .GroupBy(m => m.Kind)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}={g.Count()}");
            Console.Error.WriteLine("[containers] " + string.Join(", ", kinds));
        }

        return map;
    }

    static string ResolveLocationText(Guid? containerId, int slotIndex, Dictionary<Guid, ContainerInfo> containers)
    {
        var cid = containerId ?? Guid.Empty;

        containers.TryGetValue(cid, out var info);

        string label = info?.Kind switch
        {
            "Palbox" => $"Boîte à Pals ({info.OwnerName ?? info.OwnerId ?? "?"})",
            "PlayerParty" => $"Équipe ({info.OwnerName ?? info.OwnerId ?? "?"})",
            "Base" => $"Base {info.BaseId ?? "?"} ({info.OwnerName ?? info.OwnerId ?? "?"})",
            "ViewingCage" => $"Cage d'observation (Base {info.BaseId ?? "?"})",
            "MarketStall" => "Marché aux puces",
            _ => "Inconnu"
        };

        if (info?.Pos is (var X, var Y, var Z))
            label += $" @ ({X:0.##},{Y:0.##},{Z:0.##})";

        // Palbox : Onglet + coords (x,y)
        if (info?.Kind == "Palbox" && slotIndex >= 0)
        {
            const int slotsPerTab = 30;

            var tab = (slotIndex / slotsPerTab) + 1;
            var posInTab = slotIndex % slotsPerTab;

            var x = (posInTab % 6) + 1;
            var y = (posInTab / 6) + 1;

            return $"{label}, Onglet {tab} en ({x},{y})";
        }

        if (slotIndex >= 0)
            return $"{label}, Slot {slotIndex}";

        return label;
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    static int? ReadNullableInt(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var obj) || obj == null) return null;
        var s = TryGetString(obj);
        if (int.TryParse(s, out var i)) return i;

        var v = GetValue(obj);
        if (v != null && int.TryParse(v.ToString(), out var j)) return j;

        return null;
    }

    static object? GetProp(object obj, string propName)
        => obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);

    static object? GetField(object obj, string fieldName)
        => obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);

    static object? GetValue(object? propLike)
    {
        if (propLike is null) return null;
        return GetProp(propLike, "Value") ?? GetField(propLike, "Value");
    }

    static Dictionary<string, object>? AsDict(object? o) => o as Dictionary<string, object>;

    static string? TryGetString(object? propLike)
    {
        if (propLike is null) return null;
        if (propLike is string s) return s;

        var v = GetValue(propLike);
        if (v is string vs) return vs;
        if (v != null) return v.ToString();

        return propLike.ToString();
    }

    static bool TryGetBool(object? propLike)
    {
        if (propLike is null) return false;
        if (propLike is bool b) return b;

        var v = GetValue(propLike);
        if (v is bool vb) return vb;
        if (v is int i) return i != 0;
        if (v is byte bt) return bt != 0;

        if (v is string s && bool.TryParse(s, out var bb)) return bb;

        return false;
    }

    static object? FindInTree(object? node, string wantedKey, int depth = 0)
    {
        if (node is null || depth > 60) return null;

        if (node is Dictionary<string, object> d)
        {
            if (d.TryGetValue(wantedKey, out var hit)) return hit;
            foreach (var kv in d)
            {
                var found = FindInTree(kv.Value, wantedKey, depth + 1);
                if (found is not null) return found;
            }
            return null;
        }

        var val = GetValue(node);
        if (val is not null)
        {
            var found = FindInTree(val, wantedKey, depth + 1);
            if (found is not null) return found;
        }

        if (node is IEnumerable e && node is not string)
        {
            foreach (var item in e)
            {
                var found = FindInTree(item, wantedKey, depth + 1);
                if (found is not null) return found;
            }
        }

        var t = node.GetType();
        if (!t.IsPrimitive && !t.IsEnum && t != typeof(string))
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                object? v = null;
                try { v = p.GetValue(node); } catch { continue; }

                if (p.Name.Equals(wantedKey, StringComparison.OrdinalIgnoreCase))
                    return v;

                var found = FindInTree(v, wantedKey, depth + 1);
                if (found is not null) return found;
            }
        }

        return null;
    }

    static bool IsGuidish(string? s)
        => !string.IsNullOrWhiteSpace(s) && Guid.TryParse(s.Trim(), out _);

    static string NormalizeGuidish(string s)
        => Guid.TryParse(s.Trim(), out var g) ? g.ToString("D") : s.Trim();

    static string PlayerGuidToFilename(string guid)
        => guid.Replace("-", "").ToUpperInvariant() + ".sav";

    static Guid? ExtractInstanceIdFromCharMapKey(object keyObj)
    {
        var node = FindInTree(keyObj, "InstanceId");
        if (node == null) return null;
        return TryExtractGuidDeep(node);
    }

    static Dictionary<Guid, (Guid containerId, int slotIndex)> BuildInstanceLocationMap(GvasFile level)
    {
        var map = new Dictionary<Guid, (Guid, int)>();

        var containers = level
            .Collect(".worldSaveData.CharacterContainerSaveData")
            .Cast<MapProperty>()
            .Single()
            .Value;

        int containersSeen = 0;
        int slotsSeen = 0;
        int instanceLinks = 0;

        foreach (var kvp in containers)
        {
            containersSeen++;

            var containerId = TryExtractGuidDeep(FindInTree(kvp.Key, "ID") ?? kvp.Key);
            if (!containerId.HasValue) continue;

            var valDict = kvp.Value as Dictionary<string, object>;
            if (valDict == null || !valDict.TryGetValue("Slots", out var slotsObj)) continue;
            if (slotsObj is not ArrayProperty slotsAp) continue;

            var slots = slotsAp.Values<object>()?.ToList();
            if (slots == null) continue;

            for (int i = 0; i < slots.Count; i++)
            {
                slotsSeen++;

                if (slots[i] is not Dictionary<string, object> slotDict) continue;

                slotDict.TryGetValue("IndividualId", out var indiv);

                var instanceId =
                    TryExtractGuidDeep(FindInTree(indiv, "InstanceId") ?? indiv)
                    ?? TryExtractGuidDeep(FindInTree(slotDict, "InstanceId") ?? slotDict);

                if (!instanceId.HasValue) continue;

                int slotIndex = i;
                if (slotDict.TryGetValue("SlotIndex", out var siObj))
                {
                    var siVal = GetValue(siObj) ?? siObj;
                    if (int.TryParse(siVal?.ToString(), out var parsed))
                        slotIndex = parsed;
                }

                if (!map.ContainsKey(instanceId.Value))
                {
                    map[instanceId.Value] = (containerId.Value, slotIndex);
                    instanceLinks++;
                }
            }
        }

        Console.Error.WriteLine($"[locmap] containersSeen={containersSeen}, slotsSeen={slotsSeen}, links={instanceLinks}");
        return map;
    }

    static Guid? TryExtractGuidDeep(object? node, int depth = 0)
    {
        if (node == null || depth > 80) return null;

        if (node is Guid g) return g;
        if (node is string s && Guid.TryParse(s.Trim(), out var gs)) return gs;

        var v = GetValue(node);
        if (v != null)
        {
            var gv = TryExtractGuidDeep(v, depth + 1);
            if (gv.HasValue) return gv;
        }

        if (node is Dictionary<string, object> d)
        {
            foreach (var k in new[] { "InstanceId", "ID", "Id", "Guid", "Value" })
            {
                if (d.TryGetValue(k, out var hit))
                {
                    var gh = TryExtractGuidDeep(hit, depth + 1);
                    if (gh.HasValue) return gh;
                }
            }

            foreach (var kv in d)
            {
                var gg = TryExtractGuidDeep(kv.Value, depth + 1);
                if (gg.HasValue) return gg;
            }
        }

        if (node is IEnumerable e && node is not string)
        {
            foreach (var item in e)
            {
                var gi = TryExtractGuidDeep(item, depth + 1);
                if (gi.HasValue) return gi;
            }
        }

        var n2 = FindInTree(node, "InstanceId") ?? FindInTree(node, "ID") ?? FindInTree(node, "Guid");
        if (n2 != null)
        {
            var gn2 = TryExtractGuidDeep(n2, depth + 1);
            if (gn2.HasValue) return gn2;
        }

        return null;
    }

    static string GetContainerKind(Guid? containerId, Dictionary<Guid, ContainerInfo> containers)
    {
        if (!containerId.HasValue || containerId.Value == Guid.Empty) return "Unknown";
        return containers.TryGetValue(containerId.Value, out var info) ? info.Kind : "Unknown";
    }

    static string ResolveLocationText(QuickChar pal, Dictionary<Guid, ContainerInfo> containers)
    {
        if (!string.IsNullOrWhiteSpace(pal.VirtualContainerKind))
        {
            if (pal.VirtualContainerKind == "DimensionalPalStorage")
            {
                var label = $"Dimensional Pal Storage ({pal.VirtualContainerOwnerName ?? "?"})";
                return FormatTabbedLocation(label, pal.SlotIndex);
            }

            // fallback générique
            return $"{pal.VirtualContainerKind} ({pal.VirtualContainerOwnerName ?? "?"}), Slot {pal.SlotIndex}";
        }

        return ResolveLocationText(pal.ContainerId, pal.SlotIndex, containers);
    }

    static string FormatTabbedLocation(string label, int slotIndex)
    {
        if (slotIndex < 0) return label;

        const int slotsPerTab = 30; // 6*5

        var tab = (slotIndex / slotsPerTab) + 1;
        var posInTab = slotIndex % slotsPerTab;

        var x = (posInTab % 6) + 1;
        var y = (posInTab / 6) + 1;

        return $"{label}, Onglet {tab} en ({x},{y})";
    }

    static void RunSolveBreed(List<string> argsList)
    {
        bool HasFlag(string name) => argsList.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        string? GetArg(string name)
        {
            var i = argsList.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= argsList.Count) return null;
            return argsList[i + 1];
        }

        var ownedJson = GetArg("--ownedJson");
        if (string.IsNullOrWhiteSpace(ownedJson) || !File.Exists(ownedJson))
            throw new Exception("Missing or invalid --ownedJson (file not found).");

        var targetInternal = GetArg("--target");
        if (string.IsNullOrWhiteSpace(targetInternal))
            throw new Exception("Missing --target (Pal internal name, ex: Bellanoir).");

        var requiredCsv = GetArg("--required") ?? "";
        var optionalCsv = GetArg("--optional") ?? "";

        int minIvHp  = int.TryParse(GetArg("--minIvHp"), out var a) ? a : 0;
        int minIvAtk = int.TryParse(GetArg("--minIvAtk"), out var b) ? b : 0;
        int minIvDef = int.TryParse(GetArg("--minIvDef"), out var c) ? c : 0;

        var genderArg = (GetArg("--gender") ?? "any").Trim().ToLowerInvariant();
        var requiredGender = genderArg switch
        {
            "male" => PalGender.MALE,
            "female" => PalGender.FEMALE,
            _ => PalGender.WILDCARD
        };

        var db = PalDB.LoadEmbedded();
        var gameSettings = new GameSettings();

        var palsByInternal = db.Pals.ToDictionary(p => p.InternalName, StringComparer.OrdinalIgnoreCase);
        var passivesByInternal = db.PassiveSkills.ToDictionary(p => p.InternalName, StringComparer.OrdinalIgnoreCase);

        if (!palsByInternal.TryGetValue(targetInternal, out var targetPal))
            throw new Exception($"Target pal not found in PalDB: {targetInternal}");

        List<PassiveSkill> ParsePassivesCsv(string csv)
        {
            var list = new List<PassiveSkill>();
            foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (passivesByInternal.TryGetValue(raw, out var ps))
                    list.Add(ps);
                else
                    Console.Error.WriteLine($"[warn] unknown passive: {raw}");
            }
            return list;
        }

        var requiredPassives = ParsePassivesCsv(requiredCsv);
        var optionalPassives = ParsePassivesCsv(optionalCsv);

        // 1) ownedInstances depuis json
        var ownedInstances = SolverMapping.LoadOwnedPalsFromOwnedJson(ownedJson, db);
        
        Console.Error.WriteLine($"[solve] ownedInstances={ownedInstances.Count}");

        // 2) Spec cible
        var spec = new PalCalc.Solver.PalSpecifier
        {
            Pal = targetPal,
            RequiredGender = requiredGender,
            IV_HP = minIvHp,
            IV_Attack = minIvAtk,
            IV_Defense = minIvDef,
            RequiredPassives = requiredPassives,
            OptionalPassives = optionalPassives
        };

        // 3) pruning rules
        // (ResultPruning est un namespace dans PalCalc.Solver chez toi, pas un assembly séparé)
        var pruning = PruningRulesBuilder.Default;

        // 4) settings (on garde ton style, mais en restant simple)
        // Si ta signature diffère, on la rend robuste via reflection ciblée.
        var solverSettings = CreateBreedingSolverSettings(
            db,
            gameSettings,
            ownedInstances,
            pruning
        );

        var solver = new BreedingSolver(solverSettings);

        var controller = new SolverStateController
        {
            CancellationToken = CancellationToken.None
        };

        solver.SolverStateUpdated += s =>
        {
            if (s.CurrentPhase == SolverPhase.Breeding)
                Console.Error.WriteLine($"[solve] step {s.CurrentStepIndex + 1}/{s.TargetSteps}");
        };

        var results = solver.SolveFor(spec, controller);

        var pruner = pruning.BuildAggregate(controller.CancellationToken);
        var cached = new CachedResultData(results);
        var pruned = pruner.Apply(results, cached).ToList();

        Console.WriteLine($"Found {results.Count} solutions (raw)");
        Console.WriteLine($"Found {pruned.Count} solutions (pruned)");

        Console.WriteLine($"Found {results.Count} solutions");

        if (pruned.Count > 0)

            for (int i = 0; i < pruned.Count; i++)
            {
                Console.WriteLine();
                Console.WriteLine($"=== Solution #{i + 1} ===");
                BreedingPlanRenderer.Print((IPalReference)pruned[i]);
            }
    }

    // Reflection robuste pour BreedingSolverSettings (au cas où la signature change)
    static BreedingSolverSettings CreateBreedingSolverSettings(
        PalDB db,
        GameSettings gameSettings,
        List<PalInstance> ownedInstances,
        object pruningRules
    )
    {
        // Valeurs "CLI Discord" (sans chirurgie, sans wild, permissif sur les passifs en trop)
        const int maxBreedingSteps = 10;          // profondeur breeding (UI)
        const int maxSolverIterations = 20;       // solver steps (UI)
        const int maxWildPals = 0;                // pas de wild pour ton bot
        const int maxInputIrrelevantPassives = 3; // IMPORTANT: garde les parents avec passifs en trop
        const int maxBredIrrelevantPassives = 1;  // important aussi pour filtrage final
        var maxEffort = TimeSpan.FromDays(1);   // très large (évite prune surprise)
        var maxThreads = Environment.ProcessorCount;

        const int maxSurgeryCost = 0;             // chirurgie OFF
        var allowedSurgeryPassives = new List<PassiveSkill>(); // chirurgie OFF
        const bool useGenderReversers = false;    // optionnel

        var allowedWildPals = new List<Pal>();    // inutile car maxWildPals=0
        var bannedBredPals = new List<Pal>();     // vide

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

            // Match sur TYPES “uniques”
            if (p.ParameterType == typeof(PalDB)) { args[i] = db; continue; }
            if (p.ParameterType == typeof(GameSettings)) { args[i] = gameSettings; continue; }
            if (p.ParameterType == typeof(List<PalInstance>)) { args[i] = ownedInstances; continue; }

            // pruningRules: on accepte l’assignabilité
            if (p.ParameterType.IsAssignableFrom(pruningRules.GetType())) { args[i] = pruningRules; continue; }

            // Match sur NOMS (les int sont ambigus sinon)
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

            // fallback (au cas où)
            if (p.HasDefaultValue) { args[i] = p.DefaultValue; continue; }
            if (p.ParameterType.IsValueType) { args[i] = Activator.CreateInstance(p.ParameterType); continue; }
            args[i] = null;
        }

        var settings = (BreedingSolverSettings)ctor.Invoke(args);

        Console.Error.WriteLine(
            $"[dbg] settings: " +
            $"MaxBreedingSteps={settings.MaxBreedingSteps}, " +
            $"MaxSolverIterations={settings.MaxSolverIterations}, " +
            $"MaxWildPals={settings.MaxWildPals}, " +
            $"MaxInputIrrelevantPassives={settings.MaxInputIrrelevantPassives}, " +
            $"MaxBredIrrelevantPassives={settings.MaxBredIrrelevantPassives}, " +
            $"MaxSurgeryCost={settings.MaxSurgeryCost}, " +
            $"UseGenderReversers={settings.UseGenderReversers}"
        );

        return settings;
    }


    static int TryGetCount(object? enumerable)
    {
        if (enumerable == null) return 0;
        if (enumerable is System.Collections.ICollection c) return c.Count;

        // fallback: enumerate
        int n = 0;
        if (enumerable is System.Collections.IEnumerable e)
            foreach (var _ in e) n++;
        return n;
    }

    static object? TryGetFirst(object? enumerable)
    {
        if (enumerable is System.Collections.IEnumerable e)
        {
            foreach (var x in e) return x;
        }
        return null;
    }


    static void DebugModel()
    {
        Console.WriteLine("=== Forcing load of PalCalc.Solver assemblies ===");
        TryLoadAssembly("PalCalc.Solver");
        TryLoadAssembly("PalCalc.Solver.ResultPruning");
        TryLoadAssembly("PalCalc.SaveReader");
        Console.WriteLine();
        Console.WriteLine("=== Assemblies loaded ===");
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
            Console.WriteLine($"- {a.GetName().Name}");

        Console.WriteLine();
        DumpType(typeof(PalCalc.Model.PalInstance));
        Console.WriteLine();
        DumpType(typeof(PalCalc.Model.PalLocation));
        Console.WriteLine();
        DumpEnum(typeof(LocationType));
        Console.WriteLine();

        // PalSpecifier (si dans Model)
        TryDumpBySimpleName("PalSpecifier");

        // Types solver “introuvables” : on les cherche dans toutes les assemblies
        Console.WriteLine();
        Console.WriteLine("=== Searching for solver types by name ===");
        foreach (var name in new[]
        {
            "PruningRulesBuilder",
            "BreedingSolverSettings",
            "BreedingSolver",
            "SolverStateController",
            "SolverPhase",
        })
            FindTypeAnywhere(name);

        Console.WriteLine();
        Console.WriteLine("=== Done ===");
    }

    static void DumpType(Type t)
    {
        Console.WriteLine($"=== Type: {t.FullName} ===");

        Console.WriteLine("-- Constructors:");
        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
        {
            var ps = c.GetParameters();
            var sig = string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}{(p.HasDefaultValue ? $"={p.DefaultValue ?? "null"}" : "")}"));
            Console.WriteLine($"  {t.Name}({sig})");
        }

        Console.WriteLine("-- Properties:");
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                          .OrderBy(p => p.Name))
        {
            var rw = $"{(p.CanRead ? "get" : "")}/{(p.CanWrite ? "set" : "no-set")}";
            Console.WriteLine($"  {p.PropertyType.Name} {p.Name} ({rw})");
        }

        Console.WriteLine("-- Fields:");
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                          .OrderBy(f => f.Name))
        {
            Console.WriteLine($"  {f.FieldType.Name} {f.Name}");
        }
    }

    static void TryDumpBySimpleName(string simpleName)
    {
        var t = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .FirstOrDefault(x => x.Name.Equals(simpleName, StringComparison.OrdinalIgnoreCase));

        if (t == null)
        {
            Console.WriteLine($"[warn] Type not found: {simpleName}");
            return;
        }

        DumpType(t);
    }

    static void FindTypeAnywhere(string simpleName)
    {
        var hits = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .Where(t => t.Name.Equals(simpleName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hits.Count == 0)
        {
            Console.WriteLine($"- {simpleName}: NOT FOUND");
            return;
        }

        foreach (var t in hits)
            Console.WriteLine($"- {simpleName}: {t.FullName}");
    }

    static void TryLoadAssembly(string name)
    {
        try
        {
            var a = System.Reflection.Assembly.Load(name);
            Console.WriteLine($"- loaded: {a.GetName().Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"- failed to load {name}: {ex.GetType().Name} {ex.Message}");
        }
    }

    static void DumpEnum(Type t)
    {
        if (!t.IsEnum) { Console.WriteLine($"{t.FullName} is not an enum"); return; }
        Console.WriteLine($"=== Enum: {t.FullName} ===");
        foreach (var n in Enum.GetNames(t))
            Console.WriteLine($"- {n}");
    }

    static void DumpObjectShallow(object o, int maxDepth = 2, int depth = 0)
    {
        if (o == null) return;
        var t = o.GetType();
        Console.WriteLine($"[{t.FullName}]");

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length != 0) continue;

            object? v = null;
            try { v = p.GetValue(o); } catch { continue; }

            if (v == null)
            {
                Console.WriteLine($"- {p.Name}: null");
                continue;
            }

            // évite de spam les gros objets
            var vt = v.GetType();
            if (vt == typeof(string) || vt.IsPrimitive || vt.IsEnum)
            {
                Console.WriteLine($"- {p.Name}: {v}");
            }
            else if (v is System.Collections.IEnumerable e && v is not string)
            {
                Console.WriteLine($"- {p.Name}: (enumerable) {vt.Name}");
                int i = 0;
                foreach (var item in e)
                {
                    if (i++ >= 5) { Console.WriteLine("  ..."); break; }
                    if (item == null) continue;
                    Console.WriteLine($"  - {item.GetType().Name}: {item}");
                }
            }
            else
            {
                Console.WriteLine($"- {p.Name}: {vt.Name}");
                if (depth + 1 < maxDepth)
                    DumpObjectShallow(v, maxDepth, depth + 1);
            }
        }
    }

}
