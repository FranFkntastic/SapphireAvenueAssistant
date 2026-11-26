namespace SapphireAvenueAssistant.Relay;

public enum OutboundState
{
    Pending,
    Claimed,
    Sent,
    Ambiguous,

    // Terminal "never transmitted" for channel-origin lines (not-sent or expired before any claim).
    // Interaction-origin not-sent completions still requeue as Pending.
    NotSent
}

public static class OutboundOrigin
{
    public const string Interaction = "interaction";
    public const string Channel = "channel";
}

public sealed record ChannelOutboundSource(string ChannelId, string MessageId);

public sealed record PendingChannelReaction(
    string OutboundMessageId,
    string ChannelId,
    string SourceMessageId,
    string Emoji,
    int AttemptCount);

// Feedback on the source Discord message for ordinary channel-message ingestion.
public static class ChannelReactions
{
    public const string Sent = "✅"; // check mark: the CWLS echo was observed
    public const string Ambiguous = "⚠️"; // warning: may or may not have reached the CWLS
    public const string NotSent = "❌"; // cross: definitely not sent
    public const string RoleRequired = "\U0001F6AB"; // no entry: author lacks the relay role
    public const string Paused = "⏸️"; // pause: the relay is paused
    public const string TooLong = "✂️"; // scissors: too long for one CWLS line
    public const string Expired = "⌛"; // hourglass: no relay picked it up in time

    public static string ForTerminalState(OutboundState state, string? completionReason) => state switch
    {
        OutboundState.Sent => Sent,
        OutboundState.Ambiguous => Ambiguous,
        OutboundState.NotSent when completionReason == "expired" => Expired,
        OutboundState.NotSent => NotSent,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}

public enum ReactionCompletion
{
    Reacted,
    Retry,
    Abandoned
}

// Removal of an older message's check mark once a newer channel message carries its own.
public enum ReactionClearCompletion
{
    Cleared,
    Retry,
    Abandoned
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
