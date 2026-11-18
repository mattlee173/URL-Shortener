using System.Net;
using System.Net.Sockets;

using UrlShortener.Core.Domain;

namespace UrlShortener.Core.Validation;

/// <summary>
/// Validates and normalises destination URLs.
/// <list type="bullet">
/// <item>Only absolute http/https URLs: other schemes (javascript:, data:, file:) turn a redirect into XSS or local-file access.</item>
/// <item>No embedded credentials: they leak secrets into logs/analytics and are a phishing tell (https://bank.com@evil.example).</item>
/// <item>No loopback/private/link-local targets: a trusted short domain must not become a pointer into a visitor's intranet.</item>
/// <item>No links to the shortener itself: prevents redirect loops and chains that hide the real destination.</item>
/// <item>Operator block list for known-abusive domains (exact host or any subdomain).</item>
/// </list>
/// </summary>
public sealed class TargetUrlValidator
{
    private const string Field = "url";

    private static readonly string[] InternalHostSuffixes = [".localhost", ".local", ".internal", ".lan", ".home.arpa"];

    private static readonly byte[] Nat64Prefix = [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0];

    private static readonly byte[] Nat64LocalPrefix = [0x00, 0x64, 0xff, 0x9b, 0x00, 0x01];

    private readonly ShortenerOptions _options;
    private readonly string? _selfHost;
    private readonly string[] _blockedHosts;

    public TargetUrlValidator(ShortenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _selfHost = Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var self) ? NormaliseHost(self.IdnHost) : null;
        _blockedHosts = options.BlockedHosts.Select(NormaliseHost).Where(h => h.Length > 0).ToArray();
    }

    public Result<string> Validate(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return Invalid("url_required", "A destination URL is required.");
        }

        var trimmed = rawUrl.Trim();
        if (trimmed.Length > _options.MaxUrlLength)
        {
            return Invalid("url_too_long", $"URL must be at most {_options.MaxUrlLength} characters.");
        }

        // On Linux/macOS, Uri parses "/some/path" as an absolute file:// URI, while on Windows it
        // is rejected. Reject rooted paths explicitly so the API contract is the same on every OS.
        if (trimmed[0] is '/' or '\\' ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return Invalid("url_invalid", "URL must be a valid absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return Invalid("url_scheme_not_allowed", "Only http and https URLs are allowed.");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return Invalid("url_invalid", "URL must include a host.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Invalid("url_credentials_not_allowed", "URLs must not contain a username or password.");
        }

        // Uri canonicalises IP spellings (2130706433, 0x7f.1, 0177.0.0.1 -> 127.0.0.1), and IdnHost is
        // the ASCII (punycode) form, so these checks cannot be bypassed with alternate encodings.
        var host = NormaliseHost(uri.IdnHost);

        if (IsInternalHost(host))
        {
            return Invalid("url_host_not_allowed", "Links to local, private or internal network addresses are not allowed.");
        }

        if (host == _selfHost)
        {
            return Invalid("url_self_reference", "Links to this URL shortener are not allowed.");
        }

        if (_blockedHosts.Any(blocked => host == blocked || host.EndsWith("." + blocked, StringComparison.Ordinal)))
        {
            return Invalid("url_host_blocked", "Links to this domain are not allowed.");
        }

        // Rebuild with the punycode host: AbsoluteUri keeps Unicode hosts (IRI), and Kestrel refuses
        // non-ASCII response headers, so a Unicode host would make the redirect fail with a 500.
        var ascii = new UriBuilder(uri) { Host = uri.IdnHost }.Uri;
        return ascii.AbsoluteUri;
    }

    private static string NormaliseHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool IsInternalHost(string host)
    {
        if (host == "localhost" || InternalHostSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var ip) && IsNonPublicAddress(ip);
    }

    /// <summary>
    /// True for addresses that are not globally routable (IANA special-purpose registries), including
    /// IPv6 forms that carry an IPv4 address inside them, which would otherwise smuggle a private IPv4
    /// target past an IPv4-only check.
    /// </summary>
    private static bool IsNonPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return IsNonPublicIPv4(ip.GetAddressBytes());
        }

        // fc00::/7 unique-local, fe80::/10 link-local, fec0::/10 site-local, ff00::/8 multicast.
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast)
        {
            return true;
        }

        var b = ip.GetAddressBytes();
        var span = b.AsSpan();

        // ::/96 IPv4-compatible (deprecated, e.g. ::127.0.0.1); also covers :: and ::1.
        if (span[..12].IndexOfAnyExcept((byte)0) < 0)
        {
            return IsNonPublicIPv4(b[12..]);
        }

        // 64:ff9b::/96 NAT64 well-known prefix: the last 32 bits are the IPv4 destination.
        if (span[..12].SequenceEqual(Nat64Prefix))
        {
            return IsNonPublicIPv4(b[12..]);
        }

        // 64:ff9b:1::/48 local-use NAT64: operator-defined mapping, so the destination is unknowable.
        if (span[..6].SequenceEqual(Nat64LocalPrefix))
        {
            return true;
        }

        // 2002::/16 6to4: bits 16-47 are the IPv4 address of the tunnel endpoint.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsNonPublicIPv4(b[2..6]);
        }

        // 2001::/32 Teredo (obsolete, embedded address is obfuscated) and 2001:db8::/32 documentation.
        return b[0] == 0x20 && b[1] == 0x01 && ((b[2] == 0 && b[3] == 0) || (b[2] == 0x0d && b[3] == 0xb8));
    }

    private static bool IsNonPublicIPv4(byte[] b) => b[0] switch
    {
        0 or 10 or 127 => true,                                 // "this" network, RFC 1918, loopback
        100 => b[1] is >= 64 and <= 127,                        // RFC 6598 carrier-grade NAT
        169 => b[1] == 254,                                     // link-local / cloud metadata (169.254.169.254)
        172 => b[1] is >= 16 and <= 31,                         // RFC 1918
        192 => b[1] == 168                                      // RFC 1918
            || (b[1] == 0 && b[2] is 0 or 2)                    // IETF protocol assignments, TEST-NET-1
            || (b[1] == 88 && b[2] == 99),                      // deprecated 6to4 relay anycast
        198 => b[1] is 18 or 19                                 // benchmarking (often used internally)
            || (b[1] == 51 && b[2] == 100),                     // TEST-NET-2
        203 => b[1] == 0 && b[2] == 113,                        // TEST-NET-3
        >= 224 => true,                                         // multicast, reserved, broadcast
        _ => false,
    };

    private static DomainError Invalid(string code, string message) => DomainError.Validation(code, message, Field);
}
