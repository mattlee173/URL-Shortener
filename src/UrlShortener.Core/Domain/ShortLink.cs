namespace UrlShortener.Core.Domain;

/// <summary>
/// A short code that maps to a destination URL.
/// Links are soft-deleted (never physically removed) so a code is never reissued
/// to a different destination — reusing codes would let old, still-circulating
/// short links silently point somewhere new.
/// </summary>
public sealed record ShortLink
{
    public required string Code { get; init; }

    public required string TargetUrl { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }

    public bool IsDeleted => DeletedAt is not null;
}
