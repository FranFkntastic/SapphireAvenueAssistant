using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Discord;

namespace SapphireAvenueAssistant.Tests;

public sealed class DiscordCommandRegistrationServiceTests
{
    [Fact]
    public async Task ReconcileUpsertsOnlyNamedGuildCommandsAndDoesNotDuplicateThem()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        var getCount = 0;
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            if (request.Method == HttpMethod.Get)
            {
                getCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(getCount == 1
                        ? "[{\"id\":\"10000000000000009\",\"name\":\"relay-chat\"},{\"id\":\"10000000000000010\",\"name\":\"unrelated\"}]"
                        : "[{\"id\":\"10000000000000009\",\"name\":\"relay-chat\"},{\"id\":\"10000000000000011\",\"name\":\"bridge\"},{\"id\":\"10000000000000010\",\"name\":\"unrelated\"}]")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }))
        {
            BaseAddress = new Uri("https://discord.com/api/v10/")
        };
        var service = new DiscordCommandRegistrationService(
            httpClient,
            new SapphireAvenueOptions
            {
                Discord = new DiscordOptions
                {
                    BotToken = "test-token",
                    ApplicationId = "10000000000000001",
                    GuildId = "10000000000000002",
                    CommandName = "relay-chat"
                }
            },
            TimeProvider.System,
            NullLogger<DiscordCommandRegistrationService>.Instance);

        await service.ReconcileAsync();
        await service.ReconcileAsync();

        Assert.Equal("ready", service.Status);
        Assert.Contains(requests, request => request.Body.Contains("\"name\":\"relay-chat\"", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, request => request.Body.Contains("\"name\":\"cwls\"", StringComparison.Ordinal));
        Assert.Single(requests, request => request.Method == HttpMethod.Post);
        Assert.DoesNotContain(requests, request => request.Method is { } method &&
            (method == HttpMethod.Delete || method == HttpMethod.Put));
        Assert.All(
            requests.Where(request => request.Method != HttpMethod.Get),
            request => Assert.StartsWith(
                "/api/v10/applications/10000000000000001/guilds/10000000000000002/commands",
                request.Path,
                StringComparison.Ordinal));
        var bridgeRequests = requests
            .Where(request => request.Body.Contains("\"name\":\"bridge\"", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, bridgeRequests.Length);
        Assert.All(bridgeRequests, bridge =>
        {
            Assert.Contains("\"default_member_permissions\":\"32\"", bridge.Body, StringComparison.Ordinal);
            Assert.Contains("\"name\":\"add-node\"", bridge.Body, StringComparison.Ordinal);
            Assert.Contains("\"name\":\"pause\"", bridge.Body, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(requests, request => request.Path.Contains("10000000000000010", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostedServiceAndHealthResolveTheSameRegistrationInstance()
    {
        await using var factory = new WebApplicationFactory<Program>();
        _ = factory.CreateClient();

        var first = factory.Services.GetRequiredService<DiscordCommandRegistrationService>();
        var second = factory.Services.GetRequiredService<DiscordCommandRegistrationService>();

        Assert.Same(first, second);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request);
    }
}
