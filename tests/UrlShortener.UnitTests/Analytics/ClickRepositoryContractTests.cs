using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Analytics;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.UnitTests.Analytics;

/// <summary>Aggregation behaviour shared by every <see cref="IClickRepository"/> implementation.</summary>
public abstract class ClickRepositoryContractTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 4, 1, 10, 0, 0, TimeSpan.Zero);

    protected abstract IClickRepository Repository { get; }

    [Fact]
    public async Task GetStats_NoClicks_ReturnsZeroes()
    {
        var stats = await Repository.GetStatsAsync("empty01", DateTimeOffset.MinValue, 10, default);

        Assert.Equal(0, stats.TotalClicks);
        Assert.Equal(0, stats.UniqueVisitors);
        Assert.Null(stats.LastClickAt);
        Assert.Empty(stats.ClicksByDay);
        Assert.Empty(stats.TopReferrers);
        Assert.Empty(stats.ByClientKind);
    }

    [Fact]
    public async Task GetStats_AggregatesTotalsUniquesDaysReferrersAndClientKinds()
    {
        await Repository.AddBatchAsync(
        [
            new("stats01", Day1, "news.example", ClientKind.Desktop, "visitor-a"),
            new("stats01", Day1.AddHours(1), "news.example", ClientKind.Mobile, "visitor-b"),
            new("stats01", Day1.AddDays(1), "blog.example", ClientKind.Desktop, "visitor-a"),
            new("stats01", Day1.AddDays(1).AddMinutes(5), null, ClientKind.Bot, "visitor-c"),
            new("other01", Day1, "news.example", ClientKind.Desktop, "visitor-z"),
        ], default);

        var stats = await Repository.GetStatsAsync("stats01", DateTimeOffset.MinValue, 10, default);

        Assert.Equal(4, stats.TotalClicks);
        Assert.Equal(3, stats.UniqueVisitors);
        Assert.Equal(Day1.AddDays(1).AddMinutes(5), stats.LastClickAt);
        Assert.Equal(
            new[] { new DailyClicks(new DateOnly(2026, 4, 1), 2), new DailyClicks(new DateOnly(2026, 4, 2), 2) },
            stats.ClicksByDay);
        Assert.Equal(
            new[] { new ReferrerClicks("news.example", 2), new ReferrerClicks("blog.example", 1) },
            stats.TopReferrers);
        Assert.Equal(new ClientKindClicks(ClientKind.Desktop, 2), stats.ByClientKind[0]);
        Assert.Equal(3, stats.ByClientKind.Count);
    }

    [Fact]
    public async Task GetStats_DailyBreakdownRespectsWindow_ButTotalsAreAllTime()
    {
        await Repository.AddBatchAsync(
        [
            new("window1", Day1, null, ClientKind.Desktop, "v1"),
            new("window1", Day1.AddDays(10), null, ClientKind.Desktop, "v2"),
        ], default);

        var stats = await Repository.GetStatsAsync("window1", Day1.AddDays(5), 10, default);

        Assert.Equal(2, stats.TotalClicks);
        var day = Assert.Single(stats.ClicksByDay);
        Assert.Equal(new DateOnly(2026, 4, 11), day.Date);
    }

    [Fact]
    public async Task GetStats_LimitsTopReferrers()
    {
        await Repository.AddBatchAsync(
            Enumerable.Range(0, 5).Select(i => new ClickEvent("refs001", Day1, $"r{i}.example", ClientKind.Desktop, "v")).ToList(),
            default);

        var stats = await Repository.GetStatsAsync("refs001", DateTimeOffset.MinValue, 3, default);

        Assert.Equal(3, stats.TopReferrers.Count);
    }
}

public sealed class InMemoryClickRepositoryTests : ClickRepositoryContractTests
{
    protected override IClickRepository Repository { get; } = new InMemoryClickRepository();
}

public sealed class SqliteClickRepositoryTests : ClickRepositoryContractTests, IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"urlshortener-clicks-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _connections;

    public SqliteClickRepositoryTests()
    {
        _connections = new SqliteConnectionFactory($"Data Source={_dbPath};Pooling=False");
        Repository = new SqliteClickRepository(_connections);
    }

    protected override IClickRepository Repository { get; }

    public Task InitializeAsync() => new SqliteSchemaInitializer(_connections).InitializeAsync(default);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }
}
