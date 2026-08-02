using System.Text.Json;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Relay;

namespace SapphireAvenueAssistant.Discord;

public static class DiscordInteractionEndpoints
{
    private const int EphemeralFlag = 1 << 6;

    public static IEndpointRouteBuilder MapDiscordInteractions(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/discord/interactions",
            async Task<IResult> (
                HttpRequest request,
                DiscordRequestVerifier verifier,
                RelayStore store,
                SapphireAvenueOptions options,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                using var body = new MemoryStream();
                await request.Body.CopyToAsync(body, cancellationToken);
                var payload = body.ToArray();
                var timestamp = request.Headers["X-Signature-Timestamp"].ToString();
                var signature = request.Headers["X-Signature-Ed25519"].ToString();
                if (!verifier.Verify(timestamp, signature, payload))
                {
                    return Results.Unauthorized();
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(payload);
                }
                catch (JsonException)
                {
                    return Results.BadRequest();
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (!TryGetString(root, "application_id", out var applicationId) ||
                        !string.Equals(applicationId, options.Discord.ApplicationId, StringComparison.Ordinal) ||
                        !root.TryGetProperty("type", out var type) ||
                        !type.TryGetInt32(out var interactionType))
                    {
                        return Results.Unauthorized();
                    }

                    if (interactionType == 1)
                    {
                        return Results.Json(new { type = 1 });
                    }

                    if (interactionType != 2 ||
                        !TryGetString(root, "id", out var interactionId) ||
                        !DiscordOptions.IsSnowflake(interactionId) ||
                        !TryGetString(root, "guild_id", out var guildId) ||
                        !TryGetString(root, "channel_id", out var channelId) ||
                        !root.TryGetProperty("data", out var data) ||
                        !TryGetString(data, "name", out var commandName) ||
                        !string.Equals(commandName, options.Discord.CommandName, StringComparison.Ordinal))
                    {
                        return InteractionError("Unsupported interaction.");
                    }

                    if (!string.Equals(guildId, options.Discord.GuildId, StringComparison.Ordinal) ||
                        !string.Equals(channelId, options.Discord.ChannelId, StringComparison.Ordinal))
                    {
                        return InteractionError("Use this command in the configured CWLS relay channel.");
                    }

                    if (!TryReadMember(root, out var userId, out var displayName, out var roles) ||
                        !DiscordOptions.IsSnowflake(userId))
                    {
                        return InteractionError("A server member identity is required.");
                    }

                    if (options.Discord.AllowedRoleIds.Length > 0 &&
                        !roles.Any(role => options.Discord.AllowedRoleIds.Contains(role, StringComparer.Ordinal)))
                    {
                        return InteractionError("You are not allowed to relay messages to this CWLS.");
                    }

                    var rawMessage = ReadCommandMessage(data);
                    var message = RelayText.Normalize(rawMessage, options.Relay.BoundedMaximumMessageBytes);
                    var normalizedDisplayName = RelayText.Normalize(displayName, 128);
                    if (message is null || normalizedDisplayName is null)
                    {
                        return InteractionError("Provide a non-empty message within the relay limit.");
                    }

                    await store.EnqueueOutboundAsync(
                        interactionId,
                        userId,
                        normalizedDisplayName,
                        message,
                        timeProvider.GetUtcNow(),
                        cancellationToken);
                    return Results.Json(new
                    {
                        type = 4,
                        data = new
                        {
                            content = "Queued for the CWLS. The relayed game echo will appear in this channel.",
                            flags = EphemeralFlag,
                            allowed_mentions = new
                            {
                                parse = Array.Empty<string>()
                            }
                        }
                    });
                }
            });

        return endpoints;
    }

    private static IResult InteractionError(string message) =>
        Results.Json(new
        {
            type = 4,
            data = new
            {
                content = message,
                flags = EphemeralFlag,
                allowed_mentions = new
                {
                    parse = Array.Empty<string>()
                }
            }
        });

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private static string? ReadCommandMessage(JsonElement data)
    {
        if (!data.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var option in options.EnumerateArray())
        {
            if (TryGetString(option, "name", out var name) && name == "message" &&
                TryGetString(option, "value", out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryReadMember(
        JsonElement root,
        out string userId,
        out string displayName,
        out string[] roles)
    {
        userId = string.Empty;
        displayName = string.Empty;
        roles = [];
        if (!root.TryGetProperty("member", out var member) ||
            !member.TryGetProperty("user", out var user) ||
            !TryGetString(user, "id", out userId) ||
            !TryGetString(user, "username", out var username))
        {
            return false;
        }

        displayName = member.TryGetProperty("nick", out var nick) && nick.ValueKind == JsonValueKind.String
            ? nick.GetString() ?? username
            : user.TryGetProperty("global_name", out var globalName) && globalName.ValueKind == JsonValueKind.String
                ? globalName.GetString() ?? username
                : username;
        if (member.TryGetProperty("roles", out var roleArray) && roleArray.ValueKind == JsonValueKind.Array)
        {
            roles = roleArray
                .EnumerateArray()
                .Where(role => role.ValueKind == JsonValueKind.String)
                .Select(role => role.GetString())
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Cast<string>()
                .ToArray();
        }

        return true;
    }
}
