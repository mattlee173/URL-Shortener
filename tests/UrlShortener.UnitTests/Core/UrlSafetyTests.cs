using UrlShortener.Core;
using UrlShortener.Core.Validation;

namespace UrlShortener.UnitTests.Core;

/// <summary>Scenario 3: destination safety rules layered on top of basic URL validation.</summary>
public class UrlSafetyTests
{
    private readonly TargetUrlValidator _validator = new(new ShortenerOptions
    {
        BaseUrl = "https://sho.rt",
        BlockedHosts = ["evil.example", "Phish.Example."],
    });

    [Theory]
    [InlineData("http://localhost/admin")]
    [InlineData("http://LOCALHOST./")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://2130706433/")]          // decimal spelling of 127.0.0.1
    [InlineData("http://0x7f.0.0.1/")]          // hex spelling
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // cloud metadata endpoint
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]   // IPv4-mapped IPv6
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://db.internal/")]
    [InlineData("http://app.localhost/")]
    public void Rejects_InternalTargets(string url)
    {
        Assert.Equal("url_host_not_allowed", _validator.Validate(url).Error?.Code);
    }

    [Theory]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://172.32.0.1/")]   // just outside 172.16.0.0/12
    [InlineData("http://100.128.0.1/")]  // just outside 100.64.0.0/10
    [InlineData("http://192.169.0.1/")]
    [InlineData("http://[2606:4700:4700::1111]/")]
    [InlineData("https://local.example.com/")] // "local" as a label, not the .local suffix
    public void Allows_PublicTargets(string url)
    {
        Assert.True(_validator.Validate(url).IsSuccess);
    }

    [Theory]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://bank.example@evil.test/")]
    public void Rejects_EmbeddedCredentials(string url)
    {
        Assert.Equal("url_credentials_not_allowed", _validator.Validate(url).Error?.Code);
    }

    [Theory]
    [InlineData("https://sho.rt/abc1234")]
    [InlineData("http://SHO.RT./other")]
    public void Rejects_LinksToTheShortenerItself(string url)
    {
        Assert.Equal("url_self_reference", _validator.Validate(url).Error?.Code);
    }

    [Theory]
    [InlineData("https://evil.example/", true)]
    [InlineData("https://login.evil.example/", true)]
    [InlineData("https://phish.example/x", true)]
    [InlineData("https://notevil.example/", false)]
    [InlineData("https://evil.example.com/", false)]
    public void BlockedHosts_MatchHostAndSubdomains(string url, bool blocked)
    {
        var result = _validator.Validate(url);

        Assert.Equal(blocked ? "url_host_blocked" : null, result.Error?.Code);
    }

    [Fact]
    public void InternationalisedHost_IsStoredAsPunycode()
    {
        // Regression: a Unicode host passed through AbsoluteUri unchanged, and Kestrel rejected
        // the non-ASCII Location header, turning every redirect for the link into a 500.
        var result = _validator.Validate("https://bücher.example/straße");

        Assert.Equal("https://xn--bcher-kva.example/stra%C3%9Fe", result.Value);
        Assert.All(result.Value, c => Assert.True(c < 128));
    }
}
