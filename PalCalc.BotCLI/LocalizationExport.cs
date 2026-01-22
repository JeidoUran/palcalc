using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using PalCalc.Model;

static class LocalizationExport
{
    public static object BuildLocalizationPayload(string lang)
    {
        // Charge la DB embarquée de PalCalc (db.json)
        var db = PalDB.LoadEmbedded();

        lang = string.IsNullOrWhiteSpace(lang) ? "fr" : lang.Trim();

        static string PickLocalized(Dictionary<string, string>? dict, string lang, string fallback)
        {
            if (dict != null)
            {
                if (dict.TryGetValue(lang, out var hit) && !string.IsNullOrWhiteSpace(hit))
                    return hit;

                // fallback raisonnable si la langue demandée n’existe pas
                if (dict.TryGetValue("en", out var en) && !string.IsNullOrWhiteSpace(en))
                    return en;
            }
            return fallback;
        }

        // Pals: InternalName -> Nom localisé
        var pals = db.Pals
            .Where(p => !string.IsNullOrWhiteSpace(p.InternalName))
            .ToDictionary(
                p => p.InternalName!,
                p => PickLocalized(p.LocalizedNames, lang, p.Name ?? p.InternalName!)
            );

        // Passifs: InternalName -> Nom localisé
        var passives = db.PassiveSkills
            .Where(ps => !string.IsNullOrWhiteSpace(ps.InternalName))
            .ToDictionary(
                ps => ps.InternalName!,
                ps => PickLocalized(ps.LocalizedNames, lang, ps.Name ?? ps.InternalName!)
            );

        return new
        {
            version = db.Version,
            lang = lang,
            pals = pals,
            passives = passives
        };
    }

    public static void WriteLocalizationFile(string outPath, string lang, JsonSerializerOptions jsonOpts)
    {
        var payload = BuildLocalizationPayload(lang);

        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(outPath, JsonSerializer.Serialize(payload, jsonOpts));
    }
}
