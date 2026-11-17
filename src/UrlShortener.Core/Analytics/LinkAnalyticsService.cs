using System.Net;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;

namespace UrlShortener.Core.Analytics;

/// <summary>
/// Analytics use cases. Kept separate from <see cref="Services.ShortLinkService"/> so the
/// link lifecycle has no dependency on analytics, and analytics can be disabled or replaced
/// (e.g. with an event stream) without touching link logic.
/// </summary>
public sealed class LinkAnalyticsService
{
    private readonly IShortLinkRepository _links;
    private readonly IClickRepository _clicks;
    private readonly IClickRecorder _recorder;
    private readonly ClickEventFactory _eventFactory;
    private readonly AnalyticsOptions _options;
    private readonly TimeProvider _time;

    public LinkAnalyticsService(
        IShortLinkRepository links,
        IClickRepository clicks,
        IClickRecorder recorder,
        ClickEventFactory eventFactory,
        AnalyticsOptions options,
        TimeProvider time)
    {
        _links = links;
        _clicks = clicks;
        _recorder = recorder;
        _eventFactory = eventFactory;
        _options = options;
        _time = time;
    }

    /// <summary>Records a redirect. Non-blocking; returns <c>false</c> if the event was dropped.</summary>
    public bool RecordClick(string code, IPAddress? clientIp, string? userAgent, string? referrer) =>
        _recorder.TryRecord(_eventFactory.Create(code, _time.GetUtcNow(), clientIp, userAgent, referrer));

    public async Task<Result<LinkStats>> GetStatsAsync(string code, CancellationToken ct)
    {
        if (await _links.GetAsync(code, ct).ConfigureAwait(false) is null)
        {
            return DomainError.NotFound($"No link exists for code '{code}'.");
        }

        // Window covers today plus the previous (StatsWindowDays - 1) whole UTC days.
        var today = _time.GetUtcNow().UtcDateTime.Date;
        var since = new DateTimeOffset(today.AddDays(1 - _options.StatsWindowDays), TimeSpan.Zero);

        return await _clicks.GetStatsAsync(code, since, _options.TopReferrers, ct).ConfigureAwait(false);
    }
}
