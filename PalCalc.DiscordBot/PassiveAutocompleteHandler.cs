using Discord;
using Discord.Interactions;

namespace PalCalc.DiscordBot;

public sealed class PassiveAutocompleteHandler : AutocompleteHandler
{
    private readonly PassiveIndexService _passives;

    public PassiveAutocompleteHandler(PassiveIndexService passives)
        => _passives = passives;

    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction interaction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        // refresh non-bloquant
        _passives.TryRefreshIfStale();

        var value = (interaction.Data.Current?.Value as string ?? "").Trim();

        // IMPORTANT: on filtre uniquement en mémoire
        var cached = _passives.GetCachedFast();

        var results = cached
            .Where(p =>
                string.IsNullOrWhiteSpace(value)
                || p.Name.Contains(value, StringComparison.OrdinalIgnoreCase)
                || p.Id.Contains(value, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(p => new AutocompleteResult(p.Name, p.Id));

        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}
