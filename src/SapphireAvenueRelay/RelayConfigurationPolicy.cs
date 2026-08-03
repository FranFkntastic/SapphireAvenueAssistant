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
    public static string? NormalizePairingCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = new string(value
            .Where(character => !char.IsWhiteSpace(character) && character != '-')
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalized.Length == 13 &&
               normalized.All(character => character is >= 'A' and <= 'Z' or >= '2' and <= '7')
            ? normalized
            : null;
    }

    public static bool IsNodeIdValid(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    public static bool IsNodeLabelValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 80 &&
        value.All(character => !char.IsControl(character));

    public static bool IsAccessTokenValid(string? value) =>
        value is { Length: 43 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

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
                    : "Connected for game-to-Discord observation; Discord-to-game participation is disabled.");
        }

        if (string.Equals(snapshot.Role, "preferred-active", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(snapshot.Role, "leader", StringComparison.OrdinalIgnoreCase) && snapshot.IsPreferred))
        {
            return new RelayNodeDisplay(
                RelayNodeDisplayState.PreferredActive,
                "Preferred · active",
                "This node currently sends Discord messages into the game.");
        }

        if (string.Equals(snapshot.Role, "leader", StringComparison.OrdinalIgnoreCase))
            return new RelayNodeDisplay(RelayNodeDisplayState.Active, "Active", "This node currently sends Discord messages into the game.");

        return new RelayNodeDisplay(
            RelayNodeDisplayState.Standby,
            "Standby",
            snapshot.IsPreferred
                ? "Preferred; waiting for the current sender's lease to finish."
                : "Connected and ready if the active node goes offline.");
    }
}
