namespace SapphireAvenue.BridgeProtocol;

public sealed record RelayConnectionDetails(Uri CoordinatorBaseUri, string PairingCode);

public static class RelayConnectionBootstrap
{
    public const string VersionToken = "SADB1";
    public const int MaximumLength = 1024;
    public const int MaximumCoordinatorUrlLength = 900;

    private const string PairingAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Create(string coordinatorBaseUrl, string pairingCode)
    {
        var details = Parse($"{VersionToken} {coordinatorBaseUrl} {pairingCode}");
        return $"{VersionToken} {details.CoordinatorBaseUri.AbsoluteUri} {details.PairingCode}";
    }

    public static RelayConnectionDetails Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength)
        {
            throw new InvalidOperationException("Paste the complete connection string from Discord.");
        }

        var parts = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !string.Equals(parts[0], VersionToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Connection strings must use the SADB1 format shown by Discord.");
        }

        var coordinatorBaseUri = ParseCoordinatorBaseUri(parts[1]);
        var pairingCode = NormalizePairingCode(parts[2])
            ?? throw new InvalidOperationException("The connection string contains an invalid one-time code.");
        return new RelayConnectionDetails(coordinatorBaseUri, pairingCode);
    }

    public static Uri ParseCoordinatorBaseUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumCoordinatorUrlLength ||
            value.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "The connection string must contain an HTTPS coordinator address without credentials, a query, or a fragment.");
        }

        try
        {
            var builder = new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Path = uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
                    ? uri.AbsolutePath
                    : $"{uri.AbsolutePath}/",
                Query = string.Empty,
                Fragment = string.Empty,
            };
            return builder.Uri;
        }
        catch (UriFormatException exception)
        {
            throw new InvalidOperationException("The connection string contains an invalid coordinator address.", exception);
        }
    }

    public static string? NormalizePairingCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = new string(value
            .Where(character => character is not '-' and not ' ')
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalized.Length == 13 && normalized.All(PairingAlphabet.Contains)
            ? normalized
            : null;
    }
}
