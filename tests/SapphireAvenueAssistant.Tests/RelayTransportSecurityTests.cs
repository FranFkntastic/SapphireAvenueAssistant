using System.Net;
using Microsoft.AspNetCore.Http;
using SapphireAvenueAssistant.Relay;

namespace SapphireAvenueAssistant.Tests;

public sealed class RelayTransportSecurityTests
{
    [Fact]
    public void PairingAcceptsDirectHttps()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");

        Assert.True(RelayEndpoints.IsSecurePairingTransport(context.Request));
    }

    [Fact]
    public void PairingAcceptsForwardedHttpsOnlyFromLoopbackProxy()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        Assert.True(RelayEndpoints.IsSecurePairingTransport(context.Request));
    }

    [Fact]
    public void PairingRejectsPlainHttpAndSpoofedForwardedHttps()
    {
        var plain = new DefaultHttpContext();
        plain.Request.Scheme = "http";
        plain.Connection.RemoteIpAddress = IPAddress.Loopback;

        var spoofed = new DefaultHttpContext();
        spoofed.Request.Scheme = "http";
        spoofed.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        spoofed.Request.Headers["X-Forwarded-Proto"] = "https";

        Assert.False(RelayEndpoints.IsSecurePairingTransport(plain.Request));
        Assert.False(RelayEndpoints.IsSecurePairingTransport(spoofed.Request));
    }
}
