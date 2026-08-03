namespace SapphireAvenueAssistant.Relay;

public sealed class RelayAuthenticator(RelayStore store)
{
    public async Task<bool> AuthorizeAsync(
        HttpRequest request,
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        if (!request.Headers.TryGetValue("Authorization", out var authorization))
        {
            return false;
        }

        const string prefix = "Bearer ";
        var header = authorization.ToString();
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = header[prefix.Length..];
        return !string.IsNullOrWhiteSpace(token) &&
            await store.AuthorizeNodeAsync(nodeId, token, cancellationToken);
    }
}
