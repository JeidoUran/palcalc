using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using PalCalc.Model;

internal static class SolverMapping
{
    private sealed class OwnedJsonEntry
    {
        public string? characterId { get; set; }
        public string? gender { get; set; }
        public string? containerKind { get; set; }
        public IvObj? iv { get; set; }
        public List<string>? passives { get; set; }

        public string? containerId { get; set; }
        public int? slotIndex { get; set; }

        public sealed class IvObj
        {
            public int? hp { get; set; }
            public int? shot { get; set; } // => IV_Shot
            public int? def { get; set; }
            public int? melee { get; set; }
        }
    }

    public static List<PalInstance> LoadOwnedPalsFromOwnedJson(string ownedJsonPath, PalDB db)
    {
        var raw = File.ReadAllText(ownedJsonPath);

        // Tolérance: si le fichier contient du bruit avant le JSON (logs, chemins, etc.)
        var startArr = raw.IndexOf('[');
        var startObj = raw.IndexOf('{');
        int start;

        if (startArr >= 0 && startObj >= 0) start = Math.Min(startArr, startObj);
        else start = Math.Max(startArr, startObj);

        if (start > 0)
            raw = raw.Substring(start);

        List<OwnedJsonEntry> entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<OwnedJsonEntry>>(raw) ?? new();
        }
        catch (JsonException ex)
        {
            var head = new string(raw.Take(60).ToArray());
            throw new Exception($"Invalid ownedJson format: {ownedJsonPath}. First chars: '{head}'", ex);
        }


        var palsByInternal = db.Pals.ToDictionary(p => p.InternalName, StringComparer.OrdinalIgnoreCase);
        var passivesByInternal = db.PassiveSkills.ToDictionary(p => p.InternalName, StringComparer.OrdinalIgnoreCase);

        var result = new List<PalInstance>();

        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.characterId)) continue;
            if (string.Equals(e.characterId, "None", StringComparison.OrdinalIgnoreCase)) continue;

            if (!palsByInternal.TryGetValue(e.characterId, out var pal))
                continue;

            var gender = ParseGender(e.gender);

            var ivHp    = e.iv?.hp ?? 0;
            var ivShot  = e.iv?.shot ?? 0;      // IMPORTANT
            var ivDef   = e.iv?.def ?? 0;
            var ivMelee = e.iv?.melee ?? 0;

            var passiveSkills = new List<PassiveSkill>();
            if (e.passives != null)
            {
                foreach (var pid in e.passives)
                {
                    if (string.IsNullOrWhiteSpace(pid)) continue;
                    if (passivesByInternal.TryGetValue(pid, out var ps))
                        passiveSkills.Add(ps);
                }
            }

            var location = CreateLocation(e.containerKind, e.containerId, e.slotIndex);

            // PalInstance est mutable, ctor vide, donc initializer OK.
            var inst = new PalInstance
            {
                Pal = pal,
                Gender = gender,
                PassiveSkills = passiveSkills,
                Location = location,

                IV_HP = ivHp,
                IV_Shot = ivShot,         // <<<<< pas IV_Attack
                IV_Defense = ivDef,
                IV_Melee = ivMelee,

                Level = 1,
                Rank = 0
            };

            result.Add(inst);
        }

        return result;
    }

    static PalGender ParseGender(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return PalGender.WILDCARD; // ou NONE, mais WILDCARD est souvent “moins dangereux” côté algo

        raw = raw.Trim();

        // Format ancien: "(EPalGenderType)EPalGenderType::Female"
        if (raw.Contains("Female", StringComparison.OrdinalIgnoreCase)) return PalGender.FEMALE;
        if (raw.Contains("Male", StringComparison.OrdinalIgnoreCase)) return PalGender.MALE;

        // Format nouveau: "FEMALE" / "MALE"
        if (raw.Equals("FEMALE", StringComparison.OrdinalIgnoreCase)) return PalGender.FEMALE;
        if (raw.Equals("MALE", StringComparison.OrdinalIgnoreCase)) return PalGender.MALE;

        // parfois "EPalGenderType::Female" ou juste "Female"
        if (raw.Equals("Female", StringComparison.OrdinalIgnoreCase)) return PalGender.FEMALE;
        if (raw.Equals("Male", StringComparison.OrdinalIgnoreCase)) return PalGender.MALE;

        return PalGender.WILDCARD; // fallback safe
    }

    private static PalLocation CreateLocation(string? containerKind, string? containerId, int? slotIndex)
    {
        return new PalLocation
        {
            ContainerId = containerId ?? "",
            Index = slotIndex ?? -1,
            Type = containerKind switch
            {
                "PlayerParty" => LocationType.PlayerParty,
                "Palbox" => LocationType.Palbox,
                "Base" => LocationType.Base,
                "ViewingCage" => LocationType.ViewingCage,
                "DimensionalPalStorage" => LocationType.DimensionalPalStorage,
                "GlobalPalStorage" => LocationType.GlobalPalStorage,
                _ => LocationType.Custom
            }
        };
    }

}
