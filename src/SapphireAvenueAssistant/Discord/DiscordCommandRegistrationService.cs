using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SapphireAvenueAssistant.Configuration;

namespace SapphireAvenueAssistant.Discord;

public sealed class DiscordCommandRegistrationService(
    HttpClient httpClient,
    SapphireAvenueOptions options,
    TimeProvider timeProvider,
    ILogger<DiscordCommandRegistrationService> logger) : BackgroundService
{
    public string Status { get; private set; } = "pending";

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Discord.CanRegisterCommands)
        {
            Status = "configuration-required";
            return;
        }

        var basePath = $"applications/{Uri.EscapeDataString(options.Discord.ApplicationId)}/guilds/{Uri.EscapeDataString(options.Discord.GuildId)}/commands";
        using var listRequest = CreateRequest(HttpMethod.Get, basePath);
        using var listResponse = await httpClient.SendAsync(listRequest, cancellationToken);
        listResponse.EnsureSuccessStatusCode();
        using var existingPayload = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cancellationToken));
        var existing = existingPayload.RootElement.ValueKind == JsonValueKind.Array
            ? existingPayload.RootElement.EnumerateArray()
                .Where(command => TryGetString(command, "name", out _) && TryGetString(command, "id", out _))
                .ToDictionary(
                    command => command.GetProperty("name").GetString()!,
                    command => command.GetProperty("id").GetString()!,
                    StringComparer.Ordinal)
            : throw new InvalidOperationException("Discord returned an invalid guild command list.");

        foreach (var definition in CreateDefinitions(options.Discord.CommandName))
        {
            var path = existing.TryGetValue(definition.Name, out var commandId)
                ? $"{basePath}/{Uri.EscapeDataString(commandId)}"
                : basePath;
            using var request = CreateRequest(existing.ContainsKey(definition.Name) ? HttpMethod.Patch : HttpMethod.Post, path);
            request.Content = JsonContent.Create(definition.Payload);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        Status = "ready";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var retryDelay = TimeSpan.FromMinutes(15);
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Status = "retrying";
                retryDelay = TimeSpan.FromSeconds(30);
                logger.LogError(exception, "Discord guild command reconciliation failed; relay persistence remains available.");
            }

            await Task.Delay(retryDelay, timeProvider, stoppingToken);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.Discord.BotToken);
        request.Headers.UserAgent.ParseAdd("Sapphire-Avenue-Discord-Bridge/1.0");
        return request;
    }

    private static IReadOnlyList<(string Name, object Payload)> CreateDefinitions(string commandName) =>
    [
        (
            commandName,
            new
            {
                name = commandName,
                description = "Send a message to the configured CWLS relay",
                type = 1,
                options = new object[]
                {
                    new { name = "message", description = "Message to relay", type = 3, required = true, max_length = 500 }
                }
            }),
        (
            "bridge",
            new
            {
                name = "bridge",
                description = "Manage this server's CWLS relay",
                type = 1,
                default_member_permissions = "32",
                options = new object[]
                {
                    Subcommand("status", "Show relay configuration and state"),
                    Subcommand("list-nodes", "List relay nodes and their status"),
                    Subcommand("add-node", "Create a one-time node pairing code",
                        new { name = "name", description = "Friendly node name", type = 3, required = true, max_length = 80 }),
                    Subcommand("configure", "Set the relay channel and allowed role",
                        new { name = "channel", description = "CWLS relay text channel", type = 7, required = true, channel_types = new[] { 0 } },
                        new { name = "role", description = "Role allowed to send CWLS messages", type = 8, required = true }),
                    Subcommand("channel", "Change the CWLS relay channel",
                        new { name = "channel", description = "CWLS relay text channel", type = 7, required = true, channel_types = new[] { 0 } }),
                    Subcommand("role", "Change the role allowed to send messages",
                        new { name = "role", description = "Allowed Discord role", type = 8, required = true }),
                    Subcommand("pause", "Pause or resume CWLS message relay",
                        new { name = "paused", description = "True to pause; false to resume", type = 5, required = true }),
                    Subcommand("prefer-node", "Prefer a node at the next safe lease turnover",
                        new { name = "node", description = "Relay node ID", type = 3, required = true, max_length = 64 }),
                    Subcommand("clear-preference", "Return node selection to automatic failover"),
                    Subcommand("revoke-node", "Permanently revoke a relay node",
                        new { name = "node", description = "Relay node ID", type = 3, required = true, max_length = 64 })
                }
            })
    ];

    private static object Subcommand(string name, string description, params object[] options) => new
    {
        name,
        description,
        type = 1,
        options
    };

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }
}
