using Microsoft.Extensions.Caching.Memory;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;

namespace UrlShortener.Infrastructure.Caching;

/// <summary>
/// Read-through cache decorator for the redirect hot path. Redirects are read-heavy
/// (many reads per write) and a link's target never changes, so caching is safe; only
/// deletion invalidates. Misses are not cached so a newly created alias is visible at once.
/// </summary>
/// <remarks>
/// The cache is per process. With several instances, a deleted link can keep redirecting on
/// other instances for up to <see cref="StorageOptions.CacheTtlSeconds"/>. See docs/RISKS.md.
/// </remarks>
public sealed class CachedShortLinkRepository : IShortLinkRepository
{
    private readonly IShortLinkRepository _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    // Bumped by every delete. A read that overlaps a delete must not publish the row it read,
    // because that row may predate the delete. Deletes are rare, so one global counter is enough:
    // the cost of a collision is a single uncached read.
    private long _invalidations;

    public CachedShortLinkRepository(IShortLinkRepository inner, IMemoryCache cache, TimeSpan ttl)
    {
        _inner = inner;
        _cache = cache;
        _ttl = ttl;
    }

    public Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken) =>
        _inner.TryAddAsync(link, cancellationToken);

    public async Task<ShortLink?> GetAsync(string code, CancellationToken cancellationToken)
    {
        var key = CacheKey(code);
        if (_cache.TryGetValue(key, out ShortLink? cached))
        {
            return cached;
        }

        var before = Interlocked.Read(ref _invalidations);
        var link = await _inner.GetAsync(code, cancellationToken).ConfigureAwait(false);
        if (link is not null)
        {
            _cache.Set(key, link, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = _ttl, Size = 1 });

            // Checked after Set (not before) so a delete that lands between the check and the Set is
            // still caught: the delete bumps the counter before it removes the entry.
            if (Interlocked.Read(ref _invalidations) != before)
            {
                _cache.Remove(key);
            }
        }

        return link;
    }

    public async Task<bool> MarkDeletedAsync(string code, DateTimeOffset deletedAt, CancellationToken cancellationToken)
    {
        var deleted = await _inner.MarkDeletedAsync(code, deletedAt, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _invalidations);
        _cache.Remove(CacheKey(code));
        return deleted;
    }

    private static string CacheKey(string code) => "link:" + code;
}
