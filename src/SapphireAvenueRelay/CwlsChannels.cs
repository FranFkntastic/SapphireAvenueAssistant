using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.Text;

namespace SapphireAvenueRelay;

internal static class CwlsChannels
{
    public static XivChatType ForSlot(int slot) => slot switch
    {
        1 => XivChatType.CrossLinkShell1,
        2 => XivChatType.CrossLinkShell2,
        3 => XivChatType.CrossLinkShell3,
        4 => XivChatType.CrossLinkShell4,
        5 => XivChatType.CrossLinkShell5,
        6 => XivChatType.CrossLinkShell6,
        7 => XivChatType.CrossLinkShell7,
        8 => XivChatType.CrossLinkShell8,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), "CWLS slot must be from 1 through 8."),
    };

    public static int? ToSlot(XivChatType type) => type switch
    {
        XivChatType.CrossLinkShell1 => 1,
        XivChatType.CrossLinkShell2 => 2,
        XivChatType.CrossLinkShell3 => 3,
        XivChatType.CrossLinkShell4 => 4,
        XivChatType.CrossLinkShell5 => 5,
        XivChatType.CrossLinkShell6 => 6,
        XivChatType.CrossLinkShell7 => 7,
        XivChatType.CrossLinkShell8 => 8,
        _ => null,
    };

    public static string FormatDiscordLine(string displayName, string content)
    {
        var name = Normalize(displayName, 80) ?? "Discord";
        var prefix = $"[Discord · {name}] ";
        var availableContentBytes = 494 - Encoding.UTF8.GetByteCount(prefix);
        var text = Normalize(content, availableContentBytes) ?? throw new ArgumentException("Message is empty.", nameof(content));
        return prefix + text;
    }

    public static string ObservationId(int slot, int timestamp, ReadOnlySpan<byte> sender, ReadOnlySpan<byte> message)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(slot));
        hash.AppendData(BitConverter.GetBytes(timestamp));
        hash.AppendData(BitConverter.GetBytes(sender.Length));
        hash.AppendData(sender);
        hash.AppendData(BitConverter.GetBytes(message.Length));
        hash.AppendData(message);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string? Normalize(string? value, int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var builder = new StringBuilder(collapsed.Length);
        var bytes = 0;
        foreach (var rune in collapsed.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
                continue;
            if (bytes + rune.Utf8SequenceLength > maximumUtf8Bytes)
                break;
            builder.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }

        return builder.ToString().Trim() is { Length: > 0 } normalized ? normalized : null;
    }
}
