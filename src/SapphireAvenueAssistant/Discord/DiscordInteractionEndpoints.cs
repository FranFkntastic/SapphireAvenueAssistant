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

                    if (interactionType is not 2 and not 4 ||
                        !TryGetString(root, "id", out var interactionId) ||
                        !DiscordOptions.IsSnowflake(interactionId) ||
                        !TryGetString(root, "guild_id", out var guildId) ||
                        !TryGetString(root, "channel_id", out var channelId) ||
                        !root.TryGetProperty("data", out var data) ||
                        !TryGetString(data, "name", out var commandName))
                    {
                        return InteractionError("Unsupported interaction.");
                    }

                    if (!string.Equals(guildId, options.Discord.GuildId, StringComparison.Ordinal))
                    {
                        return interactionType == 4
                            ? AutocompleteResponse([])
                            : InteractionError("This Discord server is not connected to this relay.");
                    }

                    if (!TryReadMember(root, out var userId, out var displayName, out var roles) ||
                        !DiscordOptions.IsSnowflake(userId))
                    {
                        return interactionType == 4
                            ? AutocompleteResponse([])
                            : InteractionError("A server member identity is required.");
                    }

                    if (interactionType == 4)
                    {
                        if (!string.Equals(commandName, "bridge", StringComparison.Ordinal) ||
                            !HasManageGuildPermission(root) ||
                            !TryReadNodeAutocomplete(data, out var query))
                        {
                            return AutocompleteResponse([]);
                        }

                        var choices = await store.SearchNodeChoicesAsync(guildId, query, cancellationToken);
                        return AutocompleteResponse(choices);
                    }

                    if (string.Equals(commandName, "bridge", StringComparison.Ordinal))
                    {
                        if (!HasManageGuildPermission(root))
                        {
                            return InteractionError("Manage Server permission is required for bridge management.");
                        }

                        if (!TryReadBridgeRequest(data, interactionId, guildId, userId, out var managementRequest))
                        {
                            return InteractionError("Unsupported or incomplete bridge management command.");
                        }

                        var result = await store.ApplyBridgeManagementAsync(
                            managementRequest,
                            timeProvider.GetUtcNow(),
                            cancellationToken);
                        return InteractionResponse(result.Response);
                    }

                    if (!string.Equals(commandName, options.Discord.CommandName, StringComparison.Ordinal))
                    {
                        return InteractionError("Unsupported interaction.");
                    }

                    var rawMessage = ReadCommandMessage(data);
                    var message = RelayText.Normalize(rawMessage, options.Relay.BoundedMaximumMessageBytes);
                    var normalizedDisplayName = RelayText.Normalize(displayName, 128);
                    if (message is null || normalizedDisplayName is null)
                    {
                        return InteractionError("Provide a non-empty message within the relay limit.");
                    }

                    var enqueue = await store.EnqueueAuthorizedOutboundAsync(
                        guildId,
                        channelId,
                        roles,
                        interactionId,
                        userId,
                        normalizedDisplayName,
                        message,
                        timeProvider.GetUtcNow(),
                        cancellationToken);
                    if (enqueue.Refusal != CwlsEnqueueRefusal.None)
                    {
                        return InteractionError(enqueue.Refusal switch
                        {
                            CwlsEnqueueRefusal.NotConfigured => "The CWLS relay has not been configured.",
                            CwlsEnqueueRefusal.Paused => "The CWLS relay is paused.",
                            CwlsEnqueueRefusal.WrongChannel => "Use this command in the configured CWLS relay channel.",
                            CwlsEnqueueRefusal.RoleRequired => "You are not allowed to relay messages to this CWLS.",
                            _ => "The CWLS relay refused this message."
                        });
                    }

                    return InteractionResponse("Queued for the CWLS. The relayed game echo will appear in this channel.");
                }
            });

        return endpoints;
    }

    private static IResult InteractionError(string message) => InteractionResponse(message);

    private static IResult InteractionResponse(string message) =>
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

    private static IResult AutocompleteResponse(IReadOnlyList<RelayNodeChoice> choices) =>
        Results.Json(new
        {
            type = 8,
            data = new
            {
                choices = choices.Select(choice => new
                {
                    name = choice.DisplayName.Length <= 100 ? choice.DisplayName : choice.DisplayName[..100],
                    value = choice.NodeId
                })
            }
        });

    private static bool HasManageGuildPermission(JsonElement root)
    {
        const ulong administrator = 1UL << 3;
        const ulong manageGuild = 1UL << 5;
        return root.TryGetProperty("member", out var member) &&
            TryGetString(member, "permissions", out var rawPermissions) &&
            ulong.TryParse(rawPermissions, out var permissions) &&
            (permissions & (administrator | manageGuild)) != 0;
    }

    private static bool TryReadBridgeRequest(
        JsonElement data,
        string interactionId,
        string guildId,
        string actorUserId,
        out BridgeManagementRequest request)
    {
        request = default!;
        if (!data.TryGetProperty("options", out var options) ||
            options.ValueKind != JsonValueKind.Array ||
            options.GetArrayLength() != 1)
        {
            return false;
        }

        var subcommand = options[0];
        if (!TryGetString(subcommand, "name", out var name))
        {
            return false;
        }

        request = name switch
        {
            "status" => New(BridgeManagementAction.Status),
            "list-nodes" => New(BridgeManagementAction.ListNodes),
            "clear-preference" => New(BridgeManagementAction.ClearPreference),
            "configure" when TryReadOption(subcommand, "channel", out var channelId) &&
                TryReadOption(subcommand, "role", out var roleId) =>
                New(BridgeManagementAction.Configure) with { ChannelId = channelId, RoleId = roleId },
            "channel" when TryReadOption(subcommand, "channel", out var channelId) =>
                New(BridgeManagementAction.SetChannel) with { ChannelId = channelId },
            "role" when TryReadOption(subcommand, "role", out var roleId) =>
                New(BridgeManagementAction.SetRole) with { RoleId = roleId },
            "pause" when TryReadBooleanOption(subcommand, "paused", out var paused) =>
                New(paused ? BridgeManagementAction.Pause : BridgeManagementAction.Resume),
            "add-node" => New(BridgeManagementAction.AddNode),
            "prefer-node" when TryReadOption(subcommand, "node", out var preferredNode) =>
                New(BridgeManagementAction.PreferNode) with { NodeId = preferredNode },
            "revoke-node" when TryReadOption(subcommand, "node", out var revokedNode) =>
                New(BridgeManagementAction.RevokeNode) with { NodeId = revokedNode },
            _ => default!
        };
        return request is not null;

        BridgeManagementRequest New(BridgeManagementAction action) =>
            new(interactionId, guildId, actorUserId, action);
    }

    private static bool TryReadOption(JsonElement subcommand, string optionName, out string value)
    {
        value = string.Empty;
        if (!subcommand.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var option in options.EnumerateArray())
        {
            if (TryGetString(option, "name", out var name) && name == optionName &&
                TryGetString(option, "value", out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadNodeAutocomplete(JsonElement data, out string query)
    {
        query = string.Empty;
        if (!data.TryGetProperty("options", out var options) ||
            options.ValueKind != JsonValueKind.Array ||
            options.GetArrayLength() != 1)
        {
            return false;
        }

        var subcommand = options[0];
        if (!TryGetString(subcommand, "name", out var subcommandName) ||
            subcommandName is not "prefer-node" and not "revoke-node" ||
            !subcommand.TryGetProperty("options", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var argument in arguments.EnumerateArray())
        {
            if (TryGetString(argument, "name", out var name) && name == "node" &&
                argument.TryGetProperty("focused", out var focused) && focused.ValueKind == JsonValueKind.True)
            {
                query = argument.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadBooleanOption(JsonElement subcommand, string optionName, out bool value)
    {
        value = false;
        if (!subcommand.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var option in options.EnumerateArray())
        {
            if (TryGetString(option, "name", out var name) && name == optionName &&
                option.TryGetProperty("value", out var raw) && raw.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = raw.GetBoolean();
                return true;
            }
        }

        return false;
    }

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
