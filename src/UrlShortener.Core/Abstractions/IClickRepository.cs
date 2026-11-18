using UrlShortener.Core.Analytics;

namespace UrlShortener.Core.Abstractions;

public interface IClickRepository
{
    Task AddBatchAsync(IReadOnlyCollection<ClickEvent> clicks, CancellationToken cancellationToken);

    /// <param name="code">Link code.</param>
    /// <param name="since">Start of the daily-breakdown window (inclusive). Totals are all-time.</param>
    /// <param name="topReferrers">Maximum referrer rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<LinkStats> GetStatsAsync(string code, DateTimeOffset since, int topReferrers, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes up to <paramref name="maxCount"/> click events that occurred strictly before
    /// <paramref name="cutoff"/>. Bounded so a large purge never holds the write lock for long.
    /// </summary>
    /// <returns>How many events were deleted; fewer than <paramref name="maxCount"/> means none are left.</returns>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int maxCount, CancellationToken cancellationToken);
}
