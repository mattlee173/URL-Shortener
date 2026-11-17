using UrlShortener.Core;
using UrlShortener.Core.Domain;
using UrlShortener.Core.Services;
using UrlShortener.Core.Validation;
using UrlShortener.Infrastructure.Persistence;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Core;

/// <summary>Scenario 3: optional link expiry.</summary>
public class ExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryShortLinkRepository _repository = new();
    private readonly ManualTimeProvider _time = new(Now);
    private readonly ShortLinkService _service;

    public ExpiryTests()
    {
        var options = new ShortenerOptions { MaxExpiryDays = 30 };
        _service = new ShortLinkService(
            _repository, new SequenceCodeGenerator("Exp0001"), new TargetUrlValidator(options), options, _time);
    }

    [Fact]
    public async Task Create_WithoutExpiry_NeverExpires()
    {
        var link = (await Create(null)).Value;
        _time.Advance(TimeSpan.FromDays(10_000));

        Assert.Null(link.ExpiresAt);
        Assert.True((await _service.ResolveAsync(link.Code, default)).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public async Task Create_WithExpiryNotInFuture_IsRejected(int offsetSeconds)
    {
        var result = await Create(Now.AddSeconds(offsetSeconds));

        Assert.Equal("expiry_in_past", result.Error?.Code);
        Assert.Equal("expiresAt", result.Error?.Field);
    }

    [Fact]
    public async Task Create_WithExpiryBeyondMax_IsRejected()
    {
        var result = await Create(Now.AddDays(30).AddSeconds(1));

        Assert.Equal("expiry_too_far", result.Error?.Code);
    }

    [Fact]
    public async Task Create_NormalisesExpiryToUtcMilliseconds()
    {
        var local = new DateTimeOffset(2026, 7, 2, 8, 0, 0, TimeSpan.FromHours(-6)).AddTicks(9999);

        var link = (await Create(local)).Value;

        Assert.Equal(TimeSpan.Zero, link.ExpiresAt!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 14, 0, 0, TimeSpan.Zero), link.ExpiresAt);
    }

    [Fact]
    public async Task Resolve_BeforeExpiry_Succeeds_AtAndAfterExpiry_ReturnsGone()
    {
        var link = (await Create(Now.AddHours(1))).Value;

        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.True((await _service.ResolveAsync(link.Code, default)).IsSuccess);

        _time.Advance(TimeSpan.FromMinutes(1));
        var atExpiry = await _service.ResolveAsync(link.Code, default);

        Assert.Equal(ErrorKind.Gone, atExpiry.Error?.Kind);
        Assert.Equal("link_expired", atExpiry.Error?.Code);
    }

    private Task<Result<ShortLink>> Create(DateTimeOffset? expiresAt) =>
        _service.CreateAsync(new CreateShortLinkCommand("https://example.com", null, expiresAt), default);
}
