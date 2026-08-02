using Dalamud.Game.Text;
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
}
