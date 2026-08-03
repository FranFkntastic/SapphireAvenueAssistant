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

public interface IDiscordApiClient
{
    Task<DiscordPublishResult> PublishObservationAsync(
        DiscordPublishWorkItem workItem,
        CancellationToken cancellationToken = default);
}

public sealed class DiscordApiClient(
    HttpClient httpClient,
    SapphireAvenueOptions options,
    ILogger<DiscordApiClient> logger) : IDiscordApiClient
{
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(30);

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
        var content = RelayText.EscapeDiscordMarkdown(workItem.Content);
        var nonce = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(workItem.ObservationId)))[..24];
        if (!DiscordOptions.IsSnowflake(workItem.ChannelId))
        {
            return new DiscordPublishResult(
                DiscordPublishOutcome.TerminalRejection,
                Error: "Discord relay channel is not configured.");
        }

        var relativePath = $"channels/{Uri.EscapeDataString(workItem.ChannelId)}/messages";
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = JsonContent.Create(new
            {
                content = $"[CWLS{workItem.CwlsSlot}] **{sender}{world}:** {content}",
                nonce,
                allowed_mentions = new
                {
                    parse = Array.Empty<string>()
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

    private static async Task<TimeSpan?> ReadRetryAfterAsync(
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
