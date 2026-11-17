using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Analytics;
using UrlShortener.Core.Domain;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.Infrastructure.Persistence;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Analytics;

public class LinkAnalyticsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 15, 45, 0, TimeSpan.Zero);

    private readonly InMemoryShortLinkRepository _links = new();
    private readonly SpyClickRepository _clicks = new();
    private readonly ChannelClickRecorder _recorder = new(new AnalyticsOptions { BufferCapacity = 10 });

    private LinkAnalyticsService CreateService(int windowDays = 30) => new(
        _links,
        _clicks,
        _recorder,
        new ClickEventFactory("key"),
        new AnalyticsOptions { StatsWindowDays = windowDays, TopReferrers = 5 },
        new ManualTimeProvider(Now));

    [Fact]
    public async Task GetStatsAsync_UnknownLink_ReturnsNotFound()
    {
        var result = await CreateService().GetStatsAsync("missing", default);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
        Assert.Null(_clicks.LastSince);
    }

    [Fact]
    public async Task GetStatsAsync_WindowStartsAtUtcMidnight_IncludingToday()
    {
        await _links.TryAddAsync(new ShortLink { Code = "abc1234", TargetUrl = "https://e.x/", CreatedAt = Now }, default);

        await CreateService(windowDays: 7).GetStatsAsync("abc1234", default);

        Assert.Equal(new DateTimeOffset(2026, 6, 24, 0, 0, 0, TimeSpan.Zero), _clicks.LastSince);
        Assert.Equal(5, _clicks.LastTop);
    }

    [Fact]
    public void RecordClick_EnqueuesEventStampedWithCurrentTime()
    {
        Assert.True(CreateService().RecordClick("abc1234", null, "curl/8", "https://ref.example/x"));

        Assert.True(_recorder.Reader.TryRead(out var click));
        Assert.Equal(new ClickEvent("abc1234", Now, "ref.example", ClientKind.Bot, click!.VisitorHash), click);
    }

    private sealed class SpyClickRepository : IClickRepository
    {
        public DateTimeOffset? LastSince { get; private set; }

        public int LastTop { get; private set; }

        public Task AddBatchAsync(IReadOnlyCollection<ClickEvent> clicks, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LinkStats> GetStatsAsync(string code, DateTimeOffset since, int topReferrers, CancellationToken cancellationToken)
        {
            LastSince = since;
            LastTop = topReferrers;
            return Task.FromResult(new LinkStats(code, 0, 0, null, [], [], []));
        }
    }
}
