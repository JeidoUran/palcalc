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
        if (!File.Exists(path))
            throw new FileNotFoundException("Localization file not found", path);

        var json = File.ReadAllText(path);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var data = new LocalizationData
        {
            Pals = ReadMap(root, "pals"),
            Passives = ReadMap(root, "passives")
        };

        return data;
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
