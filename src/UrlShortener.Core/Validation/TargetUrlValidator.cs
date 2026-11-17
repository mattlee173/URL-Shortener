using UrlShortener.Core.Domain;

namespace UrlShortener.Core.Validation;

/// <summary>
/// Validates and normalises destination URLs. Only absolute http/https URLs are accepted:
/// other schemes (javascript:, data:, file:, ...) would turn a redirect into an XSS or
/// local-file vector.
/// </summary>
public sealed class TargetUrlValidator
{
    private readonly ShortenerOptions _options;

    public TargetUrlValidator(ShortenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public Result<string> Validate(string? rawUrl)
    {
        const string field = "url";

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return DomainError.Validation("url_required", "A destination URL is required.", field);
        }

        var trimmed = rawUrl.Trim();
        if (trimmed.Length > _options.MaxUrlLength)
        {
            return DomainError.Validation(
                "url_too_long", $"URL must be at most {_options.MaxUrlLength} characters.", field);
        }

        // On Linux/macOS, Uri parses "/some/path" as an absolute file:// URI, while on Windows it
        // is rejected. Reject rooted paths explicitly so the API contract is the same on every OS.
        if (trimmed[0] is '/' or '\\' ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return DomainError.Validation("url_invalid", "URL must be a valid absolute URL.", field);
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return DomainError.Validation("url_scheme_not_allowed", "Only http and https URLs are allowed.", field);
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return DomainError.Validation("url_invalid", "URL must include a host.", field);
        }

        // AbsoluteUri normalises scheme/host casing and percent-encoding, so equivalent
        // inputs are stored identically and the Location header is always well-formed.
        return uri.AbsoluteUri;
    }
}
