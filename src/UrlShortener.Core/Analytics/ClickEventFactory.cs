using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace UrlShortener.Core.Analytics;

/// <summary>Builds privacy-preserving <see cref="ClickEvent"/>s from raw request data.</summary>
public sealed class ClickEventFactory
{
    private static readonly string[] BotMarkers =
    [
        "bot", "crawler", "spider", "slurp", "facebookexternalhit", "preview", "curl", "wget",
        "python-requests", "httpclient", "headless", "okhttp", "go-http-client",
    ];

    private static readonly string[] MobileMarkers = ["mobile", "android", "iphone", "ipad", "ipod"];

    private readonly byte[] _visitorHashKey;

    /// <param name="visitorHashKey">Server-side secret. Without it the hash cannot be reversed to an IP by brute force.</param>
    public ClickEventFactory(string visitorHashKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visitorHashKey);
        _visitorHashKey = Encoding.UTF8.GetBytes(visitorHashKey);
    }

    public ClickEvent Create(
        string code, DateTimeOffset occurredAt, IPAddress? clientIp, string? userAgent, string? referrer) =>
        new(code, occurredAt, ReferrerHost(referrer), Classify(userAgent), VisitorHash(clientIp, userAgent));

    internal static ClientKind Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return ClientKind.Unknown;
        }

        if (BotMarkers.Any(m => userAgent.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return ClientKind.Bot;
        }

        return MobileMarkers.Any(m => userAgent.Contains(m, StringComparison.OrdinalIgnoreCase))
            ? ClientKind.Mobile
            : ClientKind.Desktop;
    }

    internal static string? ReferrerHost(string? referrer) =>
        Uri.TryCreate(referrer, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.IdnHost.ToLowerInvariant()
            : null;

    private string VisitorHash(IPAddress? clientIp, string? userAgent)
    {
        var material = Encoding.UTF8.GetBytes($"{clientIp}|{userAgent}");
        var hash = HMACSHA256.HashData(_visitorHashKey, material);
        return Convert.ToHexString(hash, 0, 16);
    }
}
