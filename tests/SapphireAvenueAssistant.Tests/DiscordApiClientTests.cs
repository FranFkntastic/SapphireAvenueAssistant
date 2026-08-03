using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Discord;
using SapphireAvenueAssistant.Relay;

namespace SapphireAvenueAssistant.Tests;

public sealed class DiscordApiClientTests
{
    [Fact]
    public async Task SuccessfulCreateRequiresAndReturnsDiscordMessageIdentity()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"10000000000000009\"}")
        });

        var result = await client.PublishObservationAsync(CreateWorkItem());

        Assert.Equal(DiscordPublishOutcome.Published, result.Outcome);
        Assert.Equal("10000000000000009", result.MessageId);
    }

    [Fact]
    public async Task RateLimitRejectionIsSafeToRetry()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"retry_after\":1.5}")
        });

        var result = await client.PublishObservationAsync(CreateWorkItem());

        Assert.Equal(DiscordPublishOutcome.RetryableRejection, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(1.5), result.RetryAfter);
    }

    [Fact]
    public async Task ServerErrorRequiresReconciliationInsteadOfBlindRetry()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Discord had a bad day.")
        });

        var result = await client.PublishObservationAsync(CreateWorkItem());

        Assert.Equal(DiscordPublishOutcome.ReconciliationRequired, result.Outcome);
    }

    private static DiscordApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        var httpClient = new HttpClient(new StubHandler(response))
        {
            BaseAddress = new Uri("https://discord.com/api/v10/")
        };
        return new DiscordApiClient(
            httpClient,
            new SapphireAvenueOptions
            {
                Discord = new DiscordOptions
                {
                    PublicKey = new string('0', 64),
                    BotToken = "test-token",
                    ApplicationId = "10000000000000001",
                    GuildId = "10000000000000002",
                    ChannelId = "10000000000000003"
                }
            },
            NullLogger<DiscordApiClient>.Instance);
    }

    private static DiscordPublishWorkItem CreateWorkItem() =>
        new(
            "cwls1:9ac92af4",
            "publish-claim",
            1,
            "10000000000000003",
            1,
            1,
            "Test Friend",
            "Balmung",
            "Hello from the CWLS.",
            DateTimeOffset.Parse("2026-08-02T12:00:00Z"));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
