using UrlShortener.Core.Analytics;

namespace UrlShortener.Core.Abstractions;

/// <summary>
/// Fire-and-forget sink for click events, called on the redirect hot path.
/// Implementations must never block or throw: analytics is best-effort and must not
/// slow down or fail a redirect.
/// </summary>
public interface IClickRecorder
{
    /// <returns><c>false</c> if the event was dropped (e.g. buffer full).</returns>
    bool TryRecord(ClickEvent click);
}
