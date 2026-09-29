using System;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// One shared answer to "is this AI endpoint on this machine?".
    /// The provider checks used <c>BaseUrl.Contains("localhost")</c>, which
    /// missed http://127.0.0.1:11434/v1 and [::1] - the dummy API key was
    /// then not applied and error mapping treated the local bridge as a
    /// remote host. Every local-detection check routes through here: parse
    /// the URI and accept "localhost" or any IP loopback address.
    /// </summary>
    public static class AiEndpoints
    {
        public static bool IsLocal(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return false;

            var trimmed = baseUrl.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                if (uri.HostNameType == UriHostNameType.IPv4
                    || uri.HostNameType == UriHostNameType.IPv6)
                {
                    return System.Net.IPAddress.TryParse(uri.Host, out var ip)
                        && System.Net.IPAddress.IsLoopback(ip);
                }
                return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            }

            // Unparsable (e.g. "localhost:11434" without a scheme): keep the
            // old substring behavior rather than misdetecting a local bridge
            // as remote.
            return trimmed.Contains("localhost", StringComparison.OrdinalIgnoreCase);
        }
    }
}
