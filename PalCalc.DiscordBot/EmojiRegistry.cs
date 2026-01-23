using Discord;

namespace PalCalc.DiscordBot;

public sealed class EmojiRegistry
{
    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);
    public int Count => _byName.Count;

    public void LoadMentions(IEnumerable<(string name, string mention)> items)
    {
        _byName.Clear();
        foreach (var (name, mention) in items)
            _byName[name] = mention;
    }

    public string? TryGet(string name)
        => _byName.TryGetValue(name, out var s) ? s : null;
}

