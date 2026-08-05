using System.Text;

namespace SapphireAvenueAssistant.Relay;

public static class RelayText
{
    public static string? Normalize(string? value, int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var collapsed = string.Join(' ', value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var builder = new StringBuilder(collapsed.Length);
        var bytes = 0;
        foreach (var rune in collapsed.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                continue;
            }

            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > maximumUtf8Bytes)
            {
                break;
            }

            builder.Append(rune);
            bytes += runeBytes;
        }

        return builder.ToString().Trim() is { Length: > 0 } normalized
            ? normalized
            : null;
    }

    public static string EscapeDiscordMarkdown(string value)
    {
        const string metacharacters = "\\`*_~|";
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (metacharacters.Contains(character, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    public static string NormalizeIdentityKey(string value) =>
        value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    public static string DisplayNode(string characterName, string homeWorldName) =>
        $"{characterName} @ {homeWorldName}";
}
