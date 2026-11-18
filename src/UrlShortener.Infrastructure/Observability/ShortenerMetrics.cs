using System.Diagnostics.Metrics;

namespace UrlShortener.Infrastructure.Observability;

/// <summary>
/// Application metrics on the BCL <see cref="Meter"/> API: no package dependency, visible locally with
/// <c>dotnet-counters monitor --counters UrlShortener</c>, and exported over OTLP when OpenTelemetry is
/// configured. Redirect latency and status codes are not duplicated here: ASP.NET Core already emits
/// <c>http.server.request.duration</c> tagged by route and status code.
/// </summary>
public sealed class ShortenerMetrics
{
    public const string MeterName = "UrlShortener";

    private static readonly KeyValuePair<string, object?> Hit = new("result", "hit");
    private static readonly KeyValuePair<string, object?> Miss = new("result", "miss");

    private readonly Meter _meter;
    private readonly Counter<long> _cacheLookups;
    private readonly Counter<long> _clicksDropped;
    private readonly Counter<long> _clicksPersisted;
    private readonly Counter<long> _clicksFailed;
    private readonly Counter<long> _clicksPurged;

    public ShortenerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(MeterName);

        _cacheLookups = _meter.CreateCounter<long>(
            "urlshortener.cache.lookups", "{lookup}", "Link cache lookups, tagged result=hit|miss.");
        _clicksDropped = _meter.CreateCounter<long>(
            "urlshortener.clicks.dropped", "{click}", "Click events discarded because the buffer was full.");
        _clicksPersisted = _meter.CreateCounter<long>(
            "urlshortener.clicks.persisted", "{click}", "Click events written to storage.");
        _clicksFailed = _meter.CreateCounter<long>(
            "urlshortener.clicks.failed", "{click}", "Click events lost because a batch write failed.");
        _clicksPurged = _meter.CreateCounter<long>(
            "urlshortener.clicks.purged", "{click}", "Click events deleted by the retention job.");
    }

    public void CacheHit() => _cacheLookups.Add(1, Hit);

    public void CacheMiss() => _cacheLookups.Add(1, Miss);

    public void ClickDropped() => _clicksDropped.Add(1);

    public void ClicksPersisted(int count) => _clicksPersisted.Add(count);

    public void ClicksFailed(int count) => _clicksFailed.Add(count);

    public void ClicksPurged(long count) => _clicksPurged.Add(count);

    /// <summary>Publishes the click buffer depth; a depth near capacity means drops are imminent.</summary>
    public void ObserveBufferDepth(Func<int> depth) =>
        _meter.CreateObservableGauge(
            "urlshortener.clicks.buffered", depth, "{click}", "Click events waiting to be written.");
}
