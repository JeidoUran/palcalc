using PalCalc.Model;
using PalCalc.Solver.PalReference;

public static class BreedingPlanRenderer
{
    public static void Print(IPalReference r, string indent = "")
    {
        switch (r)
        {
            case OwnedPalReference o:
                Console.WriteLine($"{indent}- OWNED {o.Pal.Name} {o.Gender} [{FormatPalLocation(o.UnderlyingInstance.Location)}]");
                Console.WriteLine($"{indent}    eff : {(o.EffectivePassives.Count == 0 ? "no passives" : string.Join(", ", o.EffectivePassives.Select(p => p.Name)))}");
                return;

            case BredPalReference b:
                Console.WriteLine($"{indent}- BRED {b.Pal.Name} {b.Gender} steps={b.NumTotalBreedingSteps} eggs~{b.AvgRequiredBreedings} totalEggs~{b.NumTotalEggs}");
                Console.WriteLine($"{indent}    passives: {(b.EffectivePassives.Count == 0 ? "none" : string.Join(", ", b.EffectivePassives.Select(p => p.Name)))}");
                Console.WriteLine($"{indent}    parents:");
                Print(b.Parent1, indent + "        ");
                Print(b.Parent2, indent + "        ");
                return;

            default:
                Console.WriteLine($"{indent}- {r}");
                return;
        }
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
}
