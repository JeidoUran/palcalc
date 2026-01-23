using Discord;
using Discord.Interactions;

namespace PalCalc.DiscordBot;

public enum TargetGender
{
    Free,
    Male,
    Female
}

public class PalCalcModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly PlayerIndexService _players;
    private readonly PalIndexService _pals;

    public PalCalcModule(PlayerIndexService players, PalIndexService pals)
    {
        _players = players;
        _pals = pals;
    }

    [SlashCommand("palcalc", "Trouve des solutions de breeding à partir des données du serveur")]
    public async Task PalCalcAsync(
        [Autocomplete(typeof(PlayerAutocompleteHandler))]
        [Summary(description: "Joueur")]
        string player, // contiendra l'UID

        [Summary("target_pal", "Pal cible")]
        [Autocomplete(typeof(PalAutocompleteHandler))]
        string targetPalInternal,

        [Summary(description: "Genre cible")] TargetGender target_gender = TargetGender.Free,
        
        [Summary(description: "Passif #1")]
        [Autocomplete(typeof(PassiveAutocompleteHandler))]
        string? passive_1 = null,

        [Summary(description: "Passif #2")]
        [Autocomplete(typeof(PassiveAutocompleteHandler))]
        string? passive_2 = null,

        [Summary(description: "Passif #3")]
        [Autocomplete(typeof(PassiveAutocompleteHandler))]
        string? passive_3 = null,

        [Summary(description: "Passif #4")]
        [Autocomplete(typeof(PassiveAutocompleteHandler))]
        string? passive_4 = null,
        [Summary(description: "IV HP min (0-100)")] int iv_hp = 0,
        [Summary(description: "IV ATK min (0-100)")] int iv_atk = 0,
        [Summary(description: "IV DEF min (0-100)")] int iv_def = 0
    )
    {
        // IMPORTANT: on defer, car plus tard on fera snapshot+solve qui peut prendre plusieurs secondes
        await DeferAsync();

        var playerName = _players.GetCachedPlayersFast()
            .FirstOrDefault(p => p.Id == player)?.Name ?? player;

        var palName = _pals.GetCachedPalsFast()
            .FirstOrDefault(p => p.Id.Equals(targetPalInternal, StringComparison.OrdinalIgnoreCase))?.Name
            ?? targetPalInternal;

        // MVP: juste confirmer qu’on reçoit bien les params.
        var eb = new EmbedBuilder()
            .WithTitle("PalCalc")
            .WithDescription("Squelette OK — bientôt le solve 😄")
            .AddField("Player", playerName, true)
            .AddField("Target", $"{palName} ({target_gender})", true)
            .AddField("Passifs", string.Join(", ",
                new[] { passive_1, passive_2, passive_3, passive_4 }
                .Where(x => !string.IsNullOrWhiteSpace(x))), inline: false)
            .AddField("IV mins", $"HP {Clamp(iv_hp)}/100 • ATK {Clamp(iv_atk)}/100 • DEF {Clamp(iv_def)}/100", inline: false)
            .WithColor(new Color(88, 101, 242)); // discord blurple

        await FollowupAsync(embed: eb.Build());
    }

    private static int Clamp(int v) => v < 0 ? 0 : (v > 100 ? 100 : v);
}
