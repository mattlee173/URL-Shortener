using System.Collections.Concurrent;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Analytics;

namespace UrlShortener.Infrastructure.Analytics;

public sealed class InMemoryClickRepository : IClickRepository
{
    private readonly ConcurrentQueue<ClickEvent> _clicks = new();

    public Task AddBatchAsync(IReadOnlyCollection<ClickEvent> clicks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clicks);
        foreach (var click in clicks)
        {
            _clicks.Enqueue(click);
        }

        return Task.CompletedTask;
    }

    public Task<LinkStats> GetStatsAsync(string code, DateTimeOffset since, int topReferrers, CancellationToken cancellationToken)
    {
        var clicks = _clicks.Where(c => c.Code == code).ToList();

        var stats = new LinkStats(
            code,
            clicks.Count,
            clicks.Select(c => c.VisitorHash).Distinct(StringComparer.Ordinal).LongCount(),
            clicks.Count == 0 ? null : clicks.Max(c => c.OccurredAt),
            clicks.Where(c => c.OccurredAt >= since)
                .GroupBy(c => DateOnly.FromDateTime(c.OccurredAt.UtcDateTime))
                .OrderBy(g => g.Key)
                .Select(g => new DailyClicks(g.Key, g.LongCount()))
                .ToList(),
            clicks.Where(c => c.ReferrerHost is not null)
                .GroupBy(c => c.ReferrerHost!, StringComparer.Ordinal)
                .Select(g => new ReferrerClicks(g.Key, g.LongCount()))
                .OrderByDescending(r => r.Clicks).ThenBy(r => r.Referrer, StringComparer.Ordinal)
                .Take(topReferrers)
                .ToList(),
            clicks.GroupBy(c => c.ClientKind)
                .Select(g => new ClientKindClicks(g.Key, g.LongCount()))
                .OrderByDescending(k => k.Clicks).ThenBy(k => k.ClientKind)
                .ToList());

        return Task.FromResult(stats);
    }
}
