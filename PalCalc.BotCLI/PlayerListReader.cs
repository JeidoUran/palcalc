using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

using PalCalc.SaveReader.FArchive;
using PalCalc.SaveReader.FArchive.Custom;
using PalCalc.SaveReader.GVAS;
using PalCalc.SaveReader.SaveFile.Support.Level;
using PalCalc.SaveReader;

namespace PalCalc.BotCLI;

public static class PlayerListReader
{
    public static List<(string id, string name)> LoadPlayersFromLevel(string saveDir)
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
                Thread.Sleep(150);
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
                Thread.Sleep(200);
            }
        }

        try { if (File.Exists(tmpLevel)) File.Delete(tmpLevel); } catch { }

        if (results.Count == 0 && last != null)
            throw new Exception("Failed to read players from Level.sav snapshot after retries.", last);

        return results.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // ===== Helpers copiés depuis Program.cs =====

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
}
