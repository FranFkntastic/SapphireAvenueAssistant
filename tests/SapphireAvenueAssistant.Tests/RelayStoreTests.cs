using Microsoft.Data.Sqlite;
using SapphireAvenue.BridgeProtocol;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Relay;
using System.Text.RegularExpressions;

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

        Assert.Equal(NodeMutationResult.Completed, completed);
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

        var lease = await fixture.Store.HeartbeatAsync("relay-a", "instance-a", now);
        var first = await fixture.Store.EnqueueObservationAsync(
            "relay-a", "instance-a", lease.Epoch, observation, now.AddSeconds(1));
        var duplicate = await fixture.Store.EnqueueObservationAsync(
            "relay-a", "instance-a", lease.Epoch, observation, now.AddSeconds(2));
        var publish = await fixture.Store.ClaimDiscordPublishAsync(
            "10000000000000002",
            now.AddSeconds(3));

        Assert.True(first.Inserted);
        Assert.False(duplicate.Inserted);
        Assert.NotNull(publish);

        var reopened = new RelayStore(fixture.Options);
        await reopened.InitializeAsync();
        Assert.Null(await reopened.ClaimDiscordPublishAsync(
            "10000000000000002",
            now.AddSeconds(40)));
        Assert.Equal(
            PublicationState.Ambiguous,
            await reopened.GetPublicationStateAsync(observation.ObservationId));
    }

    [Fact]
    public async Task OnlyCurrentLeaderObservationIsAcceptedAcrossStandbyAndFailover()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        var leader = await fixture.Store.HeartbeatAsync("relay-a", "instance-a", now);
        var standby = await fixture.Store.HeartbeatAsync("relay-b", "instance-b", now.AddSeconds(1));

        var accepted = await fixture.Store.EnqueueObservationAsync(
            "relay-a", "instance-a", leader.Epoch,
            new InboundObservation("leader:accepted", 1, "Leader", null, "accepted", now),
            now.AddSeconds(2));
        var rejectedStandby = await fixture.Store.EnqueueObservationAsync(
            "relay-b", "instance-b", standby.Epoch,
            new InboundObservation("standby:rejected", 1, "Standby", null, "rejected", now),
            now.AddSeconds(2));
        var rejectedInstance = await fixture.Store.EnqueueObservationAsync(
            "relay-a", "superseded-instance", leader.Epoch,
            new InboundObservation("instance:rejected", 1, "Leader", null, "rejected", now),
            now.AddSeconds(2));

        var replacement = await fixture.Store.HeartbeatAsync("relay-b", "instance-b", now.AddSeconds(11));
        var rejectedExpiredLeader = await fixture.Store.EnqueueObservationAsync(
            "relay-a", "instance-a", leader.Epoch,
            new InboundObservation("expired:rejected", 1, "Old Leader", null, "rejected", now),
            now.AddSeconds(12));
        var acceptedReplacement = await fixture.Store.EnqueueObservationAsync(
            "relay-b", "instance-b", replacement.Epoch,
            new InboundObservation("replacement:accepted", 1, "New Leader", null, "accepted", now),
            now.AddSeconds(12));

        Assert.True(accepted.NodeActive);
        Assert.True(accepted.LeaderAuthorized);
        Assert.True(accepted.Inserted);
        Assert.False(rejectedStandby.LeaderAuthorized);
        Assert.False(rejectedInstance.LeaderAuthorized);
        Assert.False(rejectedExpiredLeader.LeaderAuthorized);
        Assert.True(acceptedReplacement.LeaderAuthorized);
        Assert.True(acceptedReplacement.Inserted);
        Assert.Null(await fixture.Store.GetPublicationStateAsync("standby:rejected"));
        Assert.Null(await fixture.Store.GetPublicationStateAsync("instance:rejected"));
        Assert.Null(await fixture.Store.GetPublicationStateAsync("expired:rejected"));
    }

    [Fact]
    public async Task PreferredNodeFallsBackWithoutPreemptionAndRevocationFencesAccess()
    {
        await using var fixture = await StoreFixture.CreateAsync(new Dictionary<string, string>
        {
            ["relay-a"] = "token-a",
            ["relay-b"] = "token-b"
        });
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000101", "10000000000000002", "10000000000000005",
                BridgeManagementAction.Configure, "10000000000000003", "10000000000000006"),
            now);
        await fixture.Store.HeartbeatAsync(
            "relay-a", "identity-a", now.AddSeconds(-2),
            canSendToGame: false,
            identity: new RelayNodeIdentity("Wei Ning", 40, "Sargatanas"));
        await fixture.Store.HeartbeatAsync(
            "relay-b", "identity-b", now.AddSeconds(-1),
            canSendToGame: false,
            identity: new RelayNodeIdentity("Mega Phone", 40, "Sargatanas"));
        await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000102", "10000000000000002", "10000000000000005",
                BridgeManagementAction.PreferNode, NodeId: "relay-a"),
            now);

        var preferred = await fixture.Store.HeartbeatAsync("relay-a", "instance-a", now);
        var standby = await fixture.Store.HeartbeatAsync("relay-b", "instance-b", now.AddSeconds(2));
        var failover = await fixture.Store.HeartbeatAsync("relay-b", "instance-b", now.AddSeconds(11));
        var returnedPreferred = await fixture.Store.HeartbeatAsync("relay-a", "instance-a", now.AddSeconds(12));

        Assert.True(preferred.IsLeader);
        Assert.True(preferred.IsPreferred);
        Assert.False(standby.IsLeader);
        Assert.True(failover.IsLeader);
        Assert.False(failover.IsPreferred);
        Assert.False(returnedPreferred.IsLeader);
        Assert.True(returnedPreferred.IsPreferred);

        var revoked = await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000103", "10000000000000002", "10000000000000005",
                BridgeManagementAction.RevokeNode, NodeId: "relay-b"),
            now.AddSeconds(13));
        Assert.True(revoked.Succeeded);
        Assert.False(await fixture.Store.AuthorizeNodeAsync("relay-b", "token-b"));
        var replacement = await fixture.Store.HeartbeatAsync("relay-a", "instance-a", now.AddSeconds(13));
        Assert.True(replacement.IsLeader);

        await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000104", "10000000000000002", "10000000000000005",
                BridgeManagementAction.RevokeNode, NodeId: "relay-a"),
            now.AddSeconds(14));
        Assert.False(await fixture.Store.AuthorizeNodeAsync("relay-a", "token-a"));
        Assert.Null((await fixture.Store.GetCommunityConfigurationAsync("10000000000000002"))!.PreferredNodeId);
        var reopened = new RelayStore(fixture.Options);
        await reopened.InitializeAsync();
        Assert.False(await reopened.AuthorizeNodeAsync("relay-a", "token-a"));
    }

    [Fact]
    public async Task PairingCodeIsSingleUseExpiresAndNeverPersistsInReplayResponse()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        var request = new BridgeManagementRequest(
            "100000000000000111", "10000000000000002", "10000000000000005",
            BridgeManagementAction.AddNode);
        var issued = await fixture.Store.ApplyBridgeManagementAsync(request, now);
        var bootstrapMatch = Regex.Match(
            issued.Response,
            @"SADB1 (https://[^\s`]+) ([A-Z2-7]{13})");
        var code = bootstrapMatch.Groups[2].Value;

        Assert.True(issued.Succeeded);
        Assert.True(bootstrapMatch.Success);
        Assert.Equal("https://relay.example/community/", bootstrapMatch.Groups[1].Value);
        Assert.Equal(13, code.Length);
        Assert.Contains("Paste this entire connection string", issued.Response, StringComparison.Ordinal);
        var exchanged = await fixture.Store.ExchangePairingCodeAsync(code, now.AddMinutes(1));
        Assert.NotNull(exchanged);
        Assert.True(await fixture.Store.AuthorizeNodeAsync(exchanged.NodeId, exchanged.AccessToken));
        Assert.Null(await fixture.Store.ExchangePairingCodeAsync(code, now.AddMinutes(2)));

        var replay = await fixture.Store.ApplyBridgeManagementAsync(request, now.AddMinutes(2));
        Assert.True(replay.Replayed);
        Assert.DoesNotContain(code, replay.Response, StringComparison.Ordinal);
        var conflict = await fixture.Store.ApplyBridgeManagementAsync(
            request with { NodeId = "different-hidden-value" },
            now.AddMinutes(2));
        Assert.True(conflict.Conflict);

        var expiring = await fixture.Store.ApplyBridgeManagementAsync(
            request with { InteractionId = "100000000000000112", NodeId = null },
            now);
        var expiringCode = Regex.Match(expiring.Response, @"SADB1 https://[^\s`]+ ([A-Z2-7]{13})").Groups[1].Value;
        Assert.Null(await fixture.Store.ExchangePairingCodeAsync(expiringCode, now.AddMinutes(11)));
    }

    [Fact]
    public async Task AddNodeFailsWithoutConfiguredPublicCoordinatorUrlAndCreatesNoBootstrap()
    {
        await using var fixture = await StoreFixture.CreateAsync(publicBaseUrl: string.Empty);
        var nodeCountBefore = await fixture.Store.CountActiveNodesAsync();
        var issued = await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000119", "10000000000000002", "10000000000000005",
                BridgeManagementAction.AddNode),
            DateTimeOffset.Parse("2026-08-04T12:00:00Z"));

        Assert.False(issued.Succeeded);
        Assert.Contains("coordinator address is not configured", issued.Response, StringComparison.Ordinal);
        Assert.DoesNotContain(RelayConnectionBootstrap.VersionToken, issued.Response, StringComparison.Ordinal);
        Assert.Equal(nodeCountBefore, await fixture.Store.CountActiveNodesAsync());
    }

    [Fact]
    public async Task DuplicateCharacterWorldRevokesAndFencesTheConflictingInstallation()
    {
        await using var fixture = await StoreFixture.CreateAsync(new Dictionary<string, string>
        {
            ["relay-a"] = "token-a",
            ["relay-b"] = "token-b",
            ["relay-c"] = "token-c"
        });
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        await fixture.Store.HeartbeatAsync(
            "relay-a", "instance-a", now,
            canSendToGame: false,
            identity: new RelayNodeIdentity("Wei Ning", 40, "Sargatanas"));
        var leader = await fixture.Store.HeartbeatAsync(
            "relay-b", "instance-b", now.AddSeconds(1),
            canSendToGame: true,
            identity: new RelayNodeIdentity("Backup Relay", 40, "Sargatanas"));
        Assert.True(leader.IsLeader);

        var preferred = await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000171", "10000000000000002", "10000000000000005",
                BridgeManagementAction.PreferNode, NodeId: "relay-b"),
            now.AddSeconds(1));
        Assert.True(preferred.Succeeded);
        Assert.Contains("Backup Relay @ Sargatanas", preferred.Response, StringComparison.Ordinal);
        Assert.DoesNotContain("relay-b", preferred.Response, StringComparison.Ordinal);

        var queued = await fixture.Store.EnqueueOutboundAsync(
            "100000000000000172", "100000000000000173", "Manager", "Race fixture", now.AddSeconds(1));
        var claimed = await fixture.Store.ClaimOutboundAsync(
            "relay-b", "instance-b", leader.Epoch, now.AddSeconds(2));
        Assert.NotNull(claimed.Message);

        var conflict = await fixture.Store.HeartbeatAsync(
            "relay-b", "instance-b", now.AddSeconds(3),
            canSendToGame: true,
            identity: new RelayNodeIdentity("wei ning", 40, "Sargatanas"));
        Assert.False(conflict.Authorized);
        Assert.Equal("wei ning @ Sargatanas", conflict.IdentityConflict);
        Assert.False(await fixture.Store.AuthorizeNodeAsync("relay-b", "token-b"));
        Assert.Null((await fixture.Store.GetCommunityConfigurationAsync("10000000000000002"))!.PreferredNodeId);

        var laterClaim = await fixture.Store.ClaimOutboundAsync(
            "relay-b", "instance-b", leader.Epoch, now.AddSeconds(4));
        var completion = await fixture.Store.CompleteOutboundAsync(
            "relay-b", "instance-b", leader.Epoch,
            queued.MessageId, claimed.Message!.ClaimId, DeliveryOutcome.Sent, now.AddSeconds(4));
        var observation = await fixture.Store.EnqueueObservationAsync(
            "relay-b",
            "instance-b",
            leader.Epoch,
            new InboundObservation("identity-conflict:observation", 1, "Backup Relay", null, "blocked", now),
            now.AddSeconds(4));
        Assert.False(laterClaim.NodeActive);
        Assert.Equal(NodeMutationResult.Unauthorized, completion);
        Assert.False(observation.NodeActive);

        var otherWorld = await fixture.Store.HeartbeatAsync(
            "relay-c", "instance-c", now.AddSeconds(4),
            canSendToGame: false,
            identity: new RelayNodeIdentity("Wei Ning", 41, "Balmung"));
        Assert.True(otherWorld.Authorized);

        var list = await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000174", "10000000000000002", "10000000000000005",
                BridgeManagementAction.ListNodes),
            now.AddSeconds(5));
        Assert.Contains("Wei Ning @ Sargatanas", list.Response, StringComparison.Ordinal);
        Assert.Contains("Wei Ning @ Balmung", list.Response, StringComparison.Ordinal);
        Assert.DoesNotContain("relay-a", list.Response, StringComparison.Ordinal);
        Assert.DoesNotContain("relay-b", list.Response, StringComparison.Ordinal);
        Assert.DoesNotContain("relay-c", list.Response, StringComparison.Ordinal);

        var preferWinner = await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000175", "10000000000000002", "10000000000000005",
                BridgeManagementAction.PreferNode, NodeId: "relay-a"),
            now.AddSeconds(6));
        var status = await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000176", "10000000000000002", "10000000000000005",
                BridgeManagementAction.Status),
            now.AddSeconds(6));
        var revokeOtherWorld = await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000177", "10000000000000002", "10000000000000005",
                BridgeManagementAction.RevokeNode, NodeId: "relay-c"),
            now.AddSeconds(7));
        Assert.Contains("Wei Ning @ Sargatanas", preferWinner.Response, StringComparison.Ordinal);
        Assert.Contains("Wei Ning @ Sargatanas", status.Response, StringComparison.Ordinal);
        Assert.Contains("Wei Ning @ Balmung", revokeOtherWorld.Response, StringComparison.Ordinal);
        Assert.DoesNotContain("relay-a", status.Response, StringComparison.Ordinal);
        Assert.DoesNotContain("relay-c", revokeOtherWorld.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingIdentityPreservesLastTruthAndRenameOrTransferUpdatesTheSameNode()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        await fixture.Store.HeartbeatAsync(
            "relay-a", "instance-a", now,
            canSendToGame: false,
            identity: new RelayNodeIdentity("Wei Ning", 40, "Sargatanas"));
        await fixture.Store.HeartbeatAsync(
            "relay-a", "instance-a", now.AddSeconds(1), canSendToGame: false);

        var preserved = await fixture.Store.SearchNodeChoicesAsync("10000000000000002", "Wei");
        Assert.Contains(preserved, choice => choice.DisplayName == "Wei Ning @ Sargatanas");

        await fixture.Store.HeartbeatAsync(
            "relay-a", "instance-a", now.AddSeconds(2),
            canSendToGame: false,
            identity: new RelayNodeIdentity("Wei Renamed", 41, "Balmung"));
        var updated = await fixture.Store.SearchNodeChoicesAsync("10000000000000002", "Wei");
        Assert.Single(updated);
        Assert.Equal("Wei Renamed @ Balmung", updated[0].DisplayName);
        Assert.Equal("relay-a", updated[0].NodeId);
    }

    [Fact]
    public async Task RevokedNodeCannotMutateAfterAnAuthenticationRace()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        var lease = await fixture.Store.HeartbeatAsync(
            "relay-a", "instance-a", now,
            canSendToGame: true,
            identity: new RelayNodeIdentity("Wei Ning", 40, "Sargatanas"));
        var queued = await fixture.Store.EnqueueOutboundAsync(
            "100000000000000121", "100000000000000122", "Race Tester", "Before revoke", now);
        var claimed = await fixture.Store.ClaimOutboundAsync(
            "relay-a", "instance-a", lease.Epoch, now.AddSeconds(1));
        Assert.NotNull(claimed.Message);

        await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000123", "10000000000000002", "10000000000000005",
                BridgeManagementAction.RevokeNode, NodeId: "relay-a"),
            now.AddSeconds(2));

        var heartbeat = await fixture.Store.HeartbeatAsync(
            "relay-a", "instance-a", now.AddSeconds(3), canSendToGame: true);
        var claim = await fixture.Store.ClaimOutboundAsync(
            "relay-a", "instance-a", lease.Epoch, now.AddSeconds(3));
        var completion = await fixture.Store.CompleteOutboundAsync(
            "relay-a", "instance-a", lease.Epoch,
            queued.MessageId, claimed.Message!.ClaimId, DeliveryOutcome.Sent, now.AddSeconds(3));
        var observation = await fixture.Store.EnqueueObservationAsync(
            "relay-a",
            "instance-a",
            lease.Epoch,
            new InboundObservation("revoked:observation", 1, "Race Tester", null, "Blocked", now),
            now.AddSeconds(3));

        Assert.False(heartbeat.Authorized);
        Assert.False(claim.NodeActive);
        Assert.Equal(NodeMutationResult.Unauthorized, completion);
        Assert.False(observation.NodeActive);
        Assert.Equal(OutboundState.Claimed, await fixture.Store.GetOutboundStateAsync(queued.MessageId));
    }

    [Fact]
    public async Task OnlySendCapableNodeLeadsAndCapabilityLossFencesLease()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        await fixture.Store.HeartbeatAsync(
            "relay-a", "identity-a", now.AddSeconds(-1),
            canSendToGame: false,
            identity: new RelayNodeIdentity("Wei Ning", 40, "Sargatanas"));
        await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000124", "10000000000000002", "10000000000000005",
                BridgeManagementAction.PreferNode, NodeId: "relay-a"),
            now);

        var observer = await fixture.Store.HeartbeatAsync(
            "relay-a", "instance-a", now, canSendToGame: false);
        var leader = await fixture.Store.HeartbeatAsync(
            "relay-a", "instance-a", now.AddSeconds(1), canSendToGame: true);
        var released = await fixture.Store.HeartbeatAsync(
            "relay-a", "instance-a", now.AddSeconds(2), canSendToGame: false);
        var replacement = await fixture.Store.HeartbeatAsync(
            "relay-b", "instance-b", now.AddSeconds(2), canSendToGame: true);
        var returning = await fixture.Store.HeartbeatAsync(
            "relay-a", "instance-a", now.AddSeconds(3), canSendToGame: true);

        Assert.True(observer.Authorized);
        Assert.False(observer.IsLeader);
        Assert.True(leader.IsLeader);
        Assert.False(released.IsLeader);
        Assert.True(released.Epoch > leader.Epoch);
        Assert.True(replacement.IsLeader);
        Assert.True(replacement.Epoch > released.Epoch);
        Assert.False(returning.IsLeader);
    }

    [Fact]
    public async Task CwlsAuthorizationAndInsertUseOneCurrentConfigurationDecision()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000131", "10000000000000002", "10000000000000005",
                BridgeManagementAction.Configure, "10000000000000003", "10000000000000006"),
            now);

        var allowed = await fixture.Store.EnqueueAuthorizedOutboundAsync(
            "10000000000000002", "10000000000000003", ["10000000000000006"],
            "100000000000000132", "10000000000000007", "Affiliate", "Allowed", now);
        await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000133", "10000000000000002", "10000000000000005",
                BridgeManagementAction.Pause),
            now.AddSeconds(1));
        var refused = await fixture.Store.EnqueueAuthorizedOutboundAsync(
            "10000000000000002", "10000000000000003", ["10000000000000006"],
            "100000000000000134", "10000000000000007", "Affiliate", "Blocked", now.AddSeconds(1));

        Assert.Equal(CwlsEnqueueRefusal.None, allowed.Refusal);
        Assert.True(allowed.Inserted);
        Assert.Equal(CwlsEnqueueRefusal.Paused, refused.Refusal);
        Assert.Equal(0, await fixture.Store.CountOutboundByInteractionAsync("100000000000000134"));
    }

    [Fact]
    public async Task DiscordPublishClaimBindsRevisionAndSafelyRequeuesBeforeExternalSend()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        var lease = await fixture.Store.HeartbeatAsync("relay-a", "instance-a", now);
        await fixture.Store.EnqueueObservationAsync(
            "relay-a",
            "instance-a",
            lease.Epoch,
            new InboundObservation("route:observation", 1, "Route Tester", null, "Bound route", now),
            now.AddMilliseconds(1));

        var first = await fixture.Store.ClaimDiscordPublishAsync("10000000000000002", now.AddSeconds(1));
        Assert.NotNull(first);
        Assert.Equal("10000000000000003", first.ChannelId);
        await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000141", "10000000000000002", "10000000000000005",
                BridgeManagementAction.SetChannel, ChannelId: "10000000000000004"),
            now.AddSeconds(2));

        Assert.Equal(
            DiscordPublishRouteCheck.Requeued,
            await fixture.Store.ConfirmDiscordPublishRouteAsync("10000000000000002", first, now.AddSeconds(2)));
        var rebound = await fixture.Store.ClaimDiscordPublishAsync("10000000000000002", now.AddSeconds(3));
        Assert.NotNull(rebound);
        Assert.Equal("10000000000000004", rebound.ChannelId);
        Assert.True(rebound.ConfigurationRevision > first.ConfigurationRevision);
        Assert.Equal(
            DiscordPublishRouteCheck.Current,
            await fixture.Store.ConfirmDiscordPublishRouteAsync("10000000000000002", rebound, now.AddSeconds(3)));

        await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000142", "10000000000000002", "10000000000000005",
                BridgeManagementAction.SetChannel, ChannelId: "10000000000000005"),
            now.AddSeconds(4));
        Assert.True(await fixture.Store.CompleteDiscordPublishAsync(
            rebound.ObservationId,
            rebound.PublishClaimId,
            PublicationState.Published,
            now.AddSeconds(5),
            "10000000000000099",
            null));
        Assert.Equal(PublicationState.Published, await fixture.Store.GetPublicationStateAsync("route:observation"));
        Assert.Equal(
            DiscordPublishRouteCheck.ClaimLost,
            await fixture.Store.ConfirmDiscordPublishRouteAsync("10000000000000002", rebound, now.AddSeconds(5)));
    }

    [Fact]
    public async Task ListNodesDoesNotReportExpiredLeaseAsLeader()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        await fixture.Store.HeartbeatAsync(
            "relay-a", "instance-a", now,
            canSendToGame: true,
            identity: new RelayNodeIdentity("Wei Ning", 40, "Sargatanas"));

        var status = await fixture.Store.ApplyBridgeManagementAsync(
            new BridgeManagementRequest(
                "100000000000000151", "10000000000000002", "10000000000000005",
                BridgeManagementAction.ListNodes),
            now.AddSeconds(11));

        Assert.Contains("Wei Ning @ Sargatanas", status.Response, StringComparison.Ordinal);
        Assert.DoesNotContain("relay-a", status.Response, StringComparison.Ordinal);
        Assert.DoesNotContain("leader", status.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyHeartbeatCompatibilityIsBoundedAndTechnicalIdsRemainHidden()
    {
        Assert.True(new RelayOptions().AllowLegacyHeartbeatWithoutCapability);
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        await using (var allowedFixture = await StoreFixture.CreateAsync())
        {
            var legacyLeader = await allowedFixture.Store.HeartbeatAsync(
                "relay-a",
                "legacy-instance",
                now,
                canSendToGame: null,
                allowLegacyHeartbeatWithoutCapability: true);
            var eligibleStandby = await allowedFixture.Store.HeartbeatAsync(
                "relay-b", "modern-instance", now.AddSeconds(1),
                canSendToGame: true,
                identity: new RelayNodeIdentity("Mega Phone", 40, "Sargatanas"));
            var initialStatus = await allowedFixture.Store.ApplyBridgeManagementAsync(
                new BridgeManagementRequest(
                    "100000000000000161", "10000000000000002", "10000000000000005",
                    BridgeManagementAction.ListNodes),
                now.AddSeconds(2));

            Assert.True(legacyLeader.IsLeader);
            Assert.False(eligibleStandby.IsLeader);
            Assert.Contains("1 setup is waiting", initialStatus.Response, StringComparison.Ordinal);
            Assert.Contains("Mega Phone @ Sargatanas", initialStatus.Response, StringComparison.Ordinal);
            Assert.Contains("standby", initialStatus.Response, StringComparison.Ordinal);
            Assert.DoesNotContain("relay-a", initialStatus.Response, StringComparison.Ordinal);
            Assert.DoesNotContain("relay-b", initialStatus.Response, StringComparison.Ordinal);

            await allowedFixture.Store.HeartbeatAsync(
                "relay-b", "modern-instance", now.AddSeconds(3), canSendToGame: false);
            var ineligibleStatus = await allowedFixture.Store.ApplyBridgeManagementAsync(
                new BridgeManagementRequest(
                    "100000000000000162", "10000000000000002", "10000000000000005",
                    BridgeManagementAction.ListNodes),
                now.AddSeconds(3));
            Assert.Contains("observer; not eligible to send", ineligibleStatus.Response, StringComparison.Ordinal);
        }

        await using (var disabledFixture = await StoreFixture.CreateAsync())
        {
            var legacyObserver = await disabledFixture.Store.HeartbeatAsync(
                "relay-a",
                "legacy-instance",
                now,
                canSendToGame: null,
                allowLegacyHeartbeatWithoutCapability: false);
            var disabledStatus = await disabledFixture.Store.ApplyBridgeManagementAsync(
                new BridgeManagementRequest(
                    "100000000000000163", "10000000000000002", "10000000000000005",
                    BridgeManagementAction.ListNodes),
                now.AddSeconds(1));

            Assert.True(legacyObserver.Authorized);
            Assert.False(legacyObserver.IsLeader);
            Assert.Contains("2 setups are waiting", disabledStatus.Response, StringComparison.Ordinal);
            Assert.DoesNotContain("relay-a", disabledStatus.Response, StringComparison.Ordinal);
            Assert.DoesNotContain("relay-b", disabledStatus.Response, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CapabilityColumnsMigrateIdempotentlyFromLegacyNodeSchema()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"sapphire-avenue-migration-{Guid.NewGuid():N}.db");
        var options = new SapphireAvenueOptions
        {
            Relay = new RelayOptions { DatabasePath = databasePath }
        };
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE relay_nodes (
                        node_id TEXT PRIMARY KEY,
                        label TEXT NOT NULL,
                        token_hash BLOB NULL,
                        last_seen_at_ms INTEGER NULL,
                        last_instance_id TEXT NULL,
                        revoked_at_ms INTEGER NULL,
                        revoked_by_discord_user_id TEXT NULL
                    );
                    """;
                await create.ExecuteNonQueryAsync();
            }

            var store = new RelayStore(options);
            await store.InitializeAsync();
            await store.InitializeAsync();

            await using var inspectConnection = new SqliteConnection($"Data Source={databasePath}");
            await inspectConnection.OpenAsync();
            await using var inspect = inspectConnection.CreateCommand();
            inspect.CommandText = "PRAGMA table_info(relay_nodes);";
            await using var reader = await inspect.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Single(columns, column => column == "can_send_to_game");
            Assert.Single(columns, column => column == "capability_reported");
            Assert.Single(columns, column => column == "character_name");
            Assert.Single(columns, column => column == "character_name_normalized");
            Assert.Single(columns, column => column == "home_world_id");
            Assert.Single(columns, column => column == "home_world_name");
            await reader.DisposeAsync();

            await using var indexes = inspectConnection.CreateCommand();
            indexes.CommandText = "PRAGMA index_list(relay_nodes);";
            await using var indexReader = await indexes.ExecuteReaderAsync();
            var indexNames = new List<string>();
            while (await indexReader.ReadAsync())
            {
                indexNames.Add(indexReader.GetString(1));
            }
            Assert.Single(indexNames, name => name == "ux_relay_nodes_character_world");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task IdentityMigrationRevokesDuplicateClaimBeforeCreatingUniqueIndex()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"sapphire-avenue-identity-migration-{Guid.NewGuid():N}.db");
        var options = new SapphireAvenueOptions
        {
            Relay = new RelayOptions { DatabasePath = databasePath }
        };
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE relay_nodes (
                        node_id TEXT PRIMARY KEY,
                        label TEXT NOT NULL,
                        token_hash BLOB NULL,
                        last_seen_at_ms INTEGER NULL,
                        last_instance_id TEXT NULL,
                        can_send_to_game INTEGER NOT NULL DEFAULT 0,
                        capability_reported INTEGER NOT NULL DEFAULT 0,
                        character_name TEXT NULL,
                        character_name_normalized TEXT NULL,
                        home_world_id INTEGER NULL,
                        home_world_name TEXT NULL,
                        revoked_at_ms INTEGER NULL,
                        revoked_by_discord_user_id TEXT NULL
                    );
                    INSERT INTO relay_nodes(
                        node_id, label, token_hash, character_name, character_name_normalized,
                        home_world_id, home_world_name)
                    VALUES
                        ('older', '', X'01', 'Wei Ning', 'WEI NING', 40, 'Sargatanas'),
                        ('newer', '', X'02', 'wei ning', 'WEI NING', 40, 'Sargatanas');
                    """;
                await create.ExecuteNonQueryAsync();
            }

            var store = new RelayStore(options);
            await store.InitializeAsync();
            await store.InitializeAsync();

            await using var inspect = new SqliteConnection($"Data Source={databasePath}");
            await inspect.OpenAsync();
            await using var count = inspect.CreateCommand();
            count.CommandText = """
                SELECT
                    SUM(CASE WHEN revoked_at_ms IS NULL THEN 1 ELSE 0 END),
                    SUM(CASE WHEN revoked_by_discord_user_id = 'identity-migration-conflict' THEN 1 ELSE 0 END)
                FROM relay_nodes
                WHERE character_name_normalized = 'WEI NING' AND home_world_id = 40;
                """;
            await using var reader = await count.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
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

        public static async Task<StoreFixture> CreateAsync(
            Dictionary<string, string>? nodeTokens = null,
            string publicBaseUrl = "https://relay.example/community")
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"sapphire-avenue-tests-{Guid.NewGuid():N}.db");
            var options = new SapphireAvenueOptions
            {
                Discord = new DiscordOptions
                {
                    GuildId = "10000000000000002",
                    ChannelId = "10000000000000003"
                },
                Relay = new RelayOptions
                {
                    DatabasePath = databasePath,
                    PublicBaseUrl = publicBaseUrl,
                    LeaderLeaseSeconds = 10,
                    ClaimLeaseSeconds = 5,
                    PublishLeaseSeconds = 5,
                    NodeTokens = nodeTokens ?? new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["relay-a"] = "token-a",
                        ["relay-b"] = "token-b"
                    }
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
