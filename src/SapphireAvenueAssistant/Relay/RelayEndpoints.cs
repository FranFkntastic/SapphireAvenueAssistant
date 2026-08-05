namespace SapphireAvenueAssistant.Relay;

public static class RelayEndpoints
{
    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/relay/v1/pair",
            async Task<IResult> (
                PairingRequest body,
                HttpRequest request,
                RelayStore store,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!IsSecurePairingTransport(request))
                {
                    return Results.BadRequest(new { error = "Node pairing requires HTTPS." });
                }

                var result = await store.ExchangePairingCodeAsync(
                    body.PairingCode,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return result is null
                    ? Results.Unauthorized()
                    : Results.Ok(new
                    {
                        result.NodeId,
                        result.AccessToken
                    });
            });

        var group = endpoints.MapGroup("/relay/v1/nodes/{nodeId}");

        group.MapPost(
            "/heartbeat",
            async Task<IResult> (
                string nodeId,
                HeartbeatRequest body,
                HttpRequest request,
                RelayAuthenticator authenticator,
                RelayStore store,
                Configuration.SapphireAvenueOptions options,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!await authenticator.AuthorizeAsync(request, nodeId, cancellationToken))
                {
                    return Results.Unauthorized();
                }

                if (!IsIdentifier(nodeId) || !IsIdentifier(body.InstanceId))
                {
                    return Results.BadRequest(new { error = "Invalid node or instance identity." });
                }


                RelayNodeIdentity? identity = null;
                var hasAnyIdentity = body.CharacterName is not null || body.HomeWorldId is not null || body.HomeWorldName is not null;
                if (hasAnyIdentity)
                {
                    var characterName = RelayText.Normalize(body.CharacterName, 64);
                    var homeWorldName = RelayText.Normalize(body.HomeWorldName, 64);
                    if (characterName is null || homeWorldName is null || body.HomeWorldId is null or <= 0)
                    {
                        return Results.BadRequest(new { error = "Character identity must include a character name and home world." });
                    }

                    identity = new RelayNodeIdentity(characterName, body.HomeWorldId.Value, homeWorldName);
                }

                var lease = await store.HeartbeatAsync(
                    nodeId,
                    body.InstanceId,
                    timeProvider.GetUtcNow(),
                    canSendToGame: body.CanSendToGame,
                    identity: identity,
                    allowLegacyHeartbeatWithoutCapability: options.Relay.AllowLegacyHeartbeatWithoutCapability,
                    cancellationToken: cancellationToken);
                if (lease.IdentityConflict is not null)
                {
                    return Results.Conflict(new
                    {
                        error = $"{lease.IdentityConflict} is already connected to another relay installation. This pairing was revoked; disconnect it and ask a Discord manager for a new pairing code."
                    });
                }
                if (!lease.Authorized)
                {
                    return Results.Unauthorized();
                }

                return Results.Ok(new
                {
                    role = lease.IsLeader ? "leader" : "observer",
                    lease.IsPreferred,
                    lease.Epoch,
                    lease.ExpiresAtUtc
                });
            });

        group.MapPost(
            "/outbound/claim",
            async Task<IResult> (
                string nodeId,
                ClaimRequest body,
                HttpRequest request,
                RelayAuthenticator authenticator,
                RelayStore store,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!await authenticator.AuthorizeAsync(request, nodeId, cancellationToken))
                {
                    return Results.Unauthorized();
                }

                if (!IsIdentifier(nodeId) || !IsIdentifier(body.InstanceId) || body.Epoch <= 0)
                {
                    return Results.BadRequest(new { error = "Invalid claim identity." });
                }

                var claim = await store.ClaimOutboundAsync(
                    nodeId,
                    body.InstanceId,
                    body.Epoch,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                if (!claim.NodeActive)
                {
                    return Results.Unauthorized();
                }

                if (!claim.Authorized)
                {
                    return Results.Conflict(new { error = "The node does not hold the current send lease." });
                }

                return claim.Message is null ? Results.NoContent() : Results.Ok(claim.Message);
            });

        group.MapPost(
            "/outbound/{messageId}/complete",
            async Task<IResult> (
                string nodeId,
                string messageId,
                CompletionRequest body,
                HttpRequest request,
                RelayAuthenticator authenticator,
                RelayStore store,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!await authenticator.AuthorizeAsync(request, nodeId, cancellationToken))
                {
                    return Results.Unauthorized();
                }

                if (!IsIdentifier(nodeId) || !IsIdentifier(body.InstanceId) ||
                    !IsIdentifier(messageId, 64) || !IsIdentifier(body.ClaimId, 64) || body.Epoch <= 0)
                {
                    return Results.BadRequest(new { error = "Invalid completion identity." });
                }

                var completed = await store.CompleteOutboundAsync(
                    nodeId,
                    body.InstanceId,
                    body.Epoch,
                    messageId,
                    body.ClaimId,
                    body.Outcome,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return completed switch
                {
                    NodeMutationResult.Completed => Results.NoContent(),
                    NodeMutationResult.Unauthorized => Results.Unauthorized(),
                    _ => Results.Conflict(new { error = "The claim is stale, mismatched, or already completed." })
                };
            });

        group.MapPost(
            "/observations",
            async Task<IResult> (
                string nodeId,
                ObservationRequest body,
                HttpRequest request,
                RelayAuthenticator authenticator,
                RelayStore store,
                Configuration.SapphireAvenueOptions options,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!await authenticator.AuthorizeAsync(request, nodeId, cancellationToken))
                {
                    return Results.Unauthorized();
                }

                var senderName = RelayText.Normalize(body.SenderName, 128);
                var senderWorld = RelayText.Normalize(body.SenderWorld, 64);
                var content = RelayText.Normalize(body.Content, options.Relay.BoundedMaximumMessageBytes);
                if (!IsIdentifier(nodeId) || !IsIdentifier(body.ObservationId, 128) ||
                    !IsIdentifier(body.InstanceId) || body.Epoch <= 0 ||
                    body.CwlsSlot is < 1 or > 8 || senderName is null || content is null)
                {
                    return Results.BadRequest(new { error = "Invalid CWLS observation." });
                }

                var result = await store.EnqueueObservationAsync(
                    nodeId,
                    body.InstanceId,
                    body.Epoch,
                    new InboundObservation(
                        body.ObservationId,
                        body.CwlsSlot,
                        senderName,
                        senderWorld,
                        content,
                        body.ObservedAtUtc),
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                if (!result.NodeActive)
                {
                    return Results.Unauthorized();
                }

                if (!result.LeaderAuthorized)
                {
                    return Results.Conflict(new { error = "Only the current relay leader may report CWLS observations." });
                }

                return Results.Ok(new { accepted = true, duplicate = !result.Inserted });
            });

        return endpoints;
    }

    public static bool IsSecurePairingTransport(HttpRequest request)
    {
        if (request.IsHttps)
        {
            return true;
        }

        var remoteAddress = request.HttpContext.Connection.RemoteIpAddress;
        return remoteAddress is not null &&
            System.Net.IPAddress.IsLoopback(remoteAddress) &&
            request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto) &&
            forwardedProto.Count == 1 &&
            string.Equals(forwardedProto[0], "https", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdentifier(string? value, int maximumLength = 64) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}

public sealed record HeartbeatRequest(
    string InstanceId,
    bool? CanSendToGame,
    string? CharacterName = null,
    long? HomeWorldId = null,
    string? HomeWorldName = null);

public sealed record PairingRequest(string PairingCode);

public sealed record ClaimRequest(string InstanceId, long Epoch);

public sealed record CompletionRequest(
    string InstanceId,
    long Epoch,
    string ClaimId,
    DeliveryOutcome Outcome);

public sealed record ObservationRequest(
    string InstanceId,
    long Epoch,
    string ObservationId,
    int CwlsSlot,
    string SenderName,
    string? SenderWorld,
    string Content,
    DateTimeOffset ObservedAtUtc);
