using System.Text.Json;
using System.Text.Json.Serialization;

namespace SapphireAvenueRelay;

internal sealed record HeartbeatRequest(
    string InstanceId,
    bool CanSendToGame,
    string? CharacterName,
    uint? HomeWorldId,
    string? HomeWorldName);
internal sealed record HeartbeatResponse(
    string Role,
    long Epoch,
    DateTimeOffset ExpiresAtUtc,
    bool IsPreferred = false);
internal sealed record PairNodeRequest(string PairingCode);
internal sealed record PairNodeResponse(string NodeId, string AccessToken);
internal sealed record RelayPairingResult(string CoordinatorBaseUrl, string NodeId, string AccessToken);
internal sealed record ClaimRequest(string InstanceId, long Epoch);
internal sealed record OutboundRelayMessage(
    string MessageId,
    string ClaimId,
    string DiscordUserId,
    string DiscordDisplayName,
    string Content,
    DateTimeOffset CreatedAtUtc);

internal enum DeliveryOutcome
{
    Sent,
    NotSent,
    Ambiguous,
}

internal sealed record CompletionRequest(string InstanceId, long Epoch, string ClaimId, DeliveryOutcome Outcome);
internal sealed record ObservationRequest(
    string ObservationId,
    int CwlsSlot,
    string SenderName,
    string? SenderWorld,
    string Content,
    DateTimeOffset ObservedAtUtc);

internal sealed record ObservationEnvelope(
    string ObservationId,
    int CwlsSlot,
    string SenderName,
    string? SenderWorld,
    string Content,
    DateTimeOffset ObservedAtUtc);

internal sealed record ObservationOutbox(List<ObservationEnvelope> Items);

internal sealed record RelaySnapshot(
    string Schema,
    bool IsLoggedIn,
    string? Character,
    string? HomeWorld,
    IReadOnlyList<CwlsSlotSnapshot> CwlsSlots,
    int ConfiguredSlot,
    string ExpectedCwlsName,
    string? ActualCwlsName,
    bool SlotMatches,
    bool ObserveToDiscordEnabled,
    bool DiscordToGameEnabled,
    bool CanSendToGame,
    bool CoordinatorConfigured,
    bool CoordinatorReachable,
    string Role,
    bool IsPreferred,
    long Epoch,
    DateTimeOffset? LeaseExpiresAtUtc,
    int PendingObservationCount,
    PendingSendSnapshot? PendingSend,
    string? LastError);

internal sealed record CwlsSlotSnapshot(int Slot, string Name);
internal sealed record PendingSendSnapshot(string MessageId, string ClaimId, string EchoText, DateTimeOffset StartedAtUtc);

internal sealed record ConfigureRelayArguments(
    string? CoordinatorBaseUrl,
    string? NodeId,
    string? NodeToken,
    int CwlsSlot,
    string? ExpectedCwlsName);

internal sealed record SetDirectionsArguments(bool ObserveToDiscord, bool DiscordToGame);
internal sealed record SendTestArguments(string? Message);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HeartbeatRequest))]
[JsonSerializable(typeof(HeartbeatResponse))]
[JsonSerializable(typeof(PairNodeRequest))]
[JsonSerializable(typeof(PairNodeResponse))]
[JsonSerializable(typeof(ClaimRequest))]
[JsonSerializable(typeof(OutboundRelayMessage))]
[JsonSerializable(typeof(CompletionRequest))]
[JsonSerializable(typeof(ObservationRequest))]
[JsonSerializable(typeof(ObservationOutbox))]
internal partial class RelayJsonContext : JsonSerializerContext;
