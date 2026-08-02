using System.Security.Cryptography;
using System.Text;
using SapphireAvenueAssistant.Configuration;

namespace SapphireAvenueAssistant.Relay;

public sealed class RelayAuthenticator(SapphireAvenueOptions options)
{
    public bool Authorize(HttpRequest request, string nodeId)
    {
        if (!options.Relay.NodeTokens.TryGetValue(nodeId, out var expected) ||
            string.IsNullOrWhiteSpace(expected) ||
            !request.Headers.TryGetValue("Authorization", out var authorization))
        {
            return false;
        }

        const string prefix = "Bearer ";
        var header = authorization.ToString();
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var actualBytes = Encoding.UTF8.GetBytes(header[prefix.Length..]);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
