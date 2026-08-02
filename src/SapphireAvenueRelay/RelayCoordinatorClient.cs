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

    public async Task<HeartbeatResponse> HeartbeatAsync(string instanceId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "heartbeat",
            JsonContent.Create(new HeartbeatRequest(instanceId), RelayJsonContext.Default.HeartbeatRequest),
            cancellationToken).ConfigureAwait(false);
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
                    observation.ObservationId,
                    observation.CwlsSlot,
                    observation.SenderName,
                    observation.SenderWorld,
                    observation.Content,
                    observation.ObservedAtUtc),
                RelayJsonContext.Default.ObservationRequest),
            cancellationToken).ConfigureAwait(false);
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
