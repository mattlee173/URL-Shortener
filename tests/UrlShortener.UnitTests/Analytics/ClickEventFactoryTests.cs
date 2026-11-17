using System.Net;
using UrlShortener.Core.Analytics;

namespace UrlShortener.UnitTests.Analytics;

public class ClickEventFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly ClickEventFactory _factory = new("test-key");

    [Theory]
    [InlineData(null, ClientKind.Unknown)]
    [InlineData("", ClientKind.Unknown)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36", ClientKind.Desktop)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148", ClientKind.Mobile)]
    [InlineData("Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Mobile Safari/537.36", ClientKind.Mobile)]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", ClientKind.Bot)]
    [InlineData("Slackbot-LinkExpanding 1.0", ClientKind.Bot)]
    [InlineData("curl/8.5.0", ClientKind.Bot)]
    public void Classify_MapsUserAgentToClientKind(string? userAgent, ClientKind expected)
    {
        Assert.Equal(expected, ClickEventFactory.Classify(userAgent));
    }

    [Theory]
    [InlineData("https://www.Example.com/path?token=secret", "www.example.com")]
    [InlineData("http://news.ycombinator.com/", "news.ycombinator.com")]
    [InlineData("android-app://com.slack", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void ReferrerHost_KeepsOnlyTheHost(string? referrer, string? expected)
    {
        Assert.Equal(expected, ClickEventFactory.ReferrerHost(referrer));
    }

    [Fact]
    public void Create_SameVisitor_ProducesSameHash_WithoutLeakingIp()
    {
        var ip = IPAddress.Parse("203.0.113.42");

        var first = _factory.Create("abc1234", Now, ip, "ua", null);
        var second = _factory.Create("other12", Now, ip, "ua", null);

        Assert.Equal(first.VisitorHash, second.VisitorHash);
        Assert.Equal(32, first.VisitorHash.Length);
        Assert.DoesNotContain("203.0.113.42", first.VisitorHash);
    }

    [Fact]
    public void Create_DifferentVisitorsOrKeys_ProduceDifferentHashes()
    {
        var a = _factory.Create("c", Now, IPAddress.Parse("203.0.113.1"), "ua", null);
        var b = _factory.Create("c", Now, IPAddress.Parse("203.0.113.2"), "ua", null);
        var otherKey = new ClickEventFactory("another-key").Create("c", Now, IPAddress.Parse("203.0.113.1"), "ua", null);

        Assert.NotEqual(a.VisitorHash, b.VisitorHash);
        Assert.NotEqual(a.VisitorHash, otherKey.VisitorHash);
    }
}
