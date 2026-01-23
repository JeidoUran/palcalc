using Discord;
using Discord.Interactions;

namespace PalCalc.DiscordBot;

public sealed class PlayerAutocompleteHandler : AutocompleteHandler
{
    private readonly PlayerIndexService _players;

    public PlayerAutocompleteHandler(PlayerIndexService players)
        => _players = players;

    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction interaction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        // Kick refresh if stale (non bloquant)
        _players.TryRefreshIfStale();

        var value = (interaction.Data.Current?.Value as string ?? "").Trim();

        var cached = _players.GetCachedPlayersFast();

        var results = cached
            .Where(p => string.IsNullOrWhiteSpace(value)
                || p.Name.Contains(value, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(p => new AutocompleteResult(p.Name, p.Id));

        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}

