using UrlShortener.Core.Analytics;
using UrlShortener.Infrastructure.Analytics;

namespace UrlShortener.UnitTests.Analytics;

public class ChannelClickRecorderTests
{
    [Fact]
    public void TryRecord_WhenBufferFull_DropsAndCountsInsteadOfBlocking()
    {
        var recorder = new ChannelClickRecorder(new AnalyticsOptions { BufferCapacity = 2 });

        var results = Enumerable.Range(0, 5).Select(_ => recorder.TryRecord(Click())).ToList();

        Assert.Equal(new[] { true, true, false, false, false }, results);
        Assert.Equal(3, recorder.DroppedCount);
    }

    [Fact]
    public void TryRecord_AfterComplete_IsRejected()
    {
        var recorder = new ChannelClickRecorder(new AnalyticsOptions());
        recorder.Complete();

        Assert.False(recorder.TryRecord(Click()));
    }

    internal static ClickEvent Click(string code = "abc1234") =>
        new(code, DateTimeOffset.UnixEpoch, null, ClientKind.Desktop, "hash");
}
