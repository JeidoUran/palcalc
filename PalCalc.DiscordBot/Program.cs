using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.IO;

namespace PalCalc.DiscordBot;

public class Program
{
    public static async Task Main(string[] args)
    {
        var projectRoot = FindProjectRoot();

        using var host = Host.CreateDefaultBuilder(args)
            .UseContentRoot(projectRoot)
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.SetBasePath(projectRoot);
                cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                cfg.AddEnvironmentVariables(prefix: "PALCALC_");

                Console.WriteLine($"[CFG] ProjectRoot = {projectRoot}");
                Console.WriteLine($"[CFG] Looking for = {Path.Combine(projectRoot, "appsettings.json")}");
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddSingleton(new DiscordSocketConfig
                {
                    GatewayIntents = GatewayIntents.Guilds,
                    AlwaysDownloadUsers = false,
                    LogGatewayIntentWarnings = false,
                });

                services.AddSingleton<PlayerIndexService>();
                services.AddSingleton<PalIndexService>();
                services.AddSingleton<PassiveIndexService>();
                services.AddSingleton<DiscordSocketClient>();
                services.AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>()));
                services.AddSingleton<InteractionHandler>();

                services.AddLogging(b => b.AddConsole());
            })
            .Build();

        var handler = host.Services.GetRequiredService<InteractionHandler>();
        await handler.InitializeAsync();

        await host.RunAsync();
    }

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            var csproj = Path.Combine(dir.FullName, "PalCalc.DiscordBot.csproj");
            if (File.Exists(csproj))
                return dir.FullName;

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

public sealed class InteractionHandler
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly ILogger<InteractionHandler> _log;
    private readonly IConfiguration _cfg;
    private readonly PlayerIndexService _players;
    private readonly PalIndexService _pals;
    private readonly PassiveIndexService _passives;
    public InteractionHandler(
        DiscordSocketClient client,
        InteractionService interactions,
        IServiceProvider services,
        ILogger<InteractionHandler> log,
        IConfiguration cfg,
        PlayerIndexService players,
        PalIndexService pals,
        PassiveIndexService passives)
    {
        _client = client;
        _interactions = interactions;
        _services = services;
        _log = log;
        _cfg = cfg;
        _players = players;
        _pals = pals;
        _passives = passives;
    }

    public async Task InitializeAsync()
    {
        _client.Log += msg =>
        {
            _log.LogInformation("[Discord] {Severity} {Message}", msg.Severity, msg.Message);
            if (msg.Exception != null) _log.LogError(msg.Exception, "Discord exception");
            return Task.CompletedTask;
        };

        _interactions.Log += msg =>
        {
            _log.LogInformation("[Interactions] {Severity} {Message}", msg.Severity, msg.Message);
            if (msg.Exception != null) _log.LogError(msg.Exception, "Interactions exception");
            return Task.CompletedTask;
        };

        _client.Ready += OnReadyAsync;
        _client.InteractionCreated += OnInteractionAsync;

        await _interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), _services);

        var token = _cfg["Discord:Token"];
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Missing Discord:Token. Put it in appsettings.json or PALCALC_Discord__Token env var.");

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
    }

    private async Task OnReadyAsync()
    {

        var guildIdStr = _cfg["Discord:GuildId"];
        if (ulong.TryParse(guildIdStr, out var guildId) && guildId != 0)
        {
            await _interactions.RegisterCommandsToGuildAsync(guildId);
            _log.LogInformation("Slash commands registered to guild {GuildId}", guildId);
        }
        else
        {
            await _interactions.RegisterCommandsGloballyAsync();
            _log.LogInformation("Slash commands registered globally");
        }
        _log.LogInformation("Discord ready — warming up indexes");

        _players.TryRefreshIfStale();
        _pals.TryRefreshIfStale();
        _passives.TryRefreshIfStale();
    }

    private async Task OnInteractionAsync(SocketInteraction interaction)
    {
        try
        {
            var ctx = new SocketInteractionContext(_client, interaction);
            await _interactions.ExecuteCommandAsync(ctx, _services);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Interaction handling failed");

            if (interaction.Type is InteractionType.ApplicationCommand)
            {
                try { await interaction.RespondAsync("Oups, j’ai crash sur cette commande 😵", ephemeral: true); }
                catch { /* ignore */ }
            }
        }
    }
}
