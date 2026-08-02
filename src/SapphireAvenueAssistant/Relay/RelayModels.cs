namespace SapphireAvenueAssistant.Relay;

public enum OutboundState
{
    Pending,
    Claimed,
    Sent,
    Ambiguous
}

public enum DeliveryOutcome
{
    Sent,
    NotSent,
    Ambiguous
}

public enum PublicationState
{
    Pending,
    InFlight,
    Published,
    Retry,
    Ambiguous,
    Failed
}

public sealed record LeaderLease(
    bool IsLeader,
    long Epoch,
    DateTimeOffset ExpiresAtUtc);

public sealed record OutboundRelayMessage(
    string MessageId,
    string ClaimId,
    string DiscordUserId,
    string DiscordDisplayName,
    string Content,
    DateTimeOffset CreatedAtUtc);

public sealed record OutboundClaimResult(bool Authorized, OutboundRelayMessage? Message);

public sealed record InboundObservation(
    string ObservationId,
    int CwlsSlot,
    string SenderName,
    string? SenderWorld,
    string Content,
    DateTimeOffset ObservedAtUtc);

public sealed record DiscordPublishWorkItem(
    string ObservationId,
    string PublishClaimId,
    int AttemptCount,
    int CwlsSlot,
    string SenderName,
    string? SenderWorld,
    string Content,
    DateTimeOffset ObservedAtUtc);

public sealed record EnqueueResult(string MessageId, bool Inserted);

public sealed record ObservationResult(bool Inserted);
