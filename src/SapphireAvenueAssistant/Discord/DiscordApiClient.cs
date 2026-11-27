using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Relay;

namespace SapphireAvenueAssistant.Discord;

public enum DiscordPublishOutcome
{
    Published,
    RetryableRejection,
    TerminalRejection,
    ReconciliationRequired
}

public sealed record DiscordPublishResult(
    DiscordPublishOutcome Outcome,
    string? MessageId = null,
    string? Error = null,
    TimeSpan? RetryAfter = null);

public enum DiscordReactionOutcome
{
    Added,
    Removed,
    RetryableRejection,
    TerminalRejection
}

public sealed record DiscordReactionResult(
    DiscordReactionOutcome Outcome,
    string? Error = null,
    TimeSpan? RetryAfter = null);

public sealed record DiscordGatewayInfo(
    string? Url,
    int? SessionStartRemaining = null,
    TimeSpan? SessionStartResetAfter = null,
    bool Unauthorized = false,
    string? Error = null);

public interface IDiscordApiClient
{
    Task<DiscordPublishResult> PublishObservationAsync(
        DiscordPublishWorkItem workItem,
        CancellationToken cancellationToken = default);

    Task<DiscordReactionResult> AddReactionAsync(
        string channelId,
        string messageId,
        string emoji,
        CancellationToken cancellationToken = default);

    // Removes only the bot's own reaction. A missing message or reaction counts as removed.
    Task<DiscordReactionResult> RemoveOwnReactionAsync(
        string channelId,
        string messageId,
        string emoji,
        CancellationToken cancellationToken = default);

    Task<DiscordGatewayInfo> GetGatewayBotAsync(CancellationToken cancellationToken = default);
}

public sealed class DiscordApiClient(
    HttpClient httpClient,
    SapphireAvenueOptions options,
    DiscordMentionResolver mentionResolver,
    ILogger<DiscordApiClient> logger) : IDiscordApiClient
{
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumReactionRetryAfter = TimeSpan.FromMinutes(2);

    // Escapes the game text between resolved mentions and splices each one in as <@id>.
    private static string RenderContent(string raw, IReadOnlyList<GameMention> mentions)
    {
        if (mentions.Count == 0)
        {
            return RelayText.EscapeDiscordMarkdown(raw);
        }

        var builder = new StringBuilder(raw.Length + (mentions.Count * 24));
        var position = 0;
        foreach (var mention in mentions.OrderBy(mention => mention.Start))
        {
            if (mention.Start < position)
            {
                continue;
            }

            builder.Append(RelayText.EscapeDiscordMarkdown(raw[position..mention.Start]));
            builder.Append("<@").Append(mention.UserId).Append('>');
            position = mention.Start + mention.Length;
        }

        builder.Append(RelayText.EscapeDiscordMarkdown(raw[position..]));
        return builder.ToString();
    }

    public async Task<DiscordPublishResult> PublishObservationAsync(
        DiscordPublishWorkItem workItem,
        CancellationToken cancellationToken = default)
    {
        if (!options.Discord.CanPublish)
        {
            return new DiscordPublishResult(
                DiscordPublishOutcome.TerminalRejection,
                Error: "Discord publication is not configured.");
        }

        var sender = RelayText.EscapeDiscordMarkdown(workItem.SenderName);
        var world = string.IsNullOrWhiteSpace(workItem.SenderWorld)
            ? string.Empty
            : $" @ {RelayText.EscapeDiscordMarkdown(workItem.SenderWorld)}";
        var nonce = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(workItem.ObservationId)))[..24];
        if (!DiscordOptions.IsSnowflake(workItem.ChannelId))
        {
            return new DiscordPublishResult(
                DiscordPublishOutcome.TerminalRejection,
                Error: "Discord relay channel is not configured.");
        }

        // Only the game text is resolved, never the sender prefix. Pings are limited to exactly
        // the resolver-chosen users: parse stays empty, so a raw <@id>, <@&id> or @everyone typed
        // in the game renders but never notifies anyone.
        var mentions = await mentionResolver.ResolveAsync(workItem.Content, cancellationToken);
        var content = RenderContent(workItem.Content, mentions);
        var pingedUsers = mentions
            .Select(mention => mention.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var relativePath = $"channels/{Uri.EscapeDataString(workItem.ChannelId)}/messages";
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = JsonContent.Create(new
            {
                content = $"[CWLS{workItem.CwlsSlot}] **{sender}{world}:** {content}",
                nonce,
                allowed_mentions = new
                {
                    parse = Array.Empty<string>(),
                    users = pingedUsers
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.Discord.BotToken);
        request.Headers.UserAgent.ParseAdd("Sapphire-Avenue-Discord-Bridge/1.0");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DiscordPublishResult(
                DiscordPublishOutcome.ReconciliationRequired,
                Error: "Discord publication timed out after delivery may have occurred.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Discord publication failed without a conclusive response.");
            return new DiscordPublishResult(
                DiscordPublishOutcome.ReconciliationRequired,
                Error: "Discord publication failed after delivery may have occurred.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var messageId = await ReadMessageIdAsync(response, cancellationToken);
                return DiscordOptions.IsSnowflake(messageId ?? string.Empty)
                    ? new DiscordPublishResult(DiscordPublishOutcome.Published, messageId)
                    : new DiscordPublishResult(
                        DiscordPublishOutcome.ReconciliationRequired,
                        Error: "Discord returned success without a message identity.");
            }

            var error = await ReadErrorAsync(response, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = await ReadRetryAfterAsync(response, cancellationToken);
                return retryAfter is { } delay && delay >= TimeSpan.Zero && delay <= MaximumRetryAfter
                    ? new DiscordPublishResult(
                        DiscordPublishOutcome.RetryableRejection,
                        Error: error,
                        RetryAfter: delay)
                    : new DiscordPublishResult(
                        DiscordPublishOutcome.TerminalRejection,
                        Error: "Discord returned an invalid retry interval.");
            }

            return (int)response.StatusCode >= 500
                ? new DiscordPublishResult(
                    DiscordPublishOutcome.ReconciliationRequired,
                    Error: error)
                : new DiscordPublishResult(
                    DiscordPublishOutcome.TerminalRejection,
                    Error: error);
        }
    }

    // Reactions are best-effort feedback. PUT and DELETE on .../@me are idempotent, so every
    // inconclusive response is safe to retry; only a definite client rejection stops the attempt.
    public Task<DiscordReactionResult> AddReactionAsync(
        string channelId,
        string messageId,
        string emoji,
        CancellationToken cancellationToken = default) =>
        SendOwnReactionAsync(HttpMethod.Put, channelId, messageId, emoji, cancellationToken);

    public Task<DiscordReactionResult> RemoveOwnReactionAsync(
        string channelId,
        string messageId,
        string emoji,
        CancellationToken cancellationToken = default) =>
        SendOwnReactionAsync(HttpMethod.Delete, channelId, messageId, emoji, cancellationToken);

    private async Task<DiscordReactionResult> SendOwnReactionAsync(
        HttpMethod method,
        string channelId,
        string messageId,
        string emoji,
        CancellationToken cancellationToken)
    {
        var removing = method == HttpMethod.Delete;
        if (!options.Discord.CanPublish ||
            !DiscordOptions.IsSnowflake(channelId) ||
            !DiscordOptions.IsSnowflake(messageId))
        {
            return new DiscordReactionResult(
                DiscordReactionOutcome.TerminalRejection,
                "Discord reactions are not configured or the target is invalid.");
        }

        var relativePath =
            $"channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}" +
            $"/reactions/{Uri.EscapeDataString(emoji)}/@me";
        using var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.Discord.BotToken);
        request.Headers.UserAgent.ParseAdd("Sapphire-Avenue-Discord-Bridge/1.0");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DiscordReactionResult(DiscordReactionOutcome.RetryableRejection, "Discord reaction timed out.");
        }
        catch (HttpRequestException exception)
        {
            return new DiscordReactionResult(DiscordReactionOutcome.RetryableRejection, exception.Message);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return new DiscordReactionResult(
                    removing ? DiscordReactionOutcome.Removed : DiscordReactionOutcome.Added);
            }

            if (removing && response.StatusCode == HttpStatusCode.NotFound)
            {
                // The message was deleted or the reaction is already gone: nothing left to remove.
                return new DiscordReactionResult(DiscordReactionOutcome.Removed);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Read retry_after before anything else consumes the body.
                var retryAfter = await ReadRetryAfterAsync(response, cancellationToken);
                return new DiscordReactionResult(
                    DiscordReactionOutcome.RetryableRejection,
                    "Discord rate-limited the reaction.",
                    retryAfter is { } delay && delay >= TimeSpan.Zero
                        ? (delay <= MaximumReactionRetryAfter ? delay : MaximumReactionRetryAfter)
                        : TimeSpan.FromSeconds(2));
            }

            var error = await ReadErrorAsync(response, cancellationToken);
            return (int)response.StatusCode >= 500
                ? new DiscordReactionResult(DiscordReactionOutcome.RetryableRejection, error)
                : new DiscordReactionResult(DiscordReactionOutcome.TerminalRejection, error);
        }
    }

    public async Task<DiscordGatewayInfo> GetGatewayBotAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Discord.BotToken))
        {
            return new DiscordGatewayInfo(null, Error: "The Discord bot token is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "gateway/bot");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.Discord.BotToken);
        request.Headers.UserAgent.ParseAdd("Sapphire-Avenue-Discord-Bridge/1.0");
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DiscordGatewayInfo(null, Error: "Discord gateway discovery timed out.");
        }
        catch (HttpRequestException exception)
        {
            return new DiscordGatewayInfo(null, Error: exception.Message);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new DiscordGatewayInfo(null, Unauthorized: true, Error: "Discord rejected the bot token.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DiscordGatewayInfo(null, Error: await ReadErrorAsync(response, cancellationToken));
            }

            try
            {
                using var payload = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                var root = payload.RootElement;
                var url = root.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                    ? urlElement.GetString()
                    : null;
                int? remaining = null;
                TimeSpan? resetAfter = null;
                if (root.TryGetProperty("session_start_limit", out var limit) && limit.ValueKind == JsonValueKind.Object)
                {
                    if (limit.TryGetProperty("remaining", out var remainingElement) &&
                        remainingElement.TryGetInt32(out var remainingValue))
                    {
                        remaining = remainingValue;
                    }

                    if (limit.TryGetProperty("reset_after", out var resetElement) &&
                        resetElement.TryGetInt64(out var resetMs))
                    {
                        resetAfter = TimeSpan.FromMilliseconds(resetMs);
                    }
                }

                return url is not null &&
                    Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                    parsed.Scheme == "wss"
                    ? new DiscordGatewayInfo(url, remaining, resetAfter)
                    : new DiscordGatewayInfo(null, Error: "Discord returned no usable gateway URL.");
            }
            catch (JsonException)
            {
                return new DiscordGatewayInfo(null, Error: "Discord returned an unreadable gateway response.");
            }
        }
    }

    private static async Task<string?> ReadMessageIdAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var payload = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            return payload.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        if (error.Length > 512)
        {
            error = error[..512];
        }

        return string.IsNullOrWhiteSpace(error)
            ? $"Discord returned HTTP {(int)response.StatusCode}."
            : error;
    }

    internal static async Task<TimeSpan?> ReadRetryAfterAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var payload = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (payload.RootElement.TryGetProperty("retry_after", out var retryAfter) &&
                retryAfter.TryGetDouble(out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        catch (JsonException)
        {
            // Discord may still provide the standard Retry-After header.
        }

        return response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } retryAt
                ? retryAt - DateTimeOffset.UtcNow
                : null);
    }
}
