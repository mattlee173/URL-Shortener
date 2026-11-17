namespace UrlShortener.Core;

/// <summary>Tunable behaviour of the shortener. Bound from the "Shortener" configuration section.</summary>
public sealed class ShortenerOptions
{
    public const string SectionName = "Shortener";

    /// <summary>
    /// Public base URL used to build short links (e.g. https://sho.rt). Configured rather than
    /// taken from the request Host header to avoid host-header injection in generated links.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5260";

    /// <summary>Length of generated codes. 7 Base62 chars ≈ 3.5 trillion combinations.</summary>
    public int CodeLength { get; set; } = 7;

    /// <summary>How many times to retry with a fresh random code if a collision occurs.</summary>
    public int MaxGenerationAttempts { get; set; } = 5;

    public int MaxUrlLength { get; set; } = 2048;

    /// <summary>Furthest in the future a caller may set <c>expiresAt</c>.</summary>
    public int MaxExpiryDays { get; set; } = 365;

    /// <summary>Domains that may not be shortened (matches the host and all of its subdomains).</summary>
    public IReadOnlyList<string> BlockedHosts { get; set; } = [];
}
