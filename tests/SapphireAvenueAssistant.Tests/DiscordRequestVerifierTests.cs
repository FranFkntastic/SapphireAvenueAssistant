using System.Text;
using Chaos.NaCl;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Discord;

namespace SapphireAvenueAssistant.Tests;

public sealed class DiscordRequestVerifierTests
{
    [Fact]
    public void AcceptsCurrentSignatureAndRejectsStaleReplay()
    {
        var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        var seed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        Ed25519.KeyPairFromSeed(out var publicKey, out var expandedPrivateKey, seed);
        var options = new SapphireAvenueOptions
        {
            Discord = new DiscordOptions
            {
                PublicKey = Convert.ToHexString(publicKey),
                ApplicationId = "10000000000000001",
                GuildId = "10000000000000002",
                ChannelId = "10000000000000003"
            }
        };
        var verifier = new DiscordRequestVerifier(options, new FixedTimeProvider(now));
        var body = Encoding.UTF8.GetBytes("{\"type\":1}");
        var currentTimestamp = now.ToUnixTimeSeconds().ToString();
        var staleTimestamp = now.AddMinutes(-6).ToUnixTimeSeconds().ToString();

        Assert.True(verifier.Verify(
            currentTimestamp,
            Sign(currentTimestamp, body, expandedPrivateKey),
            body));
        Assert.False(verifier.Verify(
            staleTimestamp,
            Sign(staleTimestamp, body, expandedPrivateKey),
            body));
    }

    private static string Sign(string timestamp, byte[] body, byte[] expandedPrivateKey)
    {
        var timestampBytes = Encoding.ASCII.GetBytes(timestamp);
        var payload = new byte[timestampBytes.Length + body.Length];
        timestampBytes.CopyTo(payload, 0);
        body.CopyTo(payload, timestampBytes.Length);
        return Convert.ToHexString(Ed25519.Sign(payload, expandedPrivateKey));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
