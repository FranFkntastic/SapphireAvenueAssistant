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
    bool Authorized,
    bool IsLeader,
    bool IsPreferred,
    long Epoch,
    DateTimeOffset ExpiresAtUtc,
    string? IdentityConflict = null);

public sealed record RelayNodeIdentity(
    string CharacterName,
    long HomeWorldId,
    string HomeWorldName)
{
    public string DisplayName => $"{CharacterName} @ {HomeWorldName}";
}

public sealed record OutboundRelayMessage(
    string MessageId,
    string ClaimId,
    string DiscordUserId,
    string DiscordDisplayName,
    string Content,
    DateTimeOffset CreatedAtUtc);

public sealed record OutboundClaimResult(
    bool NodeActive,
    bool Authorized,
    OutboundRelayMessage? Message);

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
    string ChannelId,
    long ConfigurationRevision,
    int CwlsSlot,
    string SenderName,
    string? SenderWorld,
    string Content,
    DateTimeOffset ObservedAtUtc);

public enum DiscordPublishRouteCheck
{
    Current,
    Requeued,
    ClaimLost
}

public sealed record EnqueueResult(string MessageId, bool Inserted);

public enum CwlsEnqueueRefusal
{
    None,
    NotConfigured,
    Paused,
    WrongChannel,
    RoleRequired
}

public sealed record CwlsEnqueueResult(
    CwlsEnqueueRefusal Refusal,
    string? MessageId = null,
    bool Inserted = false);

public sealed record ObservationResult(bool NodeActive, bool LeaderAuthorized, bool Inserted);

public enum NodeMutationResult
{
    Unauthorized,
    Conflict,
    Completed
}

public sealed record CommunityRelayConfiguration(
    string GuildId,
    string ChannelId,
    string? AllowedRoleId,
    bool IsPaused,
    string? PreferredNodeId,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record RelayNodeStatus(
    string NodeId,
    string? CharacterName,
    long? HomeWorldId,
    string? HomeWorldName,
    bool IsPaired,
    bool CapabilityReported,
    bool CanSendToGame,
    bool IsRevoked,
    bool IsPreferred,
    bool IsLeader,
    DateTimeOffset? LastSeenAtUtc);

public sealed record RelayNodeChoice(string NodeId, string DisplayName);

public enum BridgeManagementAction
{
    Status,
    ListNodes,
    Configure,
    SetChannel,
    SetRole,
    Pause,
    Resume,
    PreferNode,
    ClearPreference,
    RevokeNode,
    AddNode
}

public sealed record BridgeManagementRequest(
    string InteractionId,
    string GuildId,
    string ActorDiscordUserId,
    BridgeManagementAction Action,
    string? ChannelId = null,
    string? RoleId = null,
    string? NodeId = null);

public sealed record BridgeManagementResult(
    bool Succeeded,
    bool Replayed,
    bool Conflict,
    string Response);

public sealed record PairingExchangeResult(
    string NodeId,
    string AccessToken);
