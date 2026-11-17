namespace UrlShortener.Api.Contracts;

/// <summary>Request body for POST /api/links.</summary>
/// <param name="Url">Absolute http(s) destination URL.</param>
/// <param name="CustomAlias">Optional vanity code (3-32 chars: letters, digits, '-', '_').</param>
public sealed record CreateLinkRequest(string? Url, string? CustomAlias);

public sealed record LinkResponse(
    string Code,
    string ShortUrl,
    string TargetUrl,
    DateTimeOffset CreatedAt,
    bool IsDeleted);
