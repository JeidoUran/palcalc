using System.Collections;
using System.Reflection;
using PalCalc.Model;
using PalCalc.Solver.PalReference;

public static class BreedingPlanRenderer
{
    public static void Print(IPalReference r, string indent = "")
    {
        switch (r)
        {
            case OwnedPalReference o:
            {
                Console.WriteLine($"{indent}- OWNED {o.Pal.Name} {o.Gender} [{FormatPalLocation(o.UnderlyingInstance.Location)}]");

                var target = FormatTargetPassives(o.EffectivePassives);

                var actualList = o.ActualPassives ?? o.UnderlyingInstance?.PassiveSkills;
                var actual = (actualList == null || actualList.Count == 0)
                    ? "Aucun"
                    : string.Join(", ", actualList.Select(p => p.Name));

                Console.WriteLine($"{indent}    target : {target}");
                Console.WriteLine($"{indent}    actual : {actual}");
                return;
            }

            case BredPalReference b:
            {
                Console.WriteLine($"{indent}- BRED {b.Pal.Name} {b.Gender} steps={b.NumTotalBreedingSteps} eggs~{b.AvgRequiredBreedings} totalEggs~{b.NumTotalEggs}");

                var target = FormatTargetPassives(b.EffectivePassives);
                Console.WriteLine($"{indent}    target : {target}");

                // optionnel : si tu veux afficher "actual" quand dispo
                var actualList = b.ActualPassives;
                if (actualList != null && actualList.Count > 0)
                    Console.WriteLine($"{indent}    actual : {string.Join(", ", actualList.Select(p => p.Name))}");

                Console.WriteLine($"{indent}    parents:");
                Print(b.Parent1, indent + "        ");
                Print(b.Parent2, indent + "        ");
                return;

            }

            default:
            {
                // 👇 gestion “souple” des types internes (CompositeOwnedPalReference etc.)
                var t = r.GetType();
                if (t.Name.Equals("CompositeOwnedPalReference", StringComparison.OrdinalIgnoreCase))
                {
                    PrintCompositeOwned(r, indent);
                    return;
                }

                Console.WriteLine($"{indent}- {r}");
                return;
            }
        }
    }

    private static void PrintCompositeOwned(object composite, string indent)
    {
        var t = composite.GetType();

        // On essaye de récupérer une collection de candidats (OwnedPalReference le plus souvent)
        var candidatesObj =
            GetProp(composite, "Candidates")
            ?? GetProp(composite, "Options")
            ?? GetProp(composite, "OwnedCandidates")
            ?? GetProp(composite, "UnderlyingCandidates");

        var candidates = ExtractAnyPalRefs(composite);

        // Quelques infos “haut niveau” si elles existent
        var palObj = GetProp(composite, "Pal");
        var palName = TryGetName(palObj) ?? palObj?.ToString() ?? "?";

        var genderObj = GetProp(composite, "Gender");
        var gender = genderObj?.ToString() ?? "?";

        var effPassivesObj = GetProp(composite, "EffectivePassives");
        var effNames = TryPassiveNames(effPassivesObj);

        foreach (var c in candidates.Take(8))
            Print(c, indent + "    ");
        if (candidates.Count > 8)
            Console.WriteLine($"{indent}    ... +{candidates.Count - 8} autres candidats");

        // On liste quelques candidats si possible
        Console.WriteLine($"{indent}- OWNED (COMPOSITE) {palName} {gender} candidates={candidates.Count}");

        int shown = 0;
        foreach (var c in candidates)
        {
            if (c is not IPalReference pr)
                continue;

            Print(pr, indent + "    ");
            shown++;

            if (shown >= 8)
                break;
        }

        if (candidates.Count > shown)
            Console.WriteLine($"{indent}    ... +{candidates.Count - shown} autres candidats");


        if (candidates.Count > shown)
            Console.WriteLine($"{indent}    ... +{candidates.Count - shown} autres candidats");
    }

    private static object? GetProp(object obj, string name)
        => obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(obj);

    private static List<object?> ToList(object? maybeEnumerable)
    {
        var list = new List<object?>();
        if (maybeEnumerable is IEnumerable e && maybeEnumerable is not string)
        {
            foreach (var x in e) list.Add(x);
        }
        return list;
    }

    private static string? TryGetName(object? palObj)
    {
        if (palObj == null) return null;
        var nameProp = palObj.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
        return nameProp?.GetValue(palObj)?.ToString();
    }

    private static string? TryPassiveNames(object? passivesObj)
    {
        if (passivesObj is not IEnumerable e || passivesObj is string) return null;
        var names = new List<string>();
        foreach (var p in e)
        {
            if (p == null) continue;
            var n = TryGetName(p) ?? p.ToString();
            if (!string.IsNullOrWhiteSpace(n)) names.Add(n!);
        }
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    static string FormatTargetPassives(IEnumerable<PassiveSkill> passives)
    {
        if (passives == null) return "Aucun";

        int random = 0;
        var named = new List<string>();

        foreach (var p in passives)
        {
            if (p is RandomPassiveSkill) random++;
            else if (!string.IsNullOrWhiteSpace(p.Name)) named.Add(p.Name);
            else named.Add(p.ToString() ?? "?");
        }

        if (named.Count == 0 && random == 0) return "Aucun";
        if (named.Count == 0) return $"🎲 aléatoire x{random}";
        return random > 0
            ? $"{string.Join(", ", named)} + 🎲 x{random}"
            : string.Join(", ", named);
    }

    static string FormatPalLocation(PalLocation loc)
    {
        if (loc == null) return "Unknown";

        if ((loc.Type == LocationType.Palbox || loc.Type == LocationType.DimensionalPalStorage) && loc.Index >= 0)
        {
            const int slotsPerTab = 30;
            var tab = (loc.Index / slotsPerTab) + 1;
            var pos = loc.Index % slotsPerTab;
            var x = (pos % 6) + 1;
            var y = (pos / 6) + 1;
            return $"{loc.Type} Onglet {tab} en ({x},{y})";
        }

        return loc.Index >= 0 ? $"{loc.Type} Slot {loc.Index}" : $"{loc.Type}";
    }
    
    static List<IPalReference> ExtractAnyPalRefs(object composite)
    {
        var t = composite.GetType();
        var hits = new List<IPalReference>();

        bool TryConsume(object? obj)
        {
            if (obj is null) return false;
            if (obj is string) return false;

            // direct
            if (obj is IPalReference pr)
            {
                hits.Add(pr);
                return true;
            }

            // enumerable
            if (obj is IEnumerable e)
            {
                bool any = false;
                foreach (var x in e)
                {
                    if (x is IPalReference pr2)
                    {
                        hits.Add(pr2);
                        any = true;
                    }
                }
                return any;
            }

            return false;
        }

        // 1) props (public + non-public)
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length != 0) continue;

            object? v = null;
            try { v = p.GetValue(composite); } catch { continue; }

            if (TryConsume(v))
                return hits;
        }

        // 2) fields (public + non-public)
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            object? v = null;
            try { v = f.GetValue(composite); } catch { continue; }

            if (TryConsume(v))
                return hits;
        }

        return hits;
    }
    
}
