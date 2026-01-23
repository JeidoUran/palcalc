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
        // Refresh en arrière-plan si besoin (non bloquant)
        _passives.TryRefreshIfStale();

        var value = (interaction.Data.Current?.Value as string ?? "").Trim();

        var cached = _passives.GetCachedFast();

        var results = cached
            .Where(p =>
                string.IsNullOrWhiteSpace(value) ||
                p.Name.Contains(value, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(p =>
                // label = nom FR, value = internalName
                new AutocompleteResult(p.Name, p.Id)
            );

        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}
