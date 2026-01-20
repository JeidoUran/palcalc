using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using PalCalc.SaveReader.FArchive.Custom;

using PalCalc.Model;
using PalCalc.SaveReader;
using PalCalc.SaveReader.FArchive;
using PalCalc.SaveReader.SaveFile;
using PalCalc.SaveReader.SaveFile.Support.Level;

using PalCalc.SaveReader.FArchive.Custom; // PalWorldTypeHints
using PalCalc.SaveReader.GVAS;           // GvasFile

static class Program
{
    static int Main(string[] args)
    {
        // Si ton BotCLI n'a pas Serilog configuré, tu peux commenter la ligne suivante.
        // Logging.InitCommonFull();

        var argsList = args.ToList();

        if (argsList.Count == 0 || HasFlag("--help") || HasFlag("-h"))
        {
            PrintHelp();
            return 0;
        }

        string cmd = argsList[0];

        string? GetArg(string name)
        {
            var i = argsList.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= argsList.Count) return null;
            return argsList[i + 1];
        }

        bool HasFlag(string name) => argsList.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        var saveDir = GetArg("--saveDir");
        var json = HasFlag("--json");
        var debug = HasFlag("--debug");

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

        try
        {
            if (cmd.Equals("list-players", StringComparison.OrdinalIgnoreCase))
            {
                var players = LoadPlayersFromLevel(saveDir);

                var payload = players
                    .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new { id = p.id, name = p.name })
                    .ToList();

                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                else
                    foreach (var p in payload) Console.WriteLine($"{p.name} ({p.id})");

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

                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(owned, new JsonSerializerOptions { WriteIndented = true }));
                else
                    foreach (var o in owned) Console.WriteLine(JsonSerializer.Serialize(o));

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

        // --- Snapshot safe : copie vers tmp + check taille stable ---
        var tmpDir = Path.Combine(saveDir, "_palcalc_tmp");
        Directory.CreateDirectory(tmpDir);

        var tmpLevel = Path.Combine(tmpDir, $"Level.snapshot.{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.sav");

        // on tente quelques fois au cas où le serveur écrit pile à ce moment-là
        const int attempts = 5;
        Exception? last = null;

        for (int a = 0; a < attempts; a++)
        {
            try
            {
                // Copie “classique” (si ça échoue, on retente)
                File.Copy(levelPath, tmpLevel, overwrite: true);

                // Vérifie que la taille ne bouge pas sur 2 lectures très proches
                long s1 = new FileInfo(tmpLevel).Length;
                System.Threading.Thread.Sleep(150);
                long s2 = new FileInfo(tmpLevel).Length;

                if (s1 != s2 || s2 < 64) // 64 = garde-fou débile
                    throw new IOException($"Snapshot unstable (size {s1} -> {s2}). Server is probably writing.");

                // --- Parse la COPIE ---
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

                // si on arrive ici : succès
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                try { if (File.Exists(tmpLevel)) File.Delete(tmpLevel); } catch { /* ignore */ }
                System.Threading.Thread.Sleep(200);
            }
        }

        // cleanup tmp (optionnel)
        try { if (File.Exists(tmpLevel)) File.Delete(tmpLevel); } catch { /* ignore */ }

        if (results.Count == 0 && last != null)
            throw new Exception("Failed to read players from Level.sav snapshot after retries.", last);

        return results.Select(kv => (kv.Key, kv.Value)).ToList();
    }


    static string? ExtractPlayerUidFromKeyDict(Dictionary<string, object>? keyDict)
    {
        if (keyDict == null) return null;
        if (!keyDict.TryGetValue("PlayerUId", out var puidObj)) return null;

        // StructProperty -> Value
        var puidVal = GetValue(puidObj);

        // cas direct
        var direct = TryGetString(puidVal) ?? TryGetString(puidObj);
        if (IsGuidish(direct)) return NormalizeGuidish(direct!);

        // cas dict
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

        // dernier recours
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

        // CharacterDataProperty.Data (dict)
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
    // dump-owned (basé sur StandardSaveGame + ParseGvas + Collect)
    // =====================================================================

    static List<object> DumpOwned(string worldDir, string playerName, bool debug)
    {
        var db = PalDB.LoadEmbedded();

        // 1) Resolve player
        var players = LoadPlayersFromLevel(worldDir);
        var target = players.FirstOrDefault(p => p.name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(target.id))
            throw new Exception($"Player not found: {playerName}");

        var playerGuid = Guid.Parse(target.id);

        // 2) Parse via StandardSaveGame (comme le CLI du repo)
        var save = new StandardSaveGame(worldDir);

        // Visiteur map objects (pour récupérer PalContainerId)
        var mapVis = new MapObjectVisitor(
            GvasMapObject.PalBoxObjectId,
            GvasMapObject.GlobalPalBoxObjectId,
            GvasMapObject.DimensionalPalStorageObjectId
        );

        // IMPORTANT : ParseGvas(true, visitor) => le visitor est exécuté pendant le parse
        var level = save.Level.ParseGvas(true, mapVis);

        // 3) Récupérer toutes les instances (players + pals) via Collect
        // Inspiré du snippet officiel :
        // level2.Collect(".worldSaveData.CharacterSaveParameterMap").Cast<MapProperty>().Single().Value;
        var charsMap = level
            .Collect(".worldSaveData.CharacterSaveParameterMap")
            .Cast<MapProperty>()
            .Single()
            .Value;

        var instances = new List<QuickChar>();

        foreach (var kvp in charsMap)
        {
            if (kvp.Value is not Dictionary<string, object> valueDict) continue;
            if (!valueDict.TryGetValue("RawData", out var rawDataObj)) continue;

            if (rawDataObj is not CharacterDataProperty cdp) continue;
            if (cdp.Data == null) continue;
            if (!cdp.Data.TryGetValue("SaveParameter", out var saveParamObj)) continue;

            if (saveParamObj is not StructProperty saveParamSp) continue;
            if (saveParamSp.Value is not Dictionary<string, object> sp) continue;

            bool isPlayer = sp.TryGetValue("IsPlayer", out var ipObj) && TryGetBool(ipObj);

            // NickName
            string? nickName = null;
            if (sp.TryGetValue("NickName", out var nnObj)) nickName = TryGetString(nnObj);
            if (string.IsNullOrWhiteSpace(nickName) && sp.TryGetValue("FilteredNickName", out var fnObj)) nickName = TryGetString(fnObj);

            // CharacterID (espèce)
            string? characterId = sp.TryGetValue("CharacterID", out var cidObj) ? TryGetString(cidObj) : null;

            // OwnerPlayerUId
            Guid? owner = null;
            if (sp.TryGetValue("OwnerPlayerUId", out var opObj))
            {
                var s = TryGetString(GetValue(opObj) ?? opObj);
                if (Guid.TryParse(s, out var g)) owner = g;
            }

            // Gender
            string? gender = sp.TryGetValue("Gender", out var gObj) ? TryGetString(gObj) : null;

            // Level
            int levelInt = 1;
            if (sp.TryGetValue("Level", out var lvlObj))
            {
                if (int.TryParse(TryGetString(lvlObj), out var tmp)) levelInt = tmp;
            }

            // SlotID => ContainerId + SlotIndex
            Guid? containerId = null;
            int slotIndex = -1;

            if (sp.TryGetValue("SlotID", out var slotObj) && slotObj is StructProperty slotSp && slotSp.Value is Dictionary<string, object> slotDict)
            {
                if (slotDict.TryGetValue("SlotIndex", out var siObj))
                {
                    if (int.TryParse(TryGetString(siObj), out var tmp)) slotIndex = tmp;
                }

                if (slotDict.TryGetValue("ContainerId", out var cIdObj) && cIdObj is StructProperty contSp && contSp.Value is Dictionary<string, object> contDict)
                {
                    if (contDict.TryGetValue("ID", out var idObj) && idObj is StructProperty idSp)
                    {
                        var s = idSp.Value?.ToString();
                        if (Guid.TryParse(s, out var g)) containerId = g;
                    }
                }
            }

            // IVs
            int? thp = ReadNullableInt(sp, "Talent_HP");
            int? tshot = ReadNullableInt(sp, "Talent_Shot");
            int? tmelee = ReadNullableInt(sp, "Talent_Melee");
            int? tdef = ReadNullableInt(sp, "Talent_Defense");

            // PassiveSkillList (array)
            var passives = new List<string>();
            if (sp.TryGetValue("PassiveSkillList", out var psObj))
            {
                // souvent ArrayProperty StringValues
                if (psObj is ArrayProperty ap && ap.TypedMeta.ArrayType is "NameProperty" or "EnumProperty")
                {
                    passives.AddRange(ap.StringValues.Where(x => !string.IsNullOrWhiteSpace(x)));
                }
                else
                {
                    // fallback: scan
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

        // 4) Labels des containers : MapObjects + Bases
        var containerLabels = BuildContainerLabels(mapVis);

        // 5) Filter owned pals
        var owned = instances
            .Where(i => !i.IsPlayer)
            .Where(i => i.Owner.HasValue && i.Owner.Value == playerGuid)
            .ToList();

        if (debug)
        {
            Console.Error.WriteLine($"Owned count: {owned.Count}");
            Console.Error.WriteLine("Owned sample containerIds:");
            foreach (var p in owned.Take(10))
                Console.Error.WriteLine($"- {p.CharacterId} cid={p.ContainerId} slot={p.SlotIndex}");

            Console.Error.WriteLine("Known container labels sample:");
            foreach (var kv in containerLabels.Take(20))
                Console.Error.WriteLine($"- {kv.Key} => {kv.Value}");
        }

        // 6) Output
        var output = new List<object>();

        foreach (var pal in owned.OrderBy(p => p.SlotIndex))
        {
            var loc = ResolveLocationText(pal.ContainerId, pal.SlotIndex, containerLabels);

            output.Add(new
            {
                nickname = pal.NickName,
                characterId = pal.CharacterId,
                level = pal.Level,
                gender = pal.Gender,
                owner = pal.Owner?.ToString(),
                slotIndex = pal.SlotIndex,
                location = loc,
                iv = new { hp = pal.TalentHp, melee = pal.TalentMelee, shot = pal.TalentShot, def = pal.TalentDefense },
                passives = pal.Passives
            });
        }

        return output;
    }

    static Dictionary<Guid, string> BuildContainerLabels(MapObjectVisitor mapVis)
    {
        var labels = new Dictionary<Guid, string>();

        foreach (var mo in mapVis.Result.Where(m => m.PalContainerId.HasValue))
        {
            var id = mo.PalContainerId!.Value;

            var label =
                mo.ObjectId == GvasMapObject.PalBoxObjectId ? "Boîte à Pals" :
                mo.ObjectId == GvasMapObject.GlobalPalBoxObjectId ? "Stockage global" :
                mo.ObjectId == GvasMapObject.DimensionalPalStorageObjectId ? "Stockage dimensionnel" :
                mo.ObjectId ?? "Inconnu";

            if (!labels.ContainsKey(id))
                labels[id] = label;
        }

        return labels;
    }

    static string ResolveLocationText(Guid? containerId, int slotIndex, Dictionary<Guid, string> containerLabels)
    {
        var cid = containerId ?? Guid.Empty;

        var label = "Inconnu";
        if (cid != Guid.Empty && containerLabels.TryGetValue(cid, out var l))
            label = l;

        // Grille palbox 6x5 => 30 slots par onglet
        const int slotsPerTab = 30;

        if (slotIndex < 0)
            return label;

        var tab = (slotIndex / slotsPerTab) + 1;
        var posInTab = slotIndex % slotsPerTab;

        var x = (posInTab % 6) + 1;
        var y = (posInTab / 6) + 1;

        return $"{label}, Onglet {tab} en ({x},{y})";
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    sealed class QuickChar
    {
        public bool IsPlayer;
        public string? NickName;
        public string? CharacterId;
        public Guid? Owner;
        public string? Gender;
        public int Level;

        public Guid? ContainerId;
        public int SlotIndex;

        public int? TalentHp;
        public int? TalentShot;
        public int? TalentMelee;
        public int? TalentDefense;

        public List<string> Passives = new();
    }

    static int? ReadNullableInt(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var obj) || obj == null) return null;
        var s = TryGetString(obj);
        if (int.TryParse(s, out var i)) return i;

        // certains props stockent l'int en .Value
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
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return Guid.TryParse(s.Trim(), out _);
    }

    static string NormalizeGuidish(string s)
    {
        if (Guid.TryParse(s.Trim(), out var g))
            return g.ToString("D");
        return s.Trim();
    }

    static string PlayerGuidToFilename(string guid)
        => guid.Replace("-", "").ToUpperInvariant() + ".sav";
}
