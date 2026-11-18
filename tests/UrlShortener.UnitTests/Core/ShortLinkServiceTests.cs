using UrlShortener.Core;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;
using UrlShortener.Core.Services;
using UrlShortener.Core.Validation;
using UrlShortener.Infrastructure.Persistence;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Core;

public class ShortLinkServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryShortLinkRepository _repository = new();
    private readonly ManualTimeProvider _time = new(Now);
    private readonly ShortenerOptions _options = new() { MaxGenerationAttempts = 3 };

    private ShortLinkService CreateService(IShortCodeGenerator? generator = null) => new(
        _repository,
        generator ?? new SequenceCodeGenerator("Abc1234"),
        new TargetUrlValidator(_options),
        _options,
        _time);

    [Fact]
    public async Task CreateAsync_WithValidUrl_PersistsLinkWithGeneratedCode()
    {
        var service = CreateService();

        var result = await service.CreateAsync(new CreateShortLinkCommand("https://example.com/x", null), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Abc1234", result.Value.Code);
        Assert.Equal("https://example.com/x", result.Value.TargetUrl);
        Assert.Equal(Now, result.Value.CreatedAt);
        Assert.NotNull(await _repository.GetAsync("Abc1234", default));
    }

    [Fact]
    public async Task CreateAsync_TruncatesCreatedAtToStoragePrecision()
    {
        // Regression: the create response carried tick precision while reads from SQLite carried
        // millisecond precision, so the same link reported two different CreatedAt values.
        _time.Advance(TimeSpan.FromTicks(1234));
        var service = CreateService();

        var result = await service.CreateAsync(new CreateShortLinkCommand("https://example.com", null), default);

        Assert.Equal(0, result.Value.CreatedAt.Ticks % TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidUrl_ReturnsValidationErrorAndPersistsNothing()
    {
        var generator = new SequenceCodeGenerator("Abc1234");
        var service = CreateService(generator);

        var result = await service.CreateAsync(new CreateShortLinkCommand("javascript:alert(1)", null), default);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task CreateAsync_WhenGeneratedCodeCollides_RetriesWithNewCode()
    {
        await _repository.TryAddAsync(Link("taken01"), default);
        var service = CreateService(new SequenceCodeGenerator("taken01", "fresh01"));

        var result = await service.CreateAsync(new CreateShortLinkCommand("https://example.com", null), default);

        Assert.Equal("fresh01", result.Value.Code);
    }

    [Fact]
    public async Task CreateAsync_WhenEveryAttemptCollides_ReturnsUnavailable()
    {
        await _repository.TryAddAsync(Link("taken01"), default);
        var generator = new SequenceCodeGenerator("taken01");
        var service = CreateService(generator);

        var result = await service.CreateAsync(new CreateShortLinkCommand("https://example.com", null), default);

        Assert.Equal(ErrorKind.Unavailable, result.Error!.Kind);
        Assert.Equal(_options.MaxGenerationAttempts, generator.Calls);
    }

    [Fact]
    public async Task CreateAsync_WithFreeCustomAlias_UsesAlias()
    {
        var service = CreateService();

        var result = await service.CreateAsync(new CreateShortLinkCommand("https://example.com", " my-alias "), default);

        Assert.Equal("my-alias", result.Value.Code);
    }

    [Fact]
    public async Task CreateAsync_WithTakenCustomAlias_ReturnsConflict()
    {
        await _repository.TryAddAsync(Link("my-alias"), default);
        var service = CreateService();

        var result = await service.CreateAsync(new CreateShortLinkCommand("https://example.com", "my-alias"), default);

        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal("alias_taken", result.Error.Code);
    }

    [Fact]
    public async Task CreateAsync_WithAliasOfDeletedLink_ReturnsConflict()
    {
        // Codes are never recycled: an old printed/shared link must not start pointing elsewhere.
        await _repository.TryAddAsync(Link("old-link"), default);
        await _repository.MarkDeletedAsync("old-link", Now, default);
        var service = CreateService();

        var result = await service.CreateAsync(new CreateShortLinkCommand("https://evil.example", "old-link"), default);

        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidAlias_ReturnsValidationError()
    {
        var service = CreateService();

        var result = await service.CreateAsync(new CreateShortLinkCommand("https://example.com", "api"), default);

        Assert.Equal("alias_reserved", result.Error!.Code);
    }

    [Fact]
    public async Task ResolveAsync_UnknownCode_ReturnsNotFound()
    {
        var result = await CreateService().ResolveAsync("missing", default);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task ResolveAsync_DeletedLink_ReturnsGone()
    {
        await _repository.TryAddAsync(Link("gone123"), default);
        await _repository.MarkDeletedAsync("gone123", Now, default);

        var result = await CreateService().ResolveAsync("gone123", default);

        Assert.Equal(ErrorKind.Gone, result.Error!.Kind);
    }

    [Fact]
    public async Task GetAsync_DeletedLink_ReturnsGone_AndWithholdsTarget()
    {
        // Regression: public metadata used to return deleted links in full, so a destination an
        // operator took down (e.g. a phishing page) stayed discoverable through GET /api/links/{code}.
        await _repository.TryAddAsync(Link("meta123"), default);
        await _repository.MarkDeletedAsync("meta123", Now, default);

        var result = await CreateService().GetAsync("meta123", default);

        Assert.Equal(ErrorKind.Gone, result.Error!.Kind);
        Assert.Equal("link_deleted", result.Error.Code);
    }

    [Fact]
    public async Task GetAsync_ExpiredLink_StillReturnsMetadata()
    {
        await _repository.TryAddAsync(Link("exp1234") with { ExpiresAt = Now.AddMinutes(1) }, default);
        _time.Advance(TimeSpan.FromMinutes(2));

        var result = await CreateService().GetAsync("exp1234", default);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddMinutes(1), result.Value.ExpiresAt);
    }

    [Fact]
    public async Task DeleteAsync_ActiveLink_SucceedsOnceThenReportsNotFound()
    {
        await _repository.TryAddAsync(Link("del1234"), default);
        var service = CreateService();

        var first = await service.DeleteAsync("del1234", default);
        var second = await service.DeleteAsync("del1234", default);

        Assert.True(first.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, second.Error!.Kind);
        Assert.Equal(Now, (await _repository.GetAsync("del1234", default))!.DeletedAt);
    }

    private static ShortLink Link(string code) =>
        new() { Code = code, TargetUrl = "https://example.com/", CreatedAt = Now };
}
