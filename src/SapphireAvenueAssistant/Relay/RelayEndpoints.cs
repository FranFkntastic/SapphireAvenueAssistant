namespace SapphireAvenueAssistant.Relay;

public static class RelayEndpoints
{
    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/relay/v1/nodes/{nodeId}");

        group.MapPost(
            "/heartbeat",
            async Task<IResult> (
                string nodeId,
                HeartbeatRequest body,
                HttpRequest request,
                RelayAuthenticator authenticator,
                RelayStore store,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!authenticator.Authorize(request, nodeId))
                {
                    return Results.Unauthorized();
                }

                if (!IsIdentifier(nodeId) || !IsIdentifier(body.InstanceId))
                {
                    return Results.BadRequest(new { error = "Invalid node or instance identity." });
                }

                var lease = await store.HeartbeatAsync(
                    nodeId,
                    body.InstanceId,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return Results.Ok(new
                {
                    role = lease.IsLeader ? "leader" : "observer",
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
                if (!authenticator.Authorize(request, nodeId))
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
                if (!authenticator.Authorize(request, nodeId))
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
                return completed
                    ? Results.NoContent()
                    : Results.Conflict(new { error = "The claim is stale, mismatched, or already completed." });
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
                if (!authenticator.Authorize(request, nodeId))
                {
                    return Results.Unauthorized();
                }

                var senderName = RelayText.Normalize(body.SenderName, 128);
                var senderWorld = RelayText.Normalize(body.SenderWorld, 64);
                var content = RelayText.Normalize(body.Content, options.Relay.BoundedMaximumMessageBytes);
                if (!IsIdentifier(nodeId) || !IsIdentifier(body.ObservationId, 128) ||
                    body.CwlsSlot is < 1 or > 8 || senderName is null || content is null)
                {
                    return Results.BadRequest(new { error = "Invalid CWLS observation." });
                }

                var result = await store.EnqueueObservationAsync(
                    nodeId,
                    new InboundObservation(
                        body.ObservationId,
                        body.CwlsSlot,
                        senderName,
                        senderWorld,
                        content,
                        body.ObservedAtUtc),
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return Results.Ok(new { accepted = true, duplicate = !result.Inserted });
            });

        return endpoints;
    }

    private static bool IsIdentifier(string? value, int maximumLength = 64) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}

public sealed record HeartbeatRequest(string InstanceId);

public sealed record ClaimRequest(string InstanceId, long Epoch);

public sealed record CompletionRequest(
    string InstanceId,
    long Epoch,
    string ClaimId,
    DeliveryOutcome Outcome);

public sealed record ObservationRequest(
    string ObservationId,
    int CwlsSlot,
    string SenderName,
    string? SenderWorld,
    string Content,
    DateTimeOffset ObservedAtUtc);
