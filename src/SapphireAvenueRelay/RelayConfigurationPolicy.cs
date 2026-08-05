using SapphireAvenue.BridgeProtocol;

namespace SapphireAvenueRelay;

internal enum RelayNodeDisplayState
{
    Disabled,
    Offline,
    Observer,
    Standby,
    Active,
    PreferredActive,
}

internal sealed record RelayNodeDisplay(RelayNodeDisplayState State, string Label, string Detail);
internal sealed record RelaySendEligibility(
    bool Allowed,
    string Reason,
    int? VerifiedSlot,
    string? VerifiedName);

internal static class RelayConfigurationPolicy
{
    public static bool MayReportObservation(
        string role,
        DateTimeOffset? leaseExpiresAtUtc,
        DateTimeOffset now) =>
        string.Equals(role, "leader", StringComparison.OrdinalIgnoreCase) &&
        leaseExpiresAtUtc > now;

    public static bool MaySubmitObservation(
        ObservationEnvelope observation,
        string runtimeInstanceId,
        long currentEpoch,
        string role,
        DateTimeOffset? leaseExpiresAtUtc,
        DateTimeOffset now) =>
        MayReportObservation(role, leaseExpiresAtUtc, now) &&
        string.Equals(observation.InstanceId, runtimeInstanceId, StringComparison.Ordinal) &&
        observation.Epoch == currentEpoch;

    public static string? NormalizePairingCode(string? value) =>
        RelayConnectionBootstrap.NormalizePairingCode(value);

    public static bool IsNodeIdValid(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    public static bool IsAccessTokenValid(string? value) =>
        value is { Length: 43 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static string DisplayNodeIdentity(string? characterName, string? homeWorldName) =>
        string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(homeWorldName)
            ? "Waiting for a logged-in character"
            : $"{characterName} @ {homeWorldName}";

    public static bool IsCoordinatorConfigured(RelayConfiguration configuration)
    {
        try
        {
            _ = RelayCoordinatorClient.ValidateBaseUri(configuration.CoordinatorBaseUrl);
            return IsNodeIdValid(configuration.NodeId) &&
                   !string.IsNullOrWhiteSpace(configuration.RelayProtectedAccessToken);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static CwlsSlotSnapshot? ResolveSelection(
        IReadOnlyList<CwlsSlotSnapshot> discoveredSlots,
        int slot,
        string? name) =>
        discoveredSlots.FirstOrDefault(candidate =>
            candidate.Slot == slot &&
            !string.IsNullOrWhiteSpace(candidate.Name) &&
            string.Equals(candidate.Name, name, StringComparison.Ordinal));

    public static RelaySendEligibility EvaluateSendEligibility(
        bool requireDirection,
        bool discordToGameEnabled,
        bool isLoggedIn,
        bool hasLocalPlayer,
        int configuredSlot,
        string? expectedName,
        IReadOnlyList<CwlsSlotSnapshot> discoveredSlots)
    {
        if (requireDirection && !discordToGameEnabled)
            return new RelaySendEligibility(false, "Discord-to-game participation is disabled.", null, null);
        if (!isLoggedIn || !hasLocalPlayer)
            return new RelaySendEligibility(false, "No logged-in local character is available.", null, null);
        if (configuredSlot is < 1 or > 8 || string.IsNullOrWhiteSpace(expectedName))
            return new RelaySendEligibility(false, "An explicit CWLS selection is required.", null, null);

        var actualName = discoveredSlots.FirstOrDefault(value => value.Slot == configuredSlot)?.Name;
        return !string.IsNullOrWhiteSpace(actualName) &&
               string.Equals(actualName, expectedName, StringComparison.Ordinal)
            ? new RelaySendEligibility(true, "CWLS identity verified.", configuredSlot, expectedName)
            : new RelaySendEligibility(
                false,
                $"The saved CWLS '{expectedName}' is no longer in its configured position.",
                null,
                null);
    }

    public static RelayNodeDisplay Describe(RelaySnapshot snapshot)
    {
        var participating = snapshot.ObserveToDiscordEnabled || snapshot.DiscordToGameEnabled;
        if (!participating)
        {
            return new RelayNodeDisplay(
                RelayNodeDisplayState.Disabled,
                "Disabled",
                snapshot.CoordinatorConfigured
                    ? "Paired; choose at least one relay direction to participate."
                    : "Pair this installation to begin.");
        }

        if (!snapshot.CoordinatorConfigured)
            return new RelayNodeDisplay(RelayNodeDisplayState.Offline, "Offline", "This installation is not paired.");
        if (!snapshot.IsLoggedIn)
            return new RelayNodeDisplay(
                RelayNodeDisplayState.Offline,
                "Offline",
                "Log in on the configured character to participate.");
        if (!snapshot.SlotMatches)
            return new RelayNodeDisplay(
                RelayNodeDisplayState.Offline,
                "Offline",
                string.IsNullOrWhiteSpace(snapshot.ExpectedCwlsName)
                    ? "Select a cross-world linkshell."
                    : $"'{snapshot.ExpectedCwlsName}' is no longer in its saved position. Select it again to resume.");
        if (!snapshot.CoordinatorReachable || string.Equals(snapshot.Role, "offline", StringComparison.OrdinalIgnoreCase))
            return new RelayNodeDisplay(
                RelayNodeDisplayState.Offline,
                "Offline",
                snapshot.LastError ?? "The coordinator cannot be reached.");

        if (!snapshot.CanSendToGame)
        {
            return new RelayNodeDisplay(
                RelayNodeDisplayState.Observer,
                "Observer · not eligible",
                snapshot.DiscordToGameEnabled
                    ? "Connected, but local game state isn't currently eligible to send into the game."
                    : "Connected, but this node cannot lead while Discord-to-game participation is disabled.");
        }

        if (string.Equals(snapshot.Role, "preferred-active", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(snapshot.Role, "leader", StringComparison.OrdinalIgnoreCase) && snapshot.IsPreferred))
        {
            return new RelayNodeDisplay(
                RelayNodeDisplayState.PreferredActive,
                "Preferred · active",
                "This node currently owns both relay directions.");
        }

        if (string.Equals(snapshot.Role, "leader", StringComparison.OrdinalIgnoreCase))
            return new RelayNodeDisplay(RelayNodeDisplayState.Active, "Active", "This node currently owns both relay directions.");

        return new RelayNodeDisplay(
            RelayNodeDisplayState.Standby,
            "Standby",
            snapshot.IsPreferred
                ? "Preferred; waiting for the current sender's lease to finish."
                : "Connected and ready if the active node goes offline.");
    }
}
