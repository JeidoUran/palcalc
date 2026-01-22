using System;
using System.Collections.Generic;
using System.Linq;
using PalCalc.Model;
using PalCalc.SaveReader.SaveFile;

static class DpsSupport
{
    public static List<QuickChar> ReadOwnedDpsPals(
        PalCalc.SaveReader.StandardSaveGame save,
        Guid targetPlayerGuid,
        string ownerName,
        Dictionary<Guid, QuickChar> byInstanceId,               // gardé pour compat signature (plus utilisé)
        Dictionary<string, List<QuickChar>> byCharacterId,      // gardé pour compat signature (plus utilisé)
        bool debug)
    {
        var result = new List<QuickChar>();

        // On trouve le PlayersSaveFile du joueur (celui dont PlayerMeta.PlayerId == target)
        foreach (var pf in save.Players)
        {
            var meta = pf.ReadPlayerContent();
            if (meta == null) continue;

            if (!Guid.TryParse(meta.PlayerId, out var mg) || mg != targetPlayerGuid) continue;

            var dps = pf.DimensionalPalStorageSaveFile;
            if (dps?.IsValid != true)
            {
                if (debug) Console.Error.WriteLine($"[dps] no dps file or invalid for player={targetPlayerGuid}");
                return result;
            }

            // IMPORTANT: ReadPals() hydrate en PalInstance (IV_* + PassiveSkills + Pal + Gender etc)
            // Le containerId DPS n'existe pas vraiment en jeu: PalCalc utilise un identifiant "virtuel" stable.
            var dpsContainerId = $"DPS:{targetPlayerGuid:D}";
            var data = dps.ReadPals(dpsContainerId);

            var pals = data?.Pals;
            if (pals == null || pals.Count == 0)
            {
                if (debug) Console.Error.WriteLine($"[dps] empty for player={targetPlayerGuid}");
                return result;
            }

            if (debug) Console.Error.WriteLine($"[dps] pals={pals.Count} for player={targetPlayerGuid}");

            // Chaque entrée de pals correspond à un slot DPS (ReadPals() force Location.Index = index réel)
            for (int i = 0; i < pals.Count; i++)
            {
                var p = pals[i];
                if (p == null) continue;
                if (p.Pal == null) continue;

                // InstanceId est une string dans PalInstance => on tente de la repasser en Guid? (comme QuickChar)
                Guid? instGuid = null;
                if (!string.IsNullOrWhiteSpace(p.InstanceId) && Guid.TryParse(p.InstanceId, out var g))
                    instGuid = g;

                // Sécurité: Location.Index devrait déjà être i, mais si jamais...
                var slotIndex = (p.Location != null && p.Location.Index >= 0) ? p.Location.Index : i;

                var qc = new QuickChar
                {
                    IsPlayer = false,

                    NickName = p.NickName,
                    CharacterId = p.Pal.InternalName, // ex: "Anubis"
                    InstanceId = instGuid,

                    Owner = targetPlayerGuid,

                    Level = p.Level,
                    Gender = p.Gender.ToString(),

                    ContainerId = null,
                    SlotIndex = slotIndex, // IMPORTANT: index réel du slot DPS

                    VirtualContainerKind = "DimensionalPalStorage",
                    VirtualContainerOwnerName = ownerName,

                    // Dans PalInstance, les "talents" sont des IV_*
                    TalentHp = p.IV_HP,
                    TalentMelee = p.IV_Melee,
                    TalentShot = p.IV_Shot,
                    TalentDefense = p.IV_Defense,

                    Passives = p.PassiveSkills?
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.InternalName))
                        .Select(x => x.InternalName)
                        .ToList()
                        ?? new()
                };

                result.Add(qc);
            }

            if (debug)
            {
                // Petit résumé utile
                var byPal = result
                    .Where(x => !string.IsNullOrWhiteSpace(x.CharacterId))
                    .GroupBy(x => x.CharacterId!)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => $"{g.Key}={g.Count()}");

                Console.Error.WriteLine($"[dps] kept={result.Count} topPals: " + string.Join(", ", byPal));
            }

            return result;
        }

        if (debug) Console.Error.WriteLine($"[dps] player not found in Players/*.sav for player={targetPlayerGuid}");
        return result;
    }
}
