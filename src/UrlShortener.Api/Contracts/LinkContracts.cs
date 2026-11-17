namespace UrlShortener.Api.Contracts;

/// <summary>Request body for POST /api/links.</summary>
/// <param name="Url">Absolute http(s) destination URL.</param>
/// <param name="CustomAlias">Optional vanity code (3-32 chars: letters, digits, '-', '_').</param>
/// <param name="ExpiresAt">Optional ISO-8601 instant after which the link returns 410 Gone.</param>
public sealed record CreateLinkRequest(string? Url, string? CustomAlias, DateTimeOffset? ExpiresAt = null);

public sealed record LinkResponse(
    string Code,
    string ShortUrl,
    string TargetUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsDeleted);
