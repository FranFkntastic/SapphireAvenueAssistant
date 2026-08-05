using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Relay;

namespace SapphireAvenueAssistant.Tests;

public sealed class RelayObservationEndpointTests
{
    [Fact]
    public async Task StandbyObservationIsRejectedBeforePersistence()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"sapphire-avenue-observation-endpoint-{Guid.NewGuid():N}.db");
        var options = new SapphireAvenueOptions
        {
            Discord = new DiscordOptions
            {
                GuildId = "10000000000000002",
                ChannelId = "10000000000000003"
            },
            Relay = new RelayOptions
            {
                DatabasePath = databasePath,
                NodeTokens = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["relay-a"] = "token-a",
                    ["relay-b"] = "token-b"
                }
            }
        };

        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<SapphireAvenueOptions>();
                    services.RemoveAll<RelayStore>();
                    services.AddSingleton(options);
                    services.AddSingleton<RelayStore>();
                }));
            using var client = factory.CreateClient();

            var leaderHeartbeat = await PostAsNodeAsync(
                client,
                "relay-a",
                "token-a",
                "heartbeat",
                new { instanceId = "instance-a", canSendToGame = true });
            var leaderBody = await leaderHeartbeat.Content.ReadFromJsonAsync<JsonElement>();
            var epoch = leaderBody.GetProperty("epoch").GetInt64();
            Assert.Equal("leader", leaderBody.GetProperty("role").GetString());

            var standbyHeartbeat = await PostAsNodeAsync(
                client,
                "relay-b",
                "token-b",
                "heartbeat",
                new { instanceId = "instance-b", canSendToGame = true });
            Assert.Equal(HttpStatusCode.OK, standbyHeartbeat.StatusCode);

            var standby = await PostAsNodeAsync(
                client,
                "relay-b",
                "token-b",
                "observations",
                Observation("instance-b", epoch, "standby:rejected"));
            var legacy = await PostAsNodeAsync(
                client,
                "relay-a",
                "token-a",
                "observations",
                new
                {
                    observationId = "legacy:rejected",
                    cwlsSlot = 1,
                    senderName = "CWLS Friend",
                    content = "Test line",
                    observedAtUtc = DateTimeOffset.UtcNow
                });
            var leader = await PostAsNodeAsync(
                client,
                "relay-a",
                "token-a",
                "observations",
                Observation("instance-a", epoch, "leader:accepted"));

            Assert.Equal(HttpStatusCode.Conflict, standby.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);
            Assert.Equal(HttpStatusCode.OK, leader.StatusCode);
            var store = factory.Services.GetRequiredService<RelayStore>();
            Assert.Null(await store.GetPublicationStateAsync("standby:rejected"));
            Assert.Null(await store.GetPublicationStateAsync("legacy:rejected"));
            Assert.Equal(
                PublicationState.Pending,
                await store.GetPublicationStateAsync("leader:accepted"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private static object Observation(string instanceId, long epoch, string observationId) => new
    {
        instanceId,
        epoch,
        observationId,
        cwlsSlot = 1,
        senderName = "CWLS Friend",
        senderWorld = "Siren",
        content = "Test line",
        observedAtUtc = DateTimeOffset.UtcNow
    };

    private static async Task<HttpResponseMessage> PostAsNodeAsync(
        HttpClient client,
        string nodeId,
        string token,
        string route,
        object body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/relay/v1/nodes/{nodeId}/{route}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
