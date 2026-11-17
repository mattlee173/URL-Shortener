using System.Threading.Channels;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Analytics;

namespace UrlShortener.Infrastructure.Analytics;

/// <summary>
/// Bounded in-memory buffer between the redirect path (producer) and
/// <see cref="ClickFlushService"/> (consumer). When full, new events are dropped rather than
/// blocking redirects or growing memory without limit; drops are counted so they can be
/// monitored.
/// </summary>
public sealed class ChannelClickRecorder : IClickRecorder
{
    private readonly Channel<ClickEvent> _channel;
    private long _dropped;

    public ChannelClickRecorder(AnalyticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _channel = Channel.CreateBounded<ClickEvent>(new BoundedChannelOptions(options.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // with TryWrite, "Wait" means "fail fast when full"
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<ClickEvent> Reader => _channel.Reader;

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public bool TryRecord(ClickEvent click)
    {
        if (_channel.Writer.TryWrite(click))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Stops accepting events; the consumer drains what is left.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
