// PalCalc.Debug - Program.cs
// Scanner "blindé" qui cherche une chaîne (ex: "Selene") dans un .sav Palworld (ex: Level.sav)
// Place ce fichier dans: C:\PalWorldServer\palcalc\PalCalc.Debug\Program.cs
// Puis run: dotnet run --project PalCalc.Debug

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PalCalc.SaveReader;
using PalCalc.SaveReader.FArchive;
using PalCalc.SaveReader.GVAS;
using PalCalc.SaveReader.SaveFile.Support.Level;

static class Program
{
    static void Main()
    {
        // ✅ Chemin de ton world save folder
        var worldDir = @"C:\PalWorldServer\Server\Pal\Saved\SaveGames\0\61007A364C52A926AB0123B5198297EA";

        // ✅ Fichier a scanner
        // - Essaye d'abord Level.sav (c'est le plus probable pour le mapping ID -> pseudo)
        var savPath = Path.Combine(worldDir, "Level.sav");

        // 🔎 La chaîne à chercher
        var needle = "Selene";

        if (!File.Exists(savPath))
        {
            Console.WriteLine($"Fichier introuvable: {savPath}");
            return;
        }

        Console.WriteLine($"Scanning: {savPath}");
        Console.WriteLine($"Needle : {needle}");
        Console.WriteLine();

        try
        {
            CompressedSAV.WithDecompressedSave(savPath, stream =>
            {
                using var fa = new FArchiveReader(stream, PalWorldTypeHints.Hints, archivePreserve: true);
                var gvas = GvasFile.FromFArchive(fa, []);

                Console.WriteLine("GVAS loaded. Scanning strings...\n");

                int hits = 0;

                void Hit(string path, string value)
                {
                    hits++;
                    Console.WriteLine($"{path} = \"{value}\"");
                }

                void Walk(object? obj, string path, int depth = 0)
                {
                    if (obj == null) return;

                    // Limite anti-boucles / explosions
                    if (depth > 14) return;

                    // Cas string direct
                    if (obj is string s)
                    {
                        if (s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                            Hit(path, s);
                        return;
                    }

                    // Dictionnaire typique PalCalc
                    if (obj is System.Collections.Generic.Dictionary<string, object> dict)
                    {
                        foreach (var kv in dict)
                            Walk(kv.Value, $"{path}.{kv.Key}", depth + 1);
                        return;
                    }

                    // Dictionnaires non-génériques
                    if (obj is System.Collections.IDictionary anyDict)
                    {
                        foreach (var key in anyDict.Keys)
                        {
                            object? v = null;
                            try { v = anyDict[key]; } catch { }
                            Walk(v, $"{path}[{key}]", depth + 1);
                        }
                        return;
                    }

                    // Collections
                    if (obj is System.Collections.IEnumerable enumerable && obj is not string)
                    {
                        int i = 0;
                        foreach (var item in enumerable)
                            Walk(item, $"{path}[{i++}]", depth + 1);
                        return;
                    }

                    // "Blind scan" via reflection: on explore toutes les propriétés publiques
                    var t = obj.GetType();

                    // Petit filtre: évite de refléter des types simples inutiles
                    if (t.IsPrimitive || t.IsEnum) return;

                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        // Ignore indexers
                        if (p.GetIndexParameters().Length != 0) continue;

                        object? v = null;
                        try { v = p.GetValue(obj); } catch { continue; }

                        Walk(v, $"{path}.{p.Name}", depth + 1);
                    }
                }

                // Point d'entrée du scan
                Walk(gvas, "gvas");

                Console.WriteLine();
                Console.WriteLine(hits == 0
                    ? "Aucun match trouvé. (Essaye aussi LocalData.sav / LevelMeta.sav si besoin.)"
                    : $"Done. Matches: {hits}");
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("Erreur pendant le scan :");
            Console.WriteLine(ex);
        }
    }
}
