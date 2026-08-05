using Dalamud.Game.Text;
using SapphireAvenue.BridgeProtocol;
using Xunit;

namespace SapphireAvenueRelay.Tests;

public sealed class RelayBoundaryTests
{
    [Theory]
    [InlineData(1, XivChatType.CrossLinkShell1)]
    [InlineData(2, XivChatType.CrossLinkShell2)]
    [InlineData(8, XivChatType.CrossLinkShell8)]
    public void CwlsSlotsUseExplicitChatTypeMapping(int slot, XivChatType expected)
    {
        Assert.Equal(expected, CwlsChannels.ForSlot(slot));
        Assert.Equal(slot, CwlsChannels.ToSlot(expected));
    }

    [Fact]
    public void DiscordLineIsAttributedAndWhitespaceIsNormalized()
    {
        Assert.Equal(
            "[Discord · Miqo Friend] map train at nine",
            CwlsChannels.FormatDiscordLine("  Miqo   Friend ", " map\r\n train   at nine "));
    }

    [Fact]
    public void DiscordLineFitsTheGameChatByteLimitIncludingCommand()
    {
        var line = CwlsChannels.FormatDiscordLine(new string('N', 80), new string('é', 400));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount("/cwl8 " + line) <= 500);
    }

    [Fact]
    public void NormalizationDoesNotSplitUtf8Runes()
    {
        Assert.Equal("🚀", CwlsChannels.Normalize("🚀🚀", 4));
    }

    [Fact]
    public void ObservationIdentityIsStableButIncludesTimestampAndRawPayloads()
    {
        var first = CwlsChannels.ObservationId(3, 100, "sender"u8, "hello"u8);
        Assert.Equal(first, CwlsChannels.ObservationId(3, 100, "sender"u8, "hello"u8));
        Assert.NotEqual(first, CwlsChannels.ObservationId(3, 101, "sender"u8, "hello"u8));
        Assert.NotEqual(first, CwlsChannels.ObservationId(3, 100, "sender"u8, "hello!"u8));
    }

    [Theory]
    [InlineData("https://relay.example/", true)]
    [InlineData("http://127.0.0.1:5074", true)]
    [InlineData("http://relay.example/", false)]
    [InlineData("relative/path", false)]
    public void CoordinatorTransportRequiresHttpsOrLoopback(string value, bool accepted)
    {
        if (accepted)
        {
            Assert.NotNull(RelayCoordinatorClient.ValidateBaseUri(value));
            return;
        }

        Assert.Throws<InvalidOperationException>(() => RelayCoordinatorClient.ValidateBaseUri(value));
    }

    [Fact]
    public void OneTimePairingCodeIsNeverSentOverLoopbackHttp()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RelayCoordinatorClient.ValidatePairingBaseUri("http://127.0.0.1:5074"));
        Assert.Equal(
            Uri.UriSchemeHttps,
            RelayCoordinatorClient.ValidatePairingBaseUri("https://relay.example/").Scheme);
    }

    [Fact]
    public void HeartbeatPublishesSenderEligibilityOnEveryRequest()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new HeartbeatRequest("runtime-instance", false, "Wei Ning", 40, "Sargatanas"),
            RelayJsonContext.Default.HeartbeatRequest);

        Assert.Equal(
            "{\"instanceId\":\"runtime-instance\",\"canSendToGame\":false,\"characterName\":\"Wei Ning\",\"homeWorldId\":40,\"homeWorldName\":\"Sargatanas\"}",
            json);
    }

    [Fact]
    public void SenderEligibilityRequiresDirectionLoginAndExactCurrentCwls()
    {
        CwlsSlotSnapshot[] exact = [new(1, "Sapphire Ave Company")];
        CwlsSlotSnapshot[] drifted = [new(1, "Hunters of Light"), new(2, "Sapphire Ave Company")];

        var observeOnly = RelayConfigurationPolicy.EvaluateSendEligibility(
            true, false, true, true, 1, "Sapphire Ave Company", exact);
        var loggedOut = RelayConfigurationPolicy.EvaluateSendEligibility(
            true, true, false, false, 1, "Sapphire Ave Company", exact);
        var drift = RelayConfigurationPolicy.EvaluateSendEligibility(
            true, true, true, true, 1, "Sapphire Ave Company", drifted);
        var eligible = RelayConfigurationPolicy.EvaluateSendEligibility(
            true, true, true, true, 1, "Sapphire Ave Company", exact);

        Assert.False(observeOnly.Allowed);
        Assert.False(loggedOut.Allowed);
        Assert.False(drift.Allowed);
        Assert.Null(drift.VerifiedSlot);
        Assert.True(eligible.Allowed);
        Assert.Equal(1, eligible.VerifiedSlot);
        Assert.Equal("Sapphire Ave Company", eligible.VerifiedName);
    }

    [Theory]
    [InlineData("abcd-efgh-ijk2-3", "ABCDEFGHIJK23")]
    [InlineData("ABCD EFGH IJK23", "ABCDEFGHIJK23")]
    [InlineData("ABCDEFGHIJK2", null)]
    [InlineData("ABCDEFGHIJK20", null)]
    public void PairingCodeUsesExactCaseInsensitiveBase32Contract(string value, string? expected)
    {
        Assert.Equal(expected, RelayConfigurationPolicy.NormalizePairingCode(value));
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOP", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOP=", false)]
    public void PairingCredentialRequiresExact256BitBase64UrlShape(string value, bool expected)
    {
        Assert.Equal(expected, RelayConfigurationPolicy.IsAccessTokenValid(value));
    }

    [Fact]
    public void NodeIdentityUsesOnlyCharacterAndHomeWorld()
    {
        Assert.Equal(
            "Mega Phone @ Sargatanas",
            RelayConfigurationPolicy.DisplayNodeIdentity("Mega Phone", "Sargatanas"));
        Assert.Equal(
            "Waiting for a logged-in character",
            RelayConfigurationPolicy.DisplayNodeIdentity(null, null));
    }

    [Fact]
    public void DiscordConnectionStringIsTransparentVersionedAndCanonical()
    {
        var value = RelayConnectionBootstrap.Create(
            "https://relay.example/community",
            "abcd-efgh-ijk2-3");
        var parsed = RelayConnectionBootstrap.Parse(value);

        Assert.Equal("SADB1 https://relay.example/community/ ABCDEFGHIJK23", value);
        Assert.Equal("https://relay.example/community/", parsed.CoordinatorBaseUri.AbsoluteUri);
        Assert.Equal("ABCDEFGHIJK23", parsed.PairingCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEFGHIJK23")]
    [InlineData("SADB2 https://relay.example/ ABCDEFGHIJK23")]
    [InlineData("SADB1 http://relay.example/ ABCDEFGHIJK23")]
    [InlineData("SADB1 https://user:pass@relay.example/ ABCDEFGHIJK23")]
    [InlineData("SADB1 https://relay.example/?redirect=elsewhere ABCDEFGHIJK23")]
    [InlineData("SADB1 https://relay.example/#fragment ABCDEFGHIJK23")]
    [InlineData("SADB1 https://relay.example/ INVALID-CODE")]
    [InlineData("SADB1 https://relay.example/ ABCDEFGHIJK23 extra")]
    public void DiscordConnectionStringRejectsMalformedOrUnsafeValues(string value)
    {
        Assert.Throws<InvalidOperationException>(() => RelayConnectionBootstrap.Parse(value));
    }

    [Fact]
    public void DiscordConnectionStringRejectsOversizedCoordinatorAddress()
    {
        var oversized = $"SADB1 https://relay.example/{new string('a', 901)} ABCDEFGHIJK23";

        Assert.Throws<InvalidOperationException>(() => RelayConnectionBootstrap.Parse(oversized));
    }

    [Fact]
    public void CwlsSelectionRequiresTheExactDiscoveredNameAndSlot()
    {
        CwlsSlotSnapshot[] slots = [new(1, "Hunters of Light"), new(2, "Sapphire Ave Company")];

        Assert.NotNull(RelayConfigurationPolicy.ResolveSelection(slots, 2, "Sapphire Ave Company"));
        Assert.Null(RelayConfigurationPolicy.ResolveSelection(slots, 1, "Sapphire Ave Company"));
        Assert.Null(RelayConfigurationPolicy.ResolveSelection(slots, 2, "sapphire ave company"));
    }

    [Theory]
    [InlineData(false, false, true, true, false, false, "offline", false, "Disabled")]
    [InlineData(true, true, true, true, false, true, "offline", false, "Offline")]
    [InlineData(true, true, false, true, true, false, "observer", false, "Offline")]
    [InlineData(true, true, true, false, true, false, "observer", false, "Offline")]
    [InlineData(true, false, true, true, true, false, "observer", false, "Observer")]
    [InlineData(true, true, true, true, true, false, "leader", true, "Observer")]
    [InlineData(true, true, true, true, true, true, "observer", false, "Standby")]
    [InlineData(true, true, true, true, true, true, "observer", true, "Standby")]
    [InlineData(true, true, true, true, true, true, "leader", false, "Active")]
    [InlineData(true, true, true, true, true, true, "leader", true, "PreferredActive")]
    public void NodeStatusSeparatesObserverFromEligibleStandbyAndActiveStates(
        bool observeEnabled,
        bool deliverEnabled,
        bool loggedIn,
        bool slotMatches,
        bool coordinatorReachable,
        bool canSendToGame,
        string role,
        bool preferred,
        string expected)
    {
        var snapshot = new RelaySnapshot(
            "test",
            loggedIn,
            "Wei Ning",
            "Sargatanas",
            [new CwlsSlotSnapshot(1, "Sapphire Ave Company")],
            1,
            "Sapphire Ave Company",
            slotMatches ? "Sapphire Ave Company" : "Hunters of Light",
            slotMatches,
            observeEnabled,
            deliverEnabled,
            canSendToGame,
            true,
            coordinatorReachable,
            role,
            preferred,
            1,
            DateTimeOffset.UtcNow.AddSeconds(10),
            0,
            null,
            coordinatorReachable ? null : "Connection refused.");

        var display = RelayConfigurationPolicy.Describe(snapshot);
        Assert.Equal(expected, display.State.ToString());
        if (expected == "Observer")
        {
            Assert.Equal("Observer · not eligible", display.Label);
            Assert.DoesNotContain("ready", display.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }
}
