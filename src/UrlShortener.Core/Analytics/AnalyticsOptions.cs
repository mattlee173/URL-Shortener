namespace UrlShortener.Core.Analytics;

/// <summary>Bound from the "Analytics" configuration section.</summary>
public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>
    /// Secret used to key visitor hashes. Must be set outside source control in production;
    /// rotating it resets unique-visitor continuity (by design, a privacy lever).
    /// </summary>
    public string VisitorHashKey { get; set; } = string.Empty;

    /// <summary>Max events buffered in memory before new events are dropped (protects the redirect path).</summary>
    public int BufferCapacity { get; set; } = 10_000;

    /// <summary>Max events persisted per database write.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Upper bound on how long an event waits in the buffer before being flushed.</summary>
    public int FlushIntervalMilliseconds { get; set; } = 1_000;

    /// <summary>Days covered by the per-day breakdown in the stats endpoint.</summary>
    public int StatsWindowDays { get; set; } = 30;

    public int TopReferrers { get; set; } = 10;
}
