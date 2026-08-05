using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Franthropy.Dalamud.AgentBridge;

namespace SapphireAvenueRelay;

internal sealed class RelayCoordinatorClient : IDisposable
{
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };
    private readonly RelayConfiguration configuration;

    public RelayCoordinatorClient(RelayConfiguration configuration) => this.configuration = configuration;

    public async Task<PairNodeResponse> PairAsync(
        string coordinatorBaseUrl,
        string pairingCode,
        CancellationToken cancellationToken)
    {
        var baseUri = ValidatePairingBaseUri(coordinatorBaseUrl);
        var normalizedCode = RelayConfigurationPolicy.NormalizePairingCode(pairingCode)
            ?? throw new InvalidOperationException("Pairing code must contain 13 Base32 characters (A-Z and 2-7).");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "relay/v1/pair"))
        {
            Content = JsonContent.Create(new PairNodeRequest(normalizedCode), RelayJsonContext.Default.PairNodeRequest),
        };
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ReadPairingFailureAsync(response, cancellationToken).ConfigureAwait(false));

        var pairing = await response.Content.ReadFromJsonAsync(
            RelayJsonContext.Default.PairNodeResponse,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The coordinator returned an empty pairing result.");
        if (!RelayConfigurationPolicy.IsNodeIdValid(pairing.NodeId) ||
            !RelayConfigurationPolicy.IsAccessTokenValid(pairing.AccessToken))
        {
            throw new InvalidDataException("The coordinator returned an invalid node identity or credential.");
        }

        return pairing;
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(
        string instanceId,
        bool canSendToGame,
        RelayGameIdentity identity,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "heartbeat",
            JsonContent.Create(
                new HeartbeatRequest(
                    instanceId,
                    canSendToGame,
                    identity.CharacterName,
                    identity.HomeWorldId,
                    identity.HomeWorldName),
                RelayJsonContext.Default.HeartbeatRequest),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new NodeIdentityConflictException(await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(RelayJsonContext.Default.HeartbeatResponse, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The coordinator returned an empty heartbeat.");
    }

    public async Task<OutboundRelayMessage?> ClaimAsync(string instanceId, long epoch, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "outbound/claim",
            JsonContent.Create(new ClaimRequest(instanceId, epoch), RelayJsonContext.Default.ClaimRequest),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(RelayJsonContext.Default.OutboundRelayMessage, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The coordinator returned an empty claim.");
    }

    public async Task CompleteAsync(
        string instanceId,
        long epoch,
        OutboundRelayMessage message,
        DeliveryOutcome outcome,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"outbound/{Uri.EscapeDataString(message.MessageId)}/complete",
            JsonContent.Create(
                new CompletionRequest(instanceId, epoch, message.ClaimId, outcome),
                RelayJsonContext.Default.CompletionRequest),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task PostObservationAsync(ObservationEnvelope observation, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "observations",
            JsonContent.Create(
                new ObservationRequest(
                    observation.InstanceId ?? string.Empty,
                    observation.Epoch,
                    observation.ObservationId,
                    observation.CwlsSlot,
                    observation.SenderName,
                    observation.SenderWorld,
                    observation.Content,
                    observation.ObservedAtUtc),
                RelayJsonContext.Default.ObservationRequest),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return;
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var baseUri = ValidateBaseUri(configuration.CoordinatorBaseUrl);
        var token = UnprotectNodeToken();
        try
        {
            using var request = new HttpRequestMessage(
                method,
                new Uri(baseUri, $"relay/v1/nodes/{Uri.EscapeDataString(configuration.NodeId)}/{relativePath}"))
            {
                Content = content,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Strings cannot be zeroed; keep the plaintext lifetime bounded to one request.
        }
    }

    internal static Uri ValidateBaseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Coordinator URL is not absolute.");
        var loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !loopbackHttp)
            throw new InvalidOperationException("Coordinator URL must use HTTPS, except for loopback development.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    internal static Uri ValidatePairingBaseUri(string value)
    {
        var uri = ValidateBaseUri(value);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Node pairing requires HTTPS.");
        return uri;
    }

    private static async Task<string> ReadPairingFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(body);
                var root = document.RootElement;
                foreach (var property in new[] { "error", "detail", "title" })
                {
                    if (root.TryGetProperty(property, out var value) &&
                        value.ValueKind == System.Text.Json.JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        return value.GetString()!;
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Fall through to a bounded status-specific message.
            }
        }

        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => "The pairing code is invalid.",
            HttpStatusCode.Unauthorized => "The pairing code is unknown, expired, or already used. Ask a Discord manager for a new code.",
            HttpStatusCode.NotFound => "The pairing code was not recognized.",
            HttpStatusCode.Gone => "The pairing code expired. Ask a Discord manager for a new code.",
            HttpStatusCode.Conflict => "The pairing code was already used. Ask a Discord manager for a new code.",
            _ => $"The coordinator refused pairing ({(int)response.StatusCode} {response.ReasonPhrase}).",
        };
    }

    private static async Task<string> ReadFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(error.GetString()))
            {
                return error.GetString()!;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return "This character and home world are already connected to another relay installation.";
    }

    private string UnprotectNodeToken()
    {
        if (string.IsNullOrWhiteSpace(configuration.RelayProtectedAccessToken))
            throw new InvalidOperationException("Relay node token is not configured.");
        try
        {
            return AgentBridgeDataProtection.UnprotectToken(
                configuration.RelayProtectedAccessToken,
                configuration.PluginInstanceId + ":relay");
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("Relay node token cannot be decrypted for this Windows user.", exception);
        }
    }
}

internal sealed class NodeIdentityConflictException(string message) : InvalidOperationException(message);

internal sealed record RelayGameIdentity(string? CharacterName, uint? HomeWorldId, string? HomeWorldName)
{
    public string? DisplayName => CharacterName is null || HomeWorldName is null
        ? null
        : $"{CharacterName} @ {HomeWorldName}";
}
