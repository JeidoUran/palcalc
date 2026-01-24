using System.Text;

namespace PalCalc.DiscordBot;

public sealed class ConsoleSpamFilterWriter : TextWriter
{
    private readonly TextWriter _inner;
    public override Encoding Encoding => _inner.Encoding;

    public ConsoleSpamFilterWriter(TextWriter inner) => _inner = inner;

    public override void WriteLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _inner.WriteLine(value);
            return;
        }

        // Filtre hyper ciblé : on ne droppe QUE ces messages
        if (value.StartsWith("Struct type for ", StringComparison.Ordinal) &&
            value.Contains(" not found, assuming ", StringComparison.Ordinal))
        {
            return;
        }

        _inner.WriteLine(value);
    }

    public override void Write(char value) => _inner.Write(value);
    public override void Write(string? value) => _inner.Write(value);
    public override void WriteLine() => _inner.WriteLine();
}
