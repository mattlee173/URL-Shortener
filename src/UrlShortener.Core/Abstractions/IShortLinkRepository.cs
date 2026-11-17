using UrlShortener.Core.Domain;

namespace UrlShortener.Core.Abstractions;

public interface IShortLinkRepository
{
    /// <summary>
    /// Atomically inserts the link if its code is unused (including by soft-deleted links).
    /// Returns <c>false</c> when the code already exists. Implementations must not use
    /// check-then-insert, otherwise two concurrent requests can claim the same code.
    /// </summary>
    Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken);

    /// <summary>Returns the link (including soft-deleted ones) or <c>null</c> if the code was never issued.</summary>
    Task<ShortLink?> GetAsync(string code, CancellationToken cancellationToken);

    /// <summary>Soft-deletes the link. Returns <c>false</c> if it does not exist or was already deleted.</summary>
    Task<bool> MarkDeletedAsync(string code, DateTimeOffset deletedAt, CancellationToken cancellationToken);
}
