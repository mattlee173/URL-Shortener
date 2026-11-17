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

    public void Dispose() => _cache.Dispose();

    private static ShortLink Link(string code) => new() { Code = code, TargetUrl = "https://example.com/", CreatedAt = Now };

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
