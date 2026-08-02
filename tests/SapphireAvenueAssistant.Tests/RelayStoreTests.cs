using Microsoft.Data.Sqlite;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Relay;

namespace SapphireAvenueAssistant.Tests;

public sealed class RelayStoreTests
{
    [Fact]
    public async Task ExpiredLeaderIsReplacedAndOldEpochIsFenced()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        var first = await fixture.Store.HeartbeatAsync("relay-a", "instance-a", now);
        var observer = await fixture.Store.HeartbeatAsync("relay-b", "instance-b", now.AddSeconds(2));

        Assert.True(first.IsLeader);
        Assert.False(observer.IsLeader);
        Assert.Equal(first.Epoch, observer.Epoch);

        var replacement = await fixture.Store.HeartbeatAsync("relay-b", "instance-b", now.AddSeconds(11));
        Assert.True(replacement.IsLeader);
        Assert.Equal(first.Epoch + 1, replacement.Epoch);

        await fixture.Store.EnqueueOutboundAsync(
            "100000000000000001",
            "100000000000000002",
            "Miqo Friend",
            "Map train at nine.",
            now);
        var staleClaim = await fixture.Store.ClaimOutboundAsync(
            "relay-a",
            "instance-a",
            first.Epoch,
            now.AddSeconds(12));
        var currentClaim = await fixture.Store.ClaimOutboundAsync(
            "relay-b",
            "instance-b",
            replacement.Epoch,
            now.AddSeconds(12));

        Assert.False(staleClaim.Authorized);
        Assert.Null(staleClaim.Message);
        Assert.True(currentClaim.Authorized);
        Assert.NotNull(currentClaim.Message);
    }

    [Fact]
    public async Task ExpiredOutboundClaimBecomesAmbiguousInsteadOfBeingRetried()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        var lease = await fixture.Store.HeartbeatAsync("relay-a", "instance-a", now);
        var queued = await fixture.Store.EnqueueOutboundAsync(
            "100000000000000003",
            "100000000000000004",
            "Viera Friend",
            "Treasure portal is up.",
            now);
        var firstClaim = await fixture.Store.ClaimOutboundAsync(
            "relay-a",
            "instance-a",
            lease.Epoch,
            now.AddSeconds(1));

        Assert.NotNull(firstClaim.Message);

        var secondClaim = await fixture.Store.ClaimOutboundAsync(
            "relay-a",
            "instance-a",
            lease.Epoch,
            now.AddSeconds(7));

        Assert.True(secondClaim.Authorized);
        Assert.Null(secondClaim.Message);
        Assert.Equal(OutboundState.Ambiguous, await fixture.Store.GetOutboundStateAsync(queued.MessageId));
    }

    [Fact]
    public async Task ExplicitNotSentCompletionSafelyRequeuesTheLine()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        var lease = await fixture.Store.HeartbeatAsync("relay-a", "instance-a", now);
        await fixture.Store.EnqueueOutboundAsync(
            "100000000000000005",
            "100000000000000006",
            "Lala Friend",
            "Pulling in five.",
            now);
        var first = await fixture.Store.ClaimOutboundAsync(
            "relay-a",
            "instance-a",
            lease.Epoch,
            now.AddSeconds(1));
        Assert.NotNull(first.Message);

        var completed = await fixture.Store.CompleteOutboundAsync(
            "relay-a",
            "instance-a",
            lease.Epoch,
            first.Message.MessageId,
            first.Message.ClaimId,
            DeliveryOutcome.NotSent,
            now.AddSeconds(2));
        var second = await fixture.Store.ClaimOutboundAsync(
            "relay-a",
            "instance-a",
            lease.Epoch,
            now.AddSeconds(3));

        Assert.True(completed);
        Assert.NotNull(second.Message);
        Assert.NotEqual(first.Message.ClaimId, second.Message.ClaimId);
    }

    [Fact]
    public async Task DiscordInteractionIdIsIdempotent()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");

        var first = await fixture.Store.EnqueueOutboundAsync(
            "100000000000000007",
            "100000000000000008",
            "Au Ra Friend",
            "Hello from Discord.",
            now);
        var duplicate = await fixture.Store.EnqueueOutboundAsync(
            "100000000000000007",
            "100000000000000008",
            "Au Ra Friend",
            "Hello from Discord.",
            now.AddSeconds(1));

        Assert.True(first.Inserted);
        Assert.False(duplicate.Inserted);
        Assert.Equal(first.MessageId, duplicate.MessageId);
        Assert.Equal(1, await fixture.Store.CountOutboundByInteractionAsync("100000000000000007"));
    }

    [Fact]
    public async Task ObservationIsIdempotentAndInFlightRestartBecomesAmbiguous()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        var observation = new InboundObservation(
            "cwls1:9ac92af4",
            1,
            "Hyur Friend",
            "Balmung",
            "Hello from the CWLS.",
            now);

        var first = await fixture.Store.EnqueueObservationAsync("relay-a", observation, now);
        var duplicate = await fixture.Store.EnqueueObservationAsync("relay-b", observation, now.AddSeconds(1));
        var publish = await fixture.Store.ClaimDiscordPublishAsync(now.AddSeconds(2));

        Assert.True(first.Inserted);
        Assert.False(duplicate.Inserted);
        Assert.NotNull(publish);

        var reopened = new RelayStore(fixture.Options);
        await reopened.InitializeAsync();
        Assert.Null(await reopened.ClaimDiscordPublishAsync(now.AddSeconds(40)));
        Assert.Equal(
            PublicationState.Ambiguous,
            await reopened.GetPublicationStateAsync(observation.ObservationId));
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private StoreFixture(string databasePath, SapphireAvenueOptions options, RelayStore store)
        {
            DatabasePath = databasePath;
            Options = options;
            Store = store;
        }

        public string DatabasePath { get; }

        public SapphireAvenueOptions Options { get; }

        public RelayStore Store { get; }

        public static async Task<StoreFixture> CreateAsync()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"sapphire-avenue-tests-{Guid.NewGuid():N}.db");
            var options = new SapphireAvenueOptions
            {
                Relay = new RelayOptions
                {
                    DatabasePath = databasePath,
                    LeaderLeaseSeconds = 10,
                    ClaimLeaseSeconds = 5,
                    PublishLeaseSeconds = 5
                }
            };
            var store = new RelayStore(options);
            await store.InitializeAsync();
            return new StoreFixture(databasePath, options, store);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { DatabasePath, $"{DatabasePath}-shm", $"{DatabasePath}-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
