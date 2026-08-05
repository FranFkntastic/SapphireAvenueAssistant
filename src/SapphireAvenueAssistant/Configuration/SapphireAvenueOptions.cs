using SapphireAvenue.BridgeProtocol;

namespace SapphireAvenueAssistant.Configuration;

public sealed class SapphireAvenueOptions
{
    public DiscordOptions Discord { get; init; } = new();

    public RelayOptions Relay { get; init; } = new();
}

public sealed class DiscordOptions
{
    public string PublicKey { get; init; } = string.Empty;

    public string BotToken { get; init; } = string.Empty;

    public string ApplicationId { get; init; } = string.Empty;

    public string GuildId { get; init; } = string.Empty;

    public string ChannelId { get; init; } = string.Empty;

    public string CommandName { get; init; } = "cwls";

    public string[] AllowedRoleIds { get; init; } = [];

    public bool CanVerifyInteractions =>
        PublicKey.Length == 64 && PublicKey.All(Uri.IsHexDigit) &&
        IsSnowflake(ApplicationId) &&
        IsSnowflake(GuildId);

    public bool CanPublish => CanVerifyInteractions && !string.IsNullOrWhiteSpace(BotToken);

    public bool CanRegisterCommands =>
        IsSnowflake(ApplicationId) &&
        IsSnowflake(GuildId) &&
        !string.IsNullOrWhiteSpace(BotToken);

    public static bool IsSnowflake(string value) =>
        value.Length is >= 17 and <= 20 && value.All(char.IsAsciiDigit);
}

public sealed class RelayOptions
{
    public string DatabasePath { get; init; } = "data/sapphire-avenue.db";

    public string PublicBaseUrl { get; init; } = string.Empty;

    public int LeaderLeaseSeconds { get; init; } = 20;

    public int ClaimLeaseSeconds { get; init; } = 30;

    public int PublishLeaseSeconds { get; init; } = 30;

    public int MaximumMessageBytes { get; init; } = 400;

    public bool AllowLegacyHeartbeatWithoutCapability { get; init; } = true;

    public Dictionary<string, string> NodeTokens { get; init; } = new(StringComparer.Ordinal);

    public TimeSpan LeaderLeaseDuration => TimeSpan.FromSeconds(Math.Clamp(LeaderLeaseSeconds, 5, 120));

    public TimeSpan ClaimLeaseDuration => TimeSpan.FromSeconds(Math.Clamp(ClaimLeaseSeconds, 5, 300));

    public TimeSpan PublishLeaseDuration => TimeSpan.FromSeconds(Math.Clamp(PublishLeaseSeconds, 5, 300));

    public int BoundedMaximumMessageBytes => Math.Clamp(MaximumMessageBytes, 64, 500);

    public bool CanIssueConnectionStrings
    {
        get
        {
            try
            {
                _ = RelayConnectionBootstrap.ParseCoordinatorBaseUri(PublicBaseUrl);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
