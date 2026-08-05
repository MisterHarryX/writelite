using System.Net;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Ensures language-engine HTTP traffic stays on IPv4 loopback only.
/// </summary>
public static class WriteLiteLoopbackGuard
{
    public static bool IsAllowedBaseAddress(string? baseAddress)
    {
        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            return false;
        }

        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IsAllowedBaseAddress(uri);
    }

    public static bool IsAllowedBaseAddress(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            // Local engine serves plain HTTP only.
            return false;
        }

        // Explicitly require 127.0.0.1 — do not accept "localhost" (IPv4/IPv6 ambiguity).
        if (!IPAddress.TryParse(uri.Host, out var ip))
        {
            return false;
        }

        if (!IPAddress.IsLoopback(ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        return uri.Port is > 0 and <= 65535;
    }

    public static void EnsureAllowed(string baseAddress)
    {
        if (!IsAllowedBaseAddress(baseAddress))
        {
            throw new WriteLiteLanguageEngineException(
                "invalid-endpoint",
                "Local language engine endpoint is not allowed.");
        }
    }
}
