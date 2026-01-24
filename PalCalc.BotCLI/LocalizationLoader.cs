using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Chargement des noms localisés (Pals + Passifs) depuis un JSON PalCalc-compatible
/// Exemple attendu : localization.fr.json
/// </summary>
public sealed class LocalizationData
{
    public Dictionary<string, string> Pals { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Passives { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class LocalizationLoader
{
    /// <summary>
    /// Charge un fichier de localisation PalCalc (ex: localization.fr.json)
    /// </summary>
    public static LocalizationData Load(string path)
    {
        // 1) si le path est relatif, on le résout d’abord par rapport au dossier du binaire
        var resolved = path;

        if (!Path.IsPathRooted(resolved))
        {
            // essaie à côté de l’exe
            var baseDir = AppContext.BaseDirectory;
            var candidate1 = Path.Combine(baseDir, path);
            if (File.Exists(candidate1))
                resolved = candidate1;
            else
            {
                // si on a passé un truc du style "PalCalc.BotCLI/localization.fr.json",
                // on tente juste le filename à côté de l’exe (cas publish)
                var fileName = Path.GetFileName(path);
                var candidate2 = Path.Combine(baseDir, fileName);
                if (File.Exists(candidate2))
                    resolved = candidate2;
            }
        }

        if (!File.Exists(resolved))
            throw new FileNotFoundException("Localization file not found", resolved);

        var json = File.ReadAllText(resolved);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new LocalizationData
        {
            Pals = ReadMap(root, "pals"),
            Passives = ReadMap(root, "passives")
        };
    }

    private static Dictionary<string, string> ReadMap(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return prop.EnumerateObject()
            .ToDictionary(
                p => p.Name,
                p => p.Value.GetString() ?? p.Name,
                StringComparer.OrdinalIgnoreCase
            );
    }

    // Helpers optionnels (confort)
    public static string LocalizePal(LocalizationData loc, string internalName)
        => loc.Pals.TryGetValue(internalName, out var name) ? name : internalName;

    public static string LocalizePassive(LocalizationData loc, string internalName)
        => loc.Passives.TryGetValue(internalName, out var name) ? name : internalName;
}
