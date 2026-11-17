using UrlShortener.Core;
using UrlShortener.Core.Validation;

namespace UrlShortener.UnitTests.Core;

public class TargetUrlValidatorTests
{
    private readonly TargetUrlValidator _validator = new(new ShortenerOptions { MaxUrlLength = 100 });

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("http://example.com/path?q=1#frag", "http://example.com/path?q=1#frag")]
    [InlineData("  https://EXAMPLE.com/Path  ", "https://example.com/Path")]
    [InlineData("https://example.com/a b", "https://example.com/a%20b")]
    public void Validate_AcceptsAndNormalisesHttpUrls(string input, string expected)
    {
        var result = _validator.Validate(input);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData(null, "url_required")]
    [InlineData("", "url_required")]
    [InlineData("   ", "url_required")]
    [InlineData("not a url", "url_invalid")]
    [InlineData("/relative/path", "url_invalid")]
    [InlineData("javascript:alert(1)", "url_scheme_not_allowed")]
    [InlineData("data:text/html,<script>alert(1)</script>", "url_scheme_not_allowed")]
    [InlineData("file:///etc/passwd", "url_scheme_not_allowed")]
    [InlineData("ftp://example.com/file", "url_scheme_not_allowed")]
    public void Validate_RejectsInvalidUrls(string? input, string expectedCode)
    {
        var result = _validator.Validate(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal("url", result.Error.Field);
    }

    [Fact]
    public void Validate_RejectsUrlsOverMaxLength()
    {
        var result = _validator.Validate("https://example.com/" + new string('a', 100));

        Assert.Equal("url_too_long", result.Error!.Code);
    }
}
