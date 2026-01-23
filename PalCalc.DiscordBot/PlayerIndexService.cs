using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PalCalc.SaveReader;
using System.Collections;
using System.IO;

using PalCalc.BotCLI;

namespace PalCalc.DiscordBot;

public sealed class PlayerIndexService
{
    private readonly ILogger<PlayerIndexService> _log;
    private readonly IConfiguration _cfg;

    private readonly object _lock = new();
    private DateTime _lastOkLoadUtc = DateTime.MinValue;
    private List<PlayerEntry> _cached = new();

    private bool _refreshRunning = false;

    public PlayerIndexService(ILogger<PlayerIndexService> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    public IReadOnlyList<PlayerEntry> GetCachedPlayersFast()
    {
        lock (_lock)
            return _cached;
    }

    public void TryRefreshIfStale()
    {
        var ttlSeconds = 300; // 5 min (ajuste)
        if (int.TryParse(_cfg["PalCalc:PlayersCacheTtlSeconds"], out var ttlParsed) && ttlParsed > 0)
            ttlSeconds = ttlParsed;

        lock (_lock)
        {
            var age = DateTime.UtcNow - _lastOkLoadUtc;
            if (_refreshRunning) return;
            if (_cached.Count > 0 && age.TotalSeconds <= ttlSeconds) return;

            _refreshRunning = true;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var saveDir = _cfg["PalCalc:SaveDir"];
                if (string.IsNullOrWhiteSpace(saveDir))
                    throw new InvalidOperationException("Missing PalCalc:SaveDir");

                var list = PalCalc.BotCLI.PlayerListReader.LoadPlayersFromLevel(saveDir)
                    .Select(x => new PlayerEntry(x.id, x.name))
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                lock (_lock)
                {
                    _cached = list;
                    _lastOkLoadUtc = DateTime.UtcNow;
                }

                _log.LogInformation("Players cache refreshed: {Count}", list.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Players refresh failed (keeping previous cache)");
            }
            finally
            {
                lock (_lock) _refreshRunning = false;
            }
        });
    }
}
