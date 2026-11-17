using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.UnitTests.Infrastructure;

/// <summary>
/// Behaviour every <see cref="IShortLinkRepository"/> must honour. Running the same suite against
/// each implementation keeps the in-memory store an honest stand-in for SQLite.
/// </summary>
public abstract class ShortLinkRepositoryContractTests
{
    protected static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

    protected abstract IShortLinkRepository Repository { get; }

    [Fact]
    public async Task TryAdd_ThenGet_RoundTripsAllFields()
    {
        var link = new ShortLink { Code = "Round01", TargetUrl = "https://example.com/?q=ü", CreatedAt = Now };

        Assert.True(await Repository.TryAddAsync(link, default));
        var loaded = await Repository.GetAsync("Round01", default);

        Assert.Equal(link, loaded);
    }

    [Fact]
    public async Task TryAdd_DuplicateCode_ReturnsFalseAndKeepsOriginal()
    {
        await Repository.TryAddAsync(Link("Dup0001", "https://first.example/"), default);

        var added = await Repository.TryAddAsync(Link("Dup0001", "https://second.example/"), default);

        Assert.False(added);
        Assert.Equal("https://first.example/", (await Repository.GetAsync("Dup0001", default))!.TargetUrl);
    }

    [Fact]
    public async Task Codes_AreCaseSensitive()
    {
        Assert.True(await Repository.TryAddAsync(Link("CaseAbc"), default));
        Assert.True(await Repository.TryAddAsync(Link("caseabc"), default));
    }

    [Fact]
    public async Task Get_UnknownCode_ReturnsNull()
    {
        Assert.Null(await Repository.GetAsync("nothere", default));
    }

    [Fact]
    public async Task MarkDeleted_OnlySucceedsForActiveLinks()
    {
        await Repository.TryAddAsync(Link("Del0001"), default);

        Assert.True(await Repository.MarkDeletedAsync("Del0001", Now, default));
        Assert.False(await Repository.MarkDeletedAsync("Del0001", Now, default));
        Assert.False(await Repository.MarkDeletedAsync("unknown", Now, default));
        Assert.Equal(Now, (await Repository.GetAsync("Del0001", default))!.DeletedAt);
    }

    [Fact]
    public async Task TryAdd_ConcurrentInsertsOfSameCode_ExactlyOneWins()
    {
        var attempts = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => Repository.TryAddAsync(Link("Race001", $"https://r{i}.example/"), default)));

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, added => added);
    }

    protected static ShortLink Link(string code, string url = "https://example.com/") =>
        new() { Code = code, TargetUrl = url, CreatedAt = Now };
}

public sealed class InMemoryShortLinkRepositoryTests : ShortLinkRepositoryContractTests
{
    protected override IShortLinkRepository Repository { get; } = new InMemoryShortLinkRepository();
}

public sealed class SqliteShortLinkRepositoryTests : ShortLinkRepositoryContractTests, IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"urlshortener-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _connections;

    public SqliteShortLinkRepositoryTests()
    {
        _connections = new SqliteConnectionFactory($"Data Source={_dbPath};Pooling=False");
        Repository = new SqliteShortLinkRepository(_connections);
    }

    protected override IShortLinkRepository Repository { get; }

    public Task InitializeAsync() => new SqliteSchemaInitializer(_connections).InitializeAsync(default);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        await new SqliteSchemaInitializer(_connections).InitializeAsync(default);

        Assert.True(await Repository.TryAddAsync(Link("Idem001"), default));
    }
}
