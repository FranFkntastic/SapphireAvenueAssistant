using System.Text;
using System.Text.Json;
using System.Net;
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

    [Fact]
    public async Task BridgeManagementRechecksPermissionRejectsWrongIdentityAndControlsCwls()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"sapphire-avenue-bridge-{Guid.NewGuid():N}.db");
        var seed = Enumerable.Range(33, 32).Select(value => (byte)value).ToArray();
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
                DatabasePath = databasePath,
                NodeTokens = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["relay-a"] = "token-a",
                    ["relay-b"] = "token-b"
                }
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
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        string BridgePayload(string id, string permissions, string guildId, string applicationId, object subcommand) =>
            JsonSerializer.Serialize(new
            {
                id,
                application_id = applicationId,
                type = 2,
                guild_id = guildId,
                channel_id = "10000000000000003",
                member = new
                {
                    permissions,
                    roles = Array.Empty<string>(),
                    user = new { id = "10000000000000005", username = "manager" }
                },
                data = new { name = "bridge", options = new[] { subcommand } }
            });

        var configure = BridgePayload(
            "10000000000000201", "32", "10000000000000002", "10000000000000001",
            new
            {
                name = "configure",
                type = 1,
                options = new object[]
                {
                    new { name = "channel", type = 7, value = "10000000000000003" },
                    new { name = "role", type = 8, value = "10000000000000007" }
                }
            });
        using var configured = await PostSignedAsync(client, configure, timestamp, expandedPrivateKey);
        using var replay = await PostSignedAsync(client, configure, timestamp, expandedPrivateKey);
        Assert.True(configured.IsSuccessStatusCode);
        Assert.True(replay.IsSuccessStatusCode);
        var stored = await factory.Services.GetRequiredService<RelayStore>()
            .GetCommunityConfigurationAsync("10000000000000002");
        Assert.Equal(2, stored!.Revision);
        Assert.Equal("10000000000000007", stored.AllowedRoleId);

        var store = factory.Services.GetRequiredService<RelayStore>();
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        await store.HeartbeatAsync(
            "relay-a", "instance-a", now,
            canSendToGame: false,
            identity: new RelayNodeIdentity("Wei Ning", 40, "Sargatanas"));
        await store.HeartbeatAsync(
            "relay-b", "instance-b", now,
            canSendToGame: false,
            identity: new RelayNodeIdentity("Mega Phone", 40, "Sargatanas"));

        string AutocompletePayload(string id, string permissions, string guild, string query) =>
            JsonSerializer.Serialize(new
            {
                id,
                application_id = "10000000000000001",
                type = 4,
                guild_id = guild,
                channel_id = "10000000000000003",
                member = new
                {
                    permissions,
                    roles = Array.Empty<string>(),
                    user = new { id = "10000000000000005", username = "manager" }
                },
                data = new
                {
                    name = "bridge",
                    options = new[]
                    {
                        new
                        {
                            name = "prefer-node",
                            type = 1,
                            options = new[]
                            {
                                new { name = "node", type = 3, value = query, focused = true }
                            }
                        }
                    }
                }
            });

        using var autocomplete = await PostSignedAsync(
            client,
            AutocompletePayload("10000000000000211", "32", "10000000000000002", "Wei"),
            timestamp,
            expandedPrivateKey);
        using var autocompleteBody = JsonDocument.Parse(await autocomplete.Content.ReadAsStringAsync());
        var choices = autocompleteBody.RootElement.GetProperty("data").GetProperty("choices");
        Assert.Equal(8, autocompleteBody.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(1, choices.GetArrayLength());
        Assert.Equal("Wei Ning @ Sargatanas", choices[0].GetProperty("name").GetString());
        Assert.Equal("relay-a", choices[0].GetProperty("value").GetString());

        using var forbiddenAutocomplete = await PostSignedAsync(
            client,
            AutocompletePayload("10000000000000212", "0", "10000000000000002", string.Empty),
            timestamp,
            expandedPrivateKey);
        using var forbiddenAutocompleteBody = JsonDocument.Parse(await forbiddenAutocomplete.Content.ReadAsStringAsync());
        Assert.Equal(0, forbiddenAutocompleteBody.RootElement.GetProperty("data").GetProperty("choices").GetArrayLength());

        using var wrongGuildAutocomplete = await PostSignedAsync(
            client,
            AutocompletePayload("10000000000000213", "32", "10000000000000999", string.Empty),
            timestamp,
            expandedPrivateKey);
        using var wrongGuildAutocompleteBody = JsonDocument.Parse(await wrongGuildAutocomplete.Content.ReadAsStringAsync());
        Assert.Equal(0, wrongGuildAutocompleteBody.RootElement.GetProperty("data").GetProperty("choices").GetArrayLength());

        var prefer = BridgePayload(
            "10000000000000214", "32", "10000000000000002", "10000000000000001",
            new
            {
                name = "prefer-node",
                type = 1,
                options = new[] { new { name = "node", type = 3, value = "relay-a" } }
            });
        using var preferredResponse = await PostSignedAsync(client, prefer, timestamp, expandedPrivateKey);
        var preferredBody = await preferredResponse.Content.ReadAsStringAsync();
        Assert.Contains("Wei Ning @ Sargatanas", preferredBody, StringComparison.Ordinal);
        Assert.DoesNotContain("relay-a", preferredBody, StringComparison.Ordinal);

        var forbidden = BridgePayload(
            "10000000000000202", "0", "10000000000000002", "10000000000000001",
            new { name = "status", type = 1 });
        using var forbiddenResponse = await PostSignedAsync(client, forbidden, timestamp, expandedPrivateKey);
        Assert.Contains("Manage Server", await forbiddenResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var wrongGuild = BridgePayload(
            "10000000000000203", "32", "10000000000000999", "10000000000000001",
            new { name = "status", type = 1 });
        using var wrongGuildResponse = await PostSignedAsync(client, wrongGuild, timestamp, expandedPrivateKey);
        Assert.Contains("not connected", await wrongGuildResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var wrongApplication = BridgePayload(
            "10000000000000204", "32", "10000000000000002", "10000000000000999",
            new { name = "status", type = 1 });
        using var wrongApplicationResponse = await PostSignedAsync(client, wrongApplication, timestamp, expandedPrivateKey);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongApplicationResponse.StatusCode);

        var cwls = JsonSerializer.Serialize(new
        {
            id = "10000000000000205",
            application_id = "10000000000000001",
            type = 2,
            guild_id = "10000000000000002",
            channel_id = "10000000000000003",
            member = new
            {
                roles = new[] { "10000000000000007" },
                user = new { id = "10000000000000008", username = "affiliate" }
            },
            data = new
            {
                name = "cwls",
                options = new[] { new { name = "message", value = "Hello CWLS" } }
            }
        });
        using var allowedCwls = await PostSignedAsync(client, cwls, timestamp, expandedPrivateKey);
        Assert.Contains("Queued", await allowedCwls.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var pause = BridgePayload(
            "10000000000000206", "32", "10000000000000002", "10000000000000001",
            new
            {
                name = "pause",
                type = 1,
                options = new[] { new { name = "paused", type = 5, value = true } }
            });
        using var paused = await PostSignedAsync(client, pause, timestamp, expandedPrivateKey);
        Assert.Contains("paused", await paused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var pausedCwlsPayload = cwls.Replace("10000000000000205", "10000000000000207", StringComparison.Ordinal);
        using var pausedCwls = await PostSignedAsync(client, pausedCwlsPayload, timestamp, expandedPrivateKey);
        Assert.Contains("paused", await pausedCwls.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

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
