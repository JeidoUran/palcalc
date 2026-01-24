using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using PalCalc.Model;

namespace PalCalc.DiscordBot;

public sealed class PalIndexService
{
    private readonly ILogger<PalIndexService> _log;
    private readonly IConfiguration _cfg;

    private readonly object _lock = new();
    private DateTime _lastOkLoadUtc = DateTime.MinValue;
    private List<PalEntry> _cached = new();
    private bool _refreshRunning = false;

    public PalIndexService(ILogger<PalIndexService> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    public IReadOnlyList<PalEntry> GetCachedPalsFast()
    {
        lock (_lock) return _cached;
    }

    public void TryRefreshIfStale()
    {
        var ttlSeconds = 24 * 3600; // 24h par défaut (ça change rarement)
        if (int.TryParse(_cfg["PalCalc:PalsCacheTtlSeconds"], out var ttlParsed) && ttlParsed > 0)
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
                // local file (tu peux configurer si tu veux)
                var locPath = _cfg["PalCalc:LocalizationFile"];
                if (string.IsNullOrWhiteSpace(locPath))
                    locPath = "localization.fr.json";
                var path = Path.Combine(AppContext.BaseDirectory, locPath);
                var loc = LocalizationLoader.Load(path);

                var db = PalDB.LoadEmbedded();

                var list = db.Pals
                    .Where(p => !string.IsNullOrWhiteSpace(p.InternalName))
                    .Select(p =>
                    {
                        var internalName = p.InternalName;

                        // si la loc ne connait pas, fallback sur internal
                        var name = LocalizationLoader.LocalizePal(loc, internalName);
                        if (string.IsNullOrWhiteSpace(name)) name = internalName;

                        return new PalEntry(internalName, name);
                    })
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                lock (_lock)
                {
                    _cached = list;
                    _lastOkLoadUtc = DateTime.UtcNow;
                }

                _log.LogInformation("Pals cache refreshed: {Count}", list.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Pals refresh failed (keeping previous cache)");
            }
            finally
            {
                lock (_lock) _refreshRunning = false;
            }
        });
    }
}
