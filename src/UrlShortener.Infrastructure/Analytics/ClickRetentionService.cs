using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Analytics;
using UrlShortener.Infrastructure.Observability;

namespace UrlShortener.Infrastructure.Analytics;

/// <summary>
/// Deletes click events older than <see cref="AnalyticsOptions.RetentionDays"/>: one sweep at startup,
/// then one every <see cref="AnalyticsOptions.RetentionSweepIntervalMinutes"/>. Purges in bounded batches
/// so the click flusher (the only other writer) is never blocked for long. A failed sweep is logged and
/// retried on the next tick, and never stops the host.
/// </summary>
public sealed partial class ClickRetentionService : BackgroundService
{
    private readonly IClickRepository _repository;
    private readonly AnalyticsOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ClickRetentionService> _logger;
    private readonly ShortenerMetrics? _metrics;

    public ClickRetentionService(
        IClickRepository repository,
        AnalyticsOptions options,
        TimeProvider time,
        ILogger<ClickRetentionService> logger,
        ShortenerMetrics? metrics = null)
    {
        _repository = repository;
        _options = options;
        _time = time;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>Runs one purge to completion. Returns the number of events deleted.</summary>
    public async Task<long> SweepOnceAsync(CancellationToken cancellationToken)
    {
        if (_options.RetentionDays <= 0)
        {
            return 0;
        }

        var cutoff = _time.GetUtcNow().AddDays(-_options.RetentionDays);
        long total = 0;
        int deleted;
        do
        {
            deleted = await _repository.DeleteOlderThanAsync(cutoff, _options.RetentionDeleteBatchSize, cancellationToken)
                .ConfigureAwait(false);
            total += deleted;
            _metrics?.ClicksPurged(deleted);

            // Let a waiting click flush take the write lock between batches.
            await Task.Yield();
        }
        while (deleted >= _options.RetentionDeleteBatchSize);

        if (total > 0)
        {
            LogPurged(_logger, total, cutoff);
        }

        return total;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RetentionDays <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.RetentionSweepIntervalMinutes), _time);
        try
        {
            do
            {
                try
                {
                    await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A failed purge must not stop the host; it is logged and retried on the next tick.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogSweepFailed(_logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention purge deleted {Count} click events older than {Cutoff}.")]
    private static partial void LogPurged(ILogger logger, long count, DateTimeOffset cutoff);

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention purge failed; will retry on the next sweep.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
