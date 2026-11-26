using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SapphireAvenueAssistant.Discord;

// Turns Discord-only markup into text a CWLS line can show. Ordinary characters are left alone;
// RelayText.Normalize still collapses whitespace and strips control characters afterwards.
public static partial class DiscordMessageRenderer
{
    public static string Render(string content, JsonElement message)
    {
        var mentionNames = ReadMentionNames(message);
        return Markup().Replace(content, match =>
        {
            if (match.Groups["user"].Success)
            {
                return mentionNames.TryGetValue(match.Groups["user"].Value, out var name)
                    ? $"@{name}"
                    : "@user";
            }

            if (match.Groups["role"].Success)
            {
                return "@role";
            }

            if (match.Groups["channel"].Success)
            {
                return "#channel";
            }

            if (match.Groups["emoji"].Success)
            {
                return $":{match.Groups["emoji"].Value}:";
            }

            if (match.Groups["command"].Success)
            {
                return $"/{match.Groups["command"].Value}";
            }

            if (match.Groups["timestamp"].Success &&
                long.TryParse(match.Groups["timestamp"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds) &&
                seconds is >= -62135596800 and <= 253402300799)
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds)
                    .ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            }

            return match.Value;
        });
    }

    // member nick > global_name > username, the same precedence as the slash command.
    public static string? DisplayName(JsonElement user, JsonElement? member)
    {
        if (member is { ValueKind: JsonValueKind.Object } memberObject &&
            memberObject.TryGetProperty("nick", out var nick) &&
            nick.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(nick.GetString()))
        {
            return nick.GetString();
        }

        if (user.TryGetProperty("global_name", out var globalName) &&
            globalName.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(globalName.GetString()))
        {
            return globalName.GetString();
        }

        return user.TryGetProperty("username", out var username) && username.ValueKind == JsonValueKind.String
            ? username.GetString()
            : null;
    }

    private static Dictionary<string, string> ReadMentionNames(JsonElement message)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!message.TryGetProperty("mentions", out var mentions) || mentions.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var mention in mentions.EnumerateArray())
        {
            if (mention.ValueKind != JsonValueKind.Object ||
                !mention.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            // Gateway MESSAGE_CREATE mentions carry a partial guild member under "member".
            JsonElement? member = mention.TryGetProperty("member", out var memberElement)
                ? memberElement
                : null;
            if (DisplayName(mention, member) is { } name)
            {
                names[id.GetString()!] = name;
            }
        }

        return names;
    }

    [GeneratedRegex(
        @"<@!?(?<user>\d{17,20})>|<@&(?<role>\d{17,20})>|<#(?<channel>\d{17,20})>|<a?:(?<emoji>\w{2,32}):\d{17,20}>|</(?<command>[\w\- ]{1,100}):\d{17,20}>|<t:(?<timestamp>-?\d{1,13})(?::[tTdDfFR])?>",
        RegexOptions.CultureInvariant)]
    private static partial Regex Markup();
}
