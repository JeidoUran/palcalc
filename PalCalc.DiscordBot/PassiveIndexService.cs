using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PalCalc.Model;
using System.Text.Json;

namespace PalCalc.DiscordBot;

public sealed class PassiveIndexService
{
    private readonly ILogger<PassiveIndexService> _log;
    private readonly IConfiguration _cfg;

    private readonly object _lock = new();
    private DateTime _lastOkLoadUtc = DateTime.MinValue;
    private List<PassiveEntry> _cached = new();
    private bool _refreshRunning = false;

    public PassiveIndexService(ILogger<PassiveIndexService> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    public IReadOnlyList<PassiveEntry> GetCachedFast()
    {
        lock (_lock) return _cached;
    }

    public void TryRefreshIfStale()
    {
        var ttlSeconds = 300; // 5 min
        if (int.TryParse(_cfg["PalCalc:PassivesCacheTtlSeconds"], out var ttlParsed) && ttlParsed > 0)
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
                // 1) PalDB (source des internal names)
                var db = PalDB.LoadEmbedded();

                // 2) Localization (source des labels FR)
                // -> on lit ton fichier existant. Ajuste le path si besoin (content root, etc.)
                var locPath = _cfg["PalCalc:LocalizationFile"] ?? "localization.fr.json";
                var loc = LoadLocalizationMap(locPath);

                // 3) Build list — on garde UNIQUEMENT ce qui est dans la loca (donc Pal passives only)
                var list = db.PassiveSkills
                    .Where(ps => !string.IsNullOrWhiteSpace(ps.InternalName))
                    .Where(ps => loc.ContainsKey(ps.InternalName!)) // <- le filtre clé
                    .Select(ps =>
                    {
                        var id = ps.InternalName!;
                        var name = loc.TryGetValue(id, out var fr) && !string.IsNullOrWhiteSpace(fr) ? fr : id;
                        return new PassiveEntry(id, name);
                    })
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();


                lock (_lock)
                {
                    _cached = list;
                    _lastOkLoadUtc = DateTime.UtcNow;
                }

                _log.LogInformation("Passives cache refreshed: {Count}", list.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Passives refresh failed (keeping previous cache)");
            }
            finally
            {
                lock (_lock) _refreshRunning = false;
            }
        });
    }

    private static Dictionary<string, string> LoadLocalizationMap(string path)
    {
        var json = File.ReadAllText(path);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // le fichier est structuré -> on cherche la section des passifs
        JsonElement passivesElement;

        if (root.TryGetProperty("passives", out passivesElement) ||
            root.TryGetProperty("passiveSkills", out passivesElement) ||
            root.TryGetProperty("PassiveSkills", out passivesElement))
        {
            if (passivesElement.ValueKind != JsonValueKind.Object)
                return new(StringComparer.OrdinalIgnoreCase);

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in passivesElement.EnumerateObject())
            {
                // value peut être string (attendu) ; si jamais c’est autre chose on ignore
                if (prop.Value.ValueKind == JsonValueKind.String)
                    dict[prop.Name] = prop.Value.GetString() ?? prop.Name;
            }

            return dict;
        }

        // fallback: pas trouvé
        return new(StringComparer.OrdinalIgnoreCase);
    }

}
