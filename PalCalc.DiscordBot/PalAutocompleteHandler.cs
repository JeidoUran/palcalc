using Discord;
using Discord.Interactions;

namespace PalCalc.DiscordBot;

public sealed class PalAutocompleteHandler : AutocompleteHandler
{
    private readonly PalIndexService _pals;

    public PalAutocompleteHandler(PalIndexService pals)
        => _pals = pals;

    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction interaction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        _pals.TryRefreshIfStale();

        var value = (interaction.Data.Current?.Value as string ?? "").Trim();

        var cached = _pals.GetCachedPalsFast();

        // petit “boost” : startsWith d'abord, puis contains
        IEnumerable<PalEntry> hits;
        if (string.IsNullOrWhiteSpace(value))
        {
            hits = cached.Take(25);
        }
        else
        {
            hits =
                cached.Where(p => p.Name.StartsWith(value, StringComparison.OrdinalIgnoreCase))
                      .Concat(cached.Where(p => p.Name.Contains(value, StringComparison.OrdinalIgnoreCase)))
                      .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                      .Take(25);
        }

        var results = hits.Select(p => new AutocompleteResult(p.Name, p.Id));
        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}
