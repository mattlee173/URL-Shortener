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
}
