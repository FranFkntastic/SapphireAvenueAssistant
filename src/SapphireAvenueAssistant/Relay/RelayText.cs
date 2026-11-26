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

    // Mirrors SapphireAvenueRelay.CwlsChannels.FormatDiscordLine: the plugin prefixes every
    // game-bound line with "[Discord · Name] " (name capped at 80 UTF-8 bytes) inside a
    // 494-byte budget and silently cuts whatever does not fit.
    public const int GameLineByteBudget = 494;

    public static string FormatGameLine(string displayName, string content) =>
        GameLinePrefix(displayName) + content;

    public static int GameContentByteLimit(string displayName, int configuredMaximumBytes) =>
        Math.Min(
            configuredMaximumBytes,
            GameLineByteBudget - Encoding.UTF8.GetByteCount(GameLinePrefix(displayName)));

    private static string GameLinePrefix(string displayName) =>
        $"[Discord · {Normalize(displayName, 80) ?? "Discord"}] ";

    public static string NormalizeIdentityKey(string value) =>
        value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    public static string DisplayNode(string characterName, string homeWorldName) =>
        $"{characterName} @ {homeWorldName}";
}
