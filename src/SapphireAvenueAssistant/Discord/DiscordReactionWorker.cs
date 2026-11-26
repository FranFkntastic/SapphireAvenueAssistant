using System.Threading.Channels;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Relay;

namespace SapphireAvenueAssistant.Discord;

// Feedback reactions for ordinary channel messages. Outcomes of queued lines are driven from
// durable outbound state, so a coordinator restart still reacts. Refusals that never produced a
// row (paused, role, too long) are best-effort and in-memory only. Nothing here gates delivery.
public sealed class DiscordReactionWorker(
    RelayStore store,
    IDiscordApiClient discord,
    SapphireAvenueOptions options,
    TimeProvider timeProvider,
    ILogger<DiscordReactionWorker> logger) : BackgroundService
{
    private const int MaximumDurableAttempts = 8;
    private static readonly TimeSpan MaximumInlineWait = TimeSpan.FromSeconds(10);

    private readonly Channel<InlineReaction> inline = Channel.CreateBounded<InlineReaction>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    public void EnqueueInline(string channelId, string messageId, string emoji) =>
        inline.Writer.TryWrite(new InlineReaction(channelId, messageId, emoji));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Expiry runs here as well as inside claims, so a message is sealed (and gets its
                // hourglass) even while no relay node is online to attempt a claim.
                var expired = await store.ExpireStaleChannelOutboundAsync(timeProvider.GetUtcNow(), stoppingToken);
                if (expired > 0)
                {
                    logger.LogInformation(
                        "Expired {Count} channel message(s) that no relay claimed within {Seconds}s.",
                        expired,
                        options.Relay.ChannelMessageMaxQueueDuration.TotalSeconds);
                }

                if (options.Discord.CanPublish)
                {
                    await ProcessInlineAsync(stoppingToken);
                    if (await ProcessDurableAsync(stoppingToken))
                    {
                        await ClearStaleSuccessReactionsAsync(stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Channel reaction cycle failed; retrying.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
        }
    }

    private async Task ProcessInlineAsync(CancellationToken cancellationToken)
    {
        for (var processed = 0; processed < 10 && inline.Reader.TryRead(out var reaction); processed++)
        {
            var result = await discord.AddReactionAsync(
                reaction.ChannelId, reaction.MessageId, reaction.Emoji, cancellationToken);
            if (result.Outcome == DiscordReactionOutcome.RetryableRejection &&
                result.RetryAfter is { } delay &&
                delay <= MaximumInlineWait)
            {
                await Task.Delay(delay, timeProvider, cancellationToken);
                result = await discord.AddReactionAsync(
                    reaction.ChannelId, reaction.MessageId, reaction.Emoji, cancellationToken);
            }

            if (result.Outcome != DiscordReactionOutcome.Added)
            {
                logger.LogWarning(
                    "Could not react {Emoji} to Discord message {MessageId}: {Error}",
                    reaction.Emoji,
                    reaction.MessageId,
                    result.Error);
            }
        }
    }

    // Returns false when Discord rate-limited this cycle, so clearing waits for the next one.
    private async Task<bool> ProcessDurableAsync(CancellationToken cancellationToken)
    {
        var pending = await store.ListPendingChannelReactionsAsync(timeProvider.GetUtcNow(), 10, cancellationToken);
        foreach (var reaction in pending)
        {
            var result = await discord.AddReactionAsync(
                reaction.ChannelId, reaction.SourceMessageId, reaction.Emoji, cancellationToken);
            var now = timeProvider.GetUtcNow();
            switch (result.Outcome)
            {
                case DiscordReactionOutcome.Added:
                    await store.CompleteChannelReactionAsync(
                        reaction.OutboundMessageId, ReactionCompletion.Reacted, now, cancellationToken: cancellationToken);
                    break;
                case DiscordReactionOutcome.RetryableRejection when reaction.AttemptCount + 1 < MaximumDurableAttempts:
                    var backoff = result.RetryAfter ??
                        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, reaction.AttemptCount + 1)));
                    await store.CompleteChannelReactionAsync(
                        reaction.OutboundMessageId, ReactionCompletion.Retry, now, backoff, cancellationToken);
                    if (result.RetryAfter is not null)
                    {
                        // Rate-limited: leave the rest of this batch for the next cycle.
                        return false;
                    }

                    break;
                default:
                    await store.CompleteChannelReactionAsync(
                        reaction.OutboundMessageId, ReactionCompletion.Abandoned, now, cancellationToken: cancellationToken);
                    logger.LogWarning(
                        "Gave up reacting {Emoji} to Discord message {MessageId} (outbound {OutboundId}): {Error}",
                        reaction.Emoji,
                        reaction.SourceMessageId,
                        reaction.OutboundMessageId,
                        result.Error);
                    break;
            }
        }

        return true;
    }

    // Only the latest delivered channel message keeps its check mark. Once a newer one carries
    // its own, older check marks are removed (the bot's own reaction only); every other
    // reaction stays. State lives in the store, so a restart resumes where it left off.
    private async Task ClearStaleSuccessReactionsAsync(CancellationToken cancellationToken)
    {
        var stale = await store.ListStaleSuccessReactionsAsync(timeProvider.GetUtcNow(), 10, cancellationToken);
        foreach (var reaction in stale)
        {
            var result = await discord.RemoveOwnReactionAsync(
                reaction.ChannelId, reaction.SourceMessageId, reaction.Emoji, cancellationToken);
            var now = timeProvider.GetUtcNow();
            switch (result.Outcome)
            {
                case DiscordReactionOutcome.Removed:
                    await store.CompleteSuccessReactionClearAsync(
                        reaction.OutboundMessageId, ReactionClearCompletion.Cleared, now, cancellationToken: cancellationToken);
                    break;
                case DiscordReactionOutcome.RetryableRejection when reaction.AttemptCount + 1 < MaximumDurableAttempts:
                    var backoff = result.RetryAfter ??
                        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, reaction.AttemptCount + 1)));
                    await store.CompleteSuccessReactionClearAsync(
                        reaction.OutboundMessageId, ReactionClearCompletion.Retry, now, backoff, cancellationToken);
                    if (result.RetryAfter is not null)
                    {
                        return;
                    }

                    break;
                default:
                    await store.CompleteSuccessReactionClearAsync(
                        reaction.OutboundMessageId, ReactionClearCompletion.Abandoned, now, cancellationToken: cancellationToken);
                    logger.LogWarning(
                        "Gave up removing {Emoji} from Discord message {MessageId} (outbound {OutboundId}): {Error}",
                        reaction.Emoji,
                        reaction.SourceMessageId,
                        reaction.OutboundMessageId,
                        result.Error);
                    break;
            }
        }
    }

    private sealed record InlineReaction(string ChannelId, string MessageId, string Emoji);
}
