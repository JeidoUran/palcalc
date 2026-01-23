using System.Net.Http.Headers;
using System.Text.Json;

namespace PalCalc.DiscordBot;

public static class ApplicationEmojiLoader
{
    // Discord API base
    private static readonly Uri BaseUri = new("https://discord.com/api/v10/");

    public static async Task<List<(string name, string mention)>> LoadAsync(
        string botToken,
        ulong applicationId)
    {
        using var http = new HttpClient { BaseAddress = BaseUri };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", botToken);

        // GET /applications/{application.id}/emojis
        var resp = await http.GetAsync($"applications/{applicationId}/emojis");
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // format attendu: { "items": [ { "id": "...", "name": "...", "animated": bool }, ... ] }
        var itemsEl = root.TryGetProperty("items", out var itemsProp) ? itemsProp : default;
        if (itemsEl.ValueKind != JsonValueKind.Array)
            return new();

        var result = new List<(string name, string mention)>();

        foreach (var e in itemsEl.EnumerateArray())
        {
            var name = e.GetProperty("name").GetString();
            var id = e.GetProperty("id").GetString();
            var animated = e.TryGetProperty("animated", out var animProp) && animProp.ValueKind == JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
                continue;

            var mention = animated
                ? $"<a:{name}:{id}>"
                : $"<:{name}:{id}>";

            result.Add((name!, mention));
        }

        return result;
    }
}
