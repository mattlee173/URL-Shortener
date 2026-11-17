namespace UrlShortener.Core.Analytics;

public sealed record DailyClicks(DateOnly Date, long Clicks);

public sealed record ReferrerClicks(string Referrer, long Clicks);

public sealed record ClientKindClicks(ClientKind ClientKind, long Clicks);

/// <summary>Aggregated analytics for one link.</summary>
public sealed record LinkStats(
    string Code,
    long TotalClicks,
    long UniqueVisitors,
    DateTimeOffset? LastClickAt,
    IReadOnlyList<DailyClicks> ClicksByDay,
    IReadOnlyList<ReferrerClicks> TopReferrers,
    IReadOnlyList<ClientKindClicks> ByClientKind);
