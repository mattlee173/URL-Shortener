using Microsoft.Extensions.Caching.Memory;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;
using UrlShortener.Infrastructure.Caching;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.UnitTests.Infrastructure;

public sealed class CachedShortLinkRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

    private readonly CountingRepository _inner = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100 });
    private readonly CachedShortLinkRepository _sut;

    public CachedShortLinkRepositoryTests()
    {
        _sut = new CachedShortLinkRepository(_inner, _cache, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Get_SecondCallForSameCode_IsServedFromCache()
    {
        await _sut.TryAddAsync(Link("Cache01"), default);

        await _sut.GetAsync("Cache01", default);
        await _sut.GetAsync("Cache01", default);

        Assert.Equal(1, _inner.GetCalls);
    }

    [Fact]
    public async Task Get_Miss_IsNotCached_SoLaterCreationIsVisible()
    {
        Assert.Null(await _sut.GetAsync("Later01", default));

        await _sut.TryAddAsync(Link("Later01"), default);

        Assert.NotNull(await _sut.GetAsync("Later01", default));
    }

    [Fact]
    public async Task MarkDeleted_InvalidatesCachedEntry()
    {
        await _sut.TryAddAsync(Link("Evict01"), default);
        await _sut.GetAsync("Evict01", default);

        await _sut.MarkDeletedAsync("Evict01", Now, default);

        Assert.True((await _sut.GetAsync("Evict01", default))!.IsDeleted);
    }

    [Fact]
    public async Task Get_OverlappingDelete_DoesNotRepopulateCacheWithStaleRow()
    {
        // Regression: a read could fetch the row just before a delete committed, the delete then
        // evicted the (empty) cache entry, and the read finally cached its stale "active" row. The
        // deleted link kept redirecting for the whole cache TTL, even on a single instance.
        var gated = new GatedRepository();
        await gated.TryAddAsync(Link("Race001"), default);
        var sut = new CachedShortLinkRepository(gated, _cache, TimeSpan.FromMinutes(5));

        gated.HoldNextGet();
        var slowRead = sut.GetAsync("Race001", default);    // reads the active row, then blocks
        await gated.GetStarted;

        await sut.MarkDeletedAsync("Race001", Now, default); // delete commits while the read is in flight
        gated.ReleaseGet();
        Assert.False((await slowRead)!.IsDeleted);           // that read itself returned the old row...

        Assert.True((await sut.GetAsync("Race001", default))!.IsDeleted); // ...but did not cache it
    }

    public void Dispose() => _cache.Dispose();

    private static ShortLink Link(string code) => new() { Code = code, TargetUrl = "https://example.com/", CreatedAt = Now };

    /// <summary>Lets a test pause a read after it has fetched its row, to interleave a delete.</summary>
    private sealed class GatedRepository : IShortLinkRepository
    {
        private readonly InMemoryShortLinkRepository _store = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _holdNext;

        public Task GetStarted => _started.Task;

        public void HoldNextGet() => _holdNext = true;

        public void ReleaseGet() => _release.TrySetResult();

        public Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken) =>
            _store.TryAddAsync(link, cancellationToken);

        public async Task<ShortLink?> GetAsync(string code, CancellationToken cancellationToken)
        {
            var snapshot = await _store.GetAsync(code, cancellationToken);
            if (_holdNext)
            {
                _holdNext = false;
                _started.TrySetResult();
                await _release.Task;
            }

            return snapshot;
        }

        public Task<bool> MarkDeletedAsync(string code, DateTimeOffset deletedAt, CancellationToken cancellationToken) =>
            _store.MarkDeletedAsync(code, deletedAt, cancellationToken);
    }

    private sealed class CountingRepository : IShortLinkRepository
    {
        private readonly InMemoryShortLinkRepository _store = new();

        public int GetCalls { get; private set; }

        public Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken) =>
            _store.TryAddAsync(link, cancellationToken);

        public Task<ShortLink?> GetAsync(string code, CancellationToken cancellationToken)
        {
            GetCalls++;
            return _store.GetAsync(code, cancellationToken);
        }

        public Task<bool> MarkDeletedAsync(string code, DateTimeOffset deletedAt, CancellationToken cancellationToken) =>
            _store.MarkDeletedAsync(code, deletedAt, cancellationToken);
    }
}
