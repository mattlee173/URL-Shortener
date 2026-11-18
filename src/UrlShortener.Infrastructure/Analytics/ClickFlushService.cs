using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Analytics;
using UrlShortener.Infrastructure.Observability;

namespace UrlShortener.Infrastructure.Analytics;

/// <summary>
/// Background consumer that persists buffered clicks in batches. Batching turns N single-row
/// writes into one transaction, which matters for SQLite's single-writer model.
/// On shutdown it drains the buffer so in-flight clicks are not lost on a graceful stop.
/// </summary>
public sealed partial class ClickFlushService : BackgroundService
{
    private readonly ChannelClickRecorder _recorder;
    private readonly IClickRepository _repository;
    private readonly AnalyticsOptions _options;
    private readonly ILogger<ClickFlushService> _logger;
    private readonly ShortenerMetrics? _metrics;

    public ClickFlushService(
        ChannelClickRecorder recorder,
        IClickRepository repository,
        AnalyticsOptions options,
        ILogger<ClickFlushService> logger,
        ShortenerMetrics? metrics = null)
    {
        _recorder = recorder;
        _repository = repository;
        _options = options;
        _logger = logger;
        _metrics = metrics;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _recorder.Complete();
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Since .NET 10, BackgroundService starts ExecuteAsync via Task.Run with the stopping token.
            // If the host stops before that task is scheduled, ExecuteAsync never runs and cannot drain.
            // Persist whatever is still buffered here, but only once the consumer is not running, so the
            // channel keeps a single reader. If the shutdown timeout fired while ExecuteAsync is still
            // draining, leave it to finish.
            if (ExecuteTask is null || ExecuteTask.IsCompleted)
            {
                await DrainRemainingAsync().ConfigureAwait(false);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _recorder.Reader;
        var batch = new List<ClickEvent>(_options.BatchSize);

        // The loop ends when the channel is completed (shutdown) and fully drained.
        // stoppingToken is deliberately not passed to WaitToReadAsync so the drain can finish.
        while (await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            // Small delay lets a burst accumulate into one batch instead of many tiny writes.
            if (!stoppingToken.IsCancellationRequested && reader.Count < _options.BatchSize)
            {
                await DelayIgnoringCancellation(TimeSpan.FromMilliseconds(_options.FlushIntervalMilliseconds), stoppingToken)
                    .ConfigureAwait(false);
            }

            while (batch.Count < _options.BatchSize && reader.TryRead(out var click))
            {
                batch.Add(click);
            }

            await FlushAsync(batch).ConfigureAwait(false);
            batch.Clear();
        }
    }

    private async Task DrainRemainingAsync()
    {
        var batch = new List<ClickEvent>(_options.BatchSize);
        while (_recorder.Reader.TryRead(out var click))
        {
            batch.Add(click);
            if (batch.Count == _options.BatchSize)
            {
                await FlushAsync(batch).ConfigureAwait(false);
                batch.Clear();
            }
        }

        await FlushAsync(batch).ConfigureAwait(false);
    }

    private async Task FlushAsync(List<ClickEvent> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await _repository.AddBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            _metrics?.ClicksPersisted(batch.Count);
        }
#pragma warning disable CA1031 // A storage failure must not kill the consumer loop; it is logged and the batch dropped.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFlushFailed(_logger, ex, batch.Count);
            _metrics?.ClicksFailed(batch.Count);
        }
    }

    private static async Task DelayIgnoringCancellation(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: proceed straight to draining.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to persist {Count} click events; batch dropped.")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception, int count);
}
