using System.Text;
using System.Text.Json;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Relay;

namespace SapphireAvenueAssistant.Discord;

// Ordinary messages typed in the configured relay channel. Ingestion only enqueues: the same
// fail-closed, epoch-fenced leader path as /cwls message decides whether anything reaches the game.
public sealed class ChannelMessageIngestor(
    RelayStore store,
    DiscordReactionWorker reactions,
    SapphireAvenueOptions options,
    TimeProvider timeProvider,
    ILogger<ChannelMessageIngestor> logger)
{
    private const int DefaultMessageType = 0;
    private const int ReplyMessageType = 19;

    public async Task HandleMessageCreateAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!TryGetString(message, "guild_id", out var guildId) ||
            !string.Equals(guildId, options.Discord.GuildId, StringComparison.Ordinal) ||
            !TryGetString(message, "channel_id", out var channelId) ||
            !TryGetString(message, "id", out var messageId) ||
            !DiscordOptions.IsSnowflake(messageId) ||
            !message.TryGetProperty("type", out var typeElement) ||
            !typeElement.TryGetInt32(out var messageType) ||
            messageType is not (DefaultMessageType or ReplyMessageType) ||
            message.TryGetProperty("webhook_id", out _) ||
            !message.TryGetProperty("author", out var author) ||
            author.ValueKind != JsonValueKind.Object ||
            IsTrue(author, "bot") ||
            IsTrue(author, "system") ||
            !TryGetString(author, "id", out var authorId) ||
            !message.TryGetProperty("member", out var member) ||
            member.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // Only the current DB-configured relay channel is listened to; other channels are silent.
        var configuration = await store.GetCommunityConfigurationAsync(guildId, cancellationToken);
        if (configuration is null ||
            !string.Equals(configuration.ChannelId, channelId, StringComparison.Ordinal))
        {
            return;
        }

        // Attachment-only, sticker-only and empty-after-rendering messages are ignored outright.
        if (!TryGetString(message, "content", out var rawContent))
        {
            return;
        }

        var rendered = RelayText.Normalize(DiscordMessageRenderer.Render(rawContent, message), int.MaxValue);
        var displayName = RelayText.Normalize(DiscordMessageRenderer.DisplayName(author, member), 128);
        if (rendered is null || displayName is null)
        {
            return;
        }

        var roles = ReadRoles(member);
        var source = new ChannelOutboundSource(channelId, messageId);

        // Cheap precheck so an unauthorized author sees the refusal rather than a length complaint.
        // The enqueue below repeats every check atomically.
        if (configuration.IsPaused)
        {
            React(source, ChannelReactions.Paused);
            return;
        }

        if (configuration.AllowedRoleId is not null &&
            !roles.Contains(configuration.AllowedRoleId, StringComparer.Ordinal))
        {
            React(source, ChannelReactions.RoleRequired);
            return;
        }

        // Unlike the slash command, never truncate a chat line: the game-side prefix must fit too.
        var limit = RelayText.GameContentByteLimit(displayName, options.Relay.BoundedMaximumMessageBytes);
        if (Encoding.UTF8.GetByteCount(rendered) > limit)
        {
            React(source, ChannelReactions.TooLong);
            return;
        }

        var enqueue = await store.EnqueueAuthorizedChannelOutboundAsync(
            guildId,
            source,
            roles,
            authorId,
            displayName,
            rendered,
            timeProvider.GetUtcNow(),
            cancellationToken);
        switch (enqueue.Refusal)
        {
            case CwlsEnqueueRefusal.None:
                if (enqueue.Inserted)
                {
                    logger.LogDebug(
                        "Queued Discord channel message {MessageId} as outbound {OutboundId}.",
                        messageId,
                        enqueue.MessageId);
                }

                break;
            case CwlsEnqueueRefusal.Paused:
                React(source, ChannelReactions.Paused);
                break;
            case CwlsEnqueueRefusal.RoleRequired:
                React(source, ChannelReactions.RoleRequired);
                break;
            default:
                // Configuration changed between the precheck and the enqueue; stay silent.
                break;
        }
    }

    private void React(ChannelOutboundSource source, string emoji) =>
        reactions.EnqueueInline(source.ChannelId, source.MessageId, emoji);

    private static string[] ReadRoles(JsonElement member) =>
        member.TryGetProperty("roles", out var roleArray) && roleArray.ValueKind == JsonValueKind.Array
            ? roleArray
                .EnumerateArray()
                .Where(role => role.ValueKind == JsonValueKind.String)
                .Select(role => role.GetString())
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Cast<string>()
                .ToArray()
            : [];

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }
}
