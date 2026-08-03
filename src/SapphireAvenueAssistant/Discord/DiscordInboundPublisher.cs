using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Relay;

namespace SapphireAvenueAssistant.Discord;

public sealed class DiscordInboundPublisher(
    RelayStore store,
    IDiscordApiClient discord,
    SapphireAvenueOptions options,
    TimeProvider timeProvider,
    ILogger<DiscordInboundPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!options.Discord.CanPublish)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken);
                continue;
            }

            var workItem = await store.ClaimDiscordPublishAsync(
                options.Discord.GuildId,
                timeProvider.GetUtcNow(),
                stoppingToken);
            if (workItem is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
                continue;
            }

            var route = await store.ConfirmDiscordPublishRouteAsync(
                options.Discord.GuildId,
                workItem,
                timeProvider.GetUtcNow(),
                stoppingToken);
            if (route != DiscordPublishRouteCheck.Current)
            {
                continue;
            }

            // Once the external POST starts, its outcome is bound to this claimed revision.
            // A later configuration change must not silently redirect or blindly retry it.
            var result = await discord.PublishObservationAsync(workItem, stoppingToken);
            var (state, retryAfter) = result.Outcome switch
            {
                DiscordPublishOutcome.Published => (PublicationState.Published, (TimeSpan?)null),
                DiscordPublishOutcome.RetryableRejection when workItem.AttemptCount < 5 =>
                    (PublicationState.Retry, result.RetryAfter ?? TimeSpan.FromSeconds(2)),
                DiscordPublishOutcome.RetryableRejection => (PublicationState.Failed, null),
                DiscordPublishOutcome.TerminalRejection => (PublicationState.Failed, null),
                DiscordPublishOutcome.ReconciliationRequired => (PublicationState.Ambiguous, null),
                _ => throw new ArgumentOutOfRangeException()
            };
            await store.CompleteDiscordPublishAsync(
                workItem.ObservationId,
                workItem.PublishClaimId,
                state,
                timeProvider.GetUtcNow(),
                result.MessageId,
                result.Error,
                retryAfter,
                stoppingToken);

            if (state is PublicationState.Ambiguous or PublicationState.Failed)
            {
                logger.LogError(
                    "Discord publication for observation {ObservationId} stopped in {State}: {Error}",
                    workItem.ObservationId,
                    state,
                    result.Error);
            }
        }
    }
}
