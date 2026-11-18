using Microsoft.Extensions.Logging.Abstractions;

using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Analytics;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Analytics;

public class ClickFlushServiceTests
{
    private readonly AnalyticsOptions _options = new() { BatchSize = 10, FlushIntervalMilliseconds = 10_000 };

    [Fact]
    public async Task StopAsync_DrainsBufferedEvents_BeforeExiting()
    {
        var recorder = new ChannelClickRecorder(_options);
        var repository = new RecordingClickRepository();
        using var service = new ClickFlushService(recorder, repository, _options, NullLogger<ClickFlushService>.Instance);

        await service.StartAsync(default);
        for (var i = 0; i < 25; i++)
        {
            recorder.TryRecord(ChannelClickRecorderTests.Click());
        }

        // The 10s flush interval has not elapsed; graceful stop must still persist everything.
        await service.StopAsync(default);

        Assert.Equal(25, repository.Saved.Count);
        Assert.All(repository.BatchSizes, size => Assert.InRange(size, 1, _options.BatchSize));
    }

    [Fact]
    public async Task StopAsync_WhenConsumerLoopNeverRan_StillPersistsBufferedEvents()
    {
        // Regression: since .NET 10, BackgroundService runs ExecuteAsync via Task.Run with the stopping
        // token. A stop that arrives before the task is scheduled cancels it before it ever runs, and
        // buffered clicks were silently lost (~1 in 7 runs of the test above on a fast stop). Stopping a
        // service that was never started reproduces "ExecuteAsync never ran" deterministically.
        var recorder = new ChannelClickRecorder(_options);
        var repository = new RecordingClickRepository();
        using var service = new ClickFlushService(recorder, repository, _options, NullLogger<ClickFlushService>.Instance);
        for (var i = 0; i < 25; i++)
        {
            recorder.TryRecord(ChannelClickRecorderTests.Click());
        }

        await service.StopAsync(default);

        Assert.Equal(25, repository.Saved.Count);
        Assert.All(repository.BatchSizes, size => Assert.InRange(size, 1, _options.BatchSize));
    }

    [Fact]
    public async Task StartThenImmediateStop_NeverLosesEvents_AcrossManyRuns()
    {
        // Exercises the real start/stop race repeatedly; before the fix this failed ~15% of iterations.
        for (var run = 0; run < 100; run++)
        {
            var recorder = new ChannelClickRecorder(_options);
            var repository = new RecordingClickRepository();
            using var service = new ClickFlushService(recorder, repository, _options, NullLogger<ClickFlushService>.Instance);

            await service.StartAsync(default);
            for (var i = 0; i < 25; i++)
            {
                recorder.TryRecord(ChannelClickRecorderTests.Click());
            }

            await service.StopAsync(default);

            Assert.Equal(25, repository.Saved.Count);
        }
    }

    [Fact]
    public async Task FailedBatch_IsLogged_AndConsumerKeepsRunning()
    {
        var options = new AnalyticsOptions { BatchSize = 1, FlushIntervalMilliseconds = 0 };
        var recorder = new ChannelClickRecorder(options);
        var repository = new RecordingClickRepository { FailNextBatch = true };
        using var service = new ClickFlushService(recorder, repository, options, NullLogger<ClickFlushService>.Instance);

        await service.StartAsync(default);
        recorder.TryRecord(ChannelClickRecorderTests.Click("lost123"));
        await WaitUntil(() => !repository.FailNextBatch);
        recorder.TryRecord(ChannelClickRecorderTests.Click("kept123"));
        await service.StopAsync(default);

        var saved = Assert.Single(repository.Saved);
        Assert.Equal("kept123", saved.Code);
    }

    [Fact]
    public async Task Metrics_CountPersistedAndFailedClicks()
    {
        using var capture = new MetricsCapture();
        var options = new AnalyticsOptions { BatchSize = 1, FlushIntervalMilliseconds = 0 };
        var recorder = new ChannelClickRecorder(options);
        var repository = new RecordingClickRepository { FailNextBatch = true };
        using var service = new ClickFlushService(recorder, repository, options, NullLogger<ClickFlushService>.Instance, capture.Metrics);

        await service.StartAsync(default);
        recorder.TryRecord(ChannelClickRecorderTests.Click("lost123"));
        await WaitUntil(() => !repository.FailNextBatch);
        recorder.TryRecord(ChannelClickRecorderTests.Click("kept123"));
        recorder.TryRecord(ChannelClickRecorderTests.Click("kept456"));
        await service.StopAsync(default);

        Assert.Equal(1, capture.Sum("urlshortener.clicks.failed"));
        Assert.Equal(2, capture.Sum("urlshortener.clicks.persisted"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition not met within 2s.");
    }

    private sealed class RecordingClickRepository : IClickRepository
    {
        private readonly Lock _gate = new();

        public List<ClickEvent> Saved { get; } = [];

        public List<int> BatchSizes { get; } = [];

        public volatile bool FailNextBatch;

        public Task AddBatchAsync(IReadOnlyCollection<ClickEvent> clicks, CancellationToken cancellationToken)
        {
            if (FailNextBatch)
            {
                FailNextBatch = false;
                throw new InvalidOperationException("simulated storage outage");
            }

            lock (_gate)
            {
                Saved.AddRange(clicks);
                BatchSizes.Add(clicks.Count);
            }

            return Task.CompletedTask;
        }

        public Task<LinkStats> GetStatsAsync(string code, DateTimeOffset since, int topReferrers, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int maxCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
