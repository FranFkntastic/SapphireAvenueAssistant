using System.Text;
using System.Text.Json;
using Chaos.NaCl;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Relay;

namespace SapphireAvenueAssistant.Tests;

public sealed class DiscordInteractionEndpointTests
{
    [Fact]
    public async Task SignedCommandIsAcknowledgedEphemerallyAndDeduplicated()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"sapphire-avenue-endpoint-{Guid.NewGuid():N}.db");
        var seed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        Ed25519.KeyPairFromSeed(out var publicKey, out var expandedPrivateKey, seed);
        var testOptions = new SapphireAvenueOptions
        {
            Discord = new DiscordOptions
            {
                PublicKey = Convert.ToHexString(publicKey),
                ApplicationId = "10000000000000001",
                GuildId = "10000000000000002",
                ChannelId = "10000000000000003"
            },
            Relay = new RelayOptions
            {
                DatabasePath = databasePath
            }
        };
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<SapphireAvenueOptions>();
                services.RemoveAll<RelayStore>();
                services.AddSingleton(testOptions);
                services.AddSingleton<RelayStore>();
            }));
        using var client = factory.CreateClient();
        var configured = factory.Services.GetRequiredService<SapphireAvenueOptions>().Discord;
        Assert.True(
            configured.CanVerifyInteractions,
            $"key={configured.PublicKey.Length}, app={configured.ApplicationId}, guild={configured.GuildId}, channel={configured.ChannelId}");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var payload = JsonSerializer.Serialize(new
        {
            id = "10000000000000004",
            application_id = "10000000000000001",
            type = 2,
            guild_id = "10000000000000002",
            channel_id = "10000000000000003",
            member = new
            {
                nick = "Test Friend",
                roles = Array.Empty<string>(),
                user = new
                {
                    id = "10000000000000005",
                    username = "testfriend"
                }
            },
            data = new
            {
                name = "cwls",
                options = new[]
                {
                    new
                    {
                        name = "message",
                        value = "Maps at nine!"
                    }
                }
            }
        });

        using var first = await PostSignedAsync(client, payload, timestamp, expandedPrivateKey);
        using var duplicate = await PostSignedAsync(client, payload, timestamp, expandedPrivateKey);
        var firstBody = await first.Content.ReadAsStringAsync();
        var duplicateBody = await duplicate.Content.ReadAsStringAsync();

        Assert.True(first.IsSuccessStatusCode, $"{first.StatusCode}: {firstBody}");
        Assert.True(duplicate.IsSuccessStatusCode, $"{duplicate.StatusCode}: {duplicateBody}");
        using var response = JsonDocument.Parse(firstBody);
        Assert.Equal(4, response.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(64, response.RootElement.GetProperty("data").GetProperty("flags").GetInt32());
        var store = factory.Services.GetRequiredService<RelayStore>();
        Assert.Equal(1, await store.CountOutboundByInteractionAsync("10000000000000004"));

        await factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<HttpResponseMessage> PostSignedAsync(
        HttpClient client,
        string payload,
        string timestamp,
        byte[] expandedPrivateKey)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var timestampBytes = Encoding.ASCII.GetBytes(timestamp);
        var signedPayload = new byte[timestampBytes.Length + body.Length];
        timestampBytes.CopyTo(signedPayload, 0);
        body.CopyTo(signedPayload, timestampBytes.Length);
        var request = new HttpRequestMessage(HttpMethod.Post, "/discord/interactions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Signature-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation(
            "X-Signature-Ed25519",
            Convert.ToHexString(Ed25519.Sign(signedPayload, expandedPrivateKey)));
        return await client.SendAsync(request);
    }
}
