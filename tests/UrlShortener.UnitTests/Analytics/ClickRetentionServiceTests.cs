using Microsoft.Extensions.Logging.Abstractions;

using UrlShortener.Core.Analytics;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Analytics;

public class ClickRetentionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryClickRepository _repository = new();
    private readonly ManualTimeProvider _time = new(Now);

    [Fact]
    public async Task Sweep_DeletesOnlyEventsOlderThanRetention_InBatches()
    {
        using var capture = new MetricsCapture();
        var options = new AnalyticsOptions { RetentionDays = 30, RetentionDeleteBatchSize = 2 };
        await _repository.AddBatchAsync(
        [
            Click("old0001", Now.AddDays(-90)),
            Click("old0002", Now.AddDays(-31)),
            Click("old0003", Now.AddDays(-30).AddMilliseconds(-1)), // just past the cutoff
            Click("keep001", Now.AddDays(-30)),                      // exactly at the cutoff is kept
            Click("keep002", Now.AddDays(-1)),
        ], default);
        var sut = Service(options, capture);

        var deleted = await sut.SweepOnceAsync(default);

        // 3 old events with a batch size of 2 means two DELETE statements (2 + 1).
        Assert.Equal(3, deleted);
        Assert.Equal(3, capture.Sum("urlshortener.clicks.purged"));
        Assert.Equal(1, (await _repository.GetStatsAsync("keep001", DateTimeOffset.MinValue, 1, default)).TotalClicks);
        Assert.Equal(1, (await _repository.GetStatsAsync("keep002", DateTimeOffset.MinValue, 1, default)).TotalClicks);
        Assert.Equal(0, (await _repository.GetStatsAsync("old0003", DateTimeOffset.MinValue, 1, default)).TotalClicks);
    }

    [Fact]
    public async Task Sweep_WhenRetentionDisabled_DeletesNothing()
    {
        await _repository.AddBatchAsync([Click("ancient", Now.AddYears(-10))], default);
        var sut = Service(new AnalyticsOptions { RetentionDays = 0 });

        Assert.Equal(0, await sut.SweepOnceAsync(default));
        Assert.Equal(1, (await _repository.GetStatsAsync("ancient", DateTimeOffset.MinValue, 1, default)).TotalClicks);
    }

    [Fact]
    public async Task Sweep_UsesTheInjectedClock()
    {
        var sut = Service(new AnalyticsOptions { RetentionDays = 30 });
        await _repository.AddBatchAsync([Click("ages001", Now.AddDays(-20))], default);

        Assert.Equal(0, await sut.SweepOnceAsync(default));
        _time.Advance(TimeSpan.FromDays(11));
        Assert.Equal(1, await sut.SweepOnceAsync(default));
    }

    private ClickRetentionService Service(AnalyticsOptions options, MetricsCapture? capture = null) =>
        new(_repository, options, _time, NullLogger<ClickRetentionService>.Instance, capture?.Metrics);

    private static ClickEvent Click(string code, DateTimeOffset at) => new(code, at, null, ClientKind.Desktop, "v");
}
