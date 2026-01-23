using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PalCalc.DiscordBot;

public static class EmojiNameNormalizer
{
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var s = input.ToLowerInvariant();

        // Normalisation unicode (accents)
        s = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length);

        foreach (var c in s)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        s = sb.ToString().Normalize(NormalizationForm.FormC);

        // Cas spéciaux
        s = s.Replace("œ", "oe");

        // Garde alphanum + espaces uniquement
        s = Regex.Replace(s, @"[^a-z0-9 ]", "");

        // Espaces -> underscore
        s = Regex.Replace(s, @"\s+", "_").Trim('_');

        return s;
    }
}

