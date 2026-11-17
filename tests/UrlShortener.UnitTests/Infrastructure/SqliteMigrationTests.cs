using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.UnitTests.Infrastructure;

/// <summary>Scenario 3: versioned schema migrations, including upgrade of pre-versioning databases.</summary>
public sealed class SqliteMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"urlshortener-mig-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _connections;

    public SqliteMigrationTests()
    {
        _connections = new SqliteConnectionFactory($"Data Source={_dbPath};Pooling=False");
    }

    [Fact]
    public async Task FreshDatabase_IsMigratedToLatestVersion()
    {
        await new SqliteSchemaInitializer(_connections).InitializeAsync(default);

        Assert.Equal(SqliteSchemaInitializer.LatestVersion, await VersionAsync());
    }

    [Fact]
    public async Task RunningTwice_IsANoOp()
    {
        var initializer = new SqliteSchemaInitializer(_connections);

        await initializer.InitializeAsync(default);
        await initializer.InitializeAsync(default);

        Assert.Equal(SqliteSchemaInitializer.LatestVersion, await VersionAsync());
    }

    [Fact]
    public async Task LegacyUnversionedDatabase_IsUpgraded_AndDataPreserved()
    {
        // Simulate a database created by the Scenario 1 build: table exists, user_version = 0.
        await ExecAsync("""
            CREATE TABLE short_links (code TEXT NOT NULL PRIMARY KEY, target_url TEXT NOT NULL,
                                      created_at_ms INTEGER NOT NULL, deleted_at_ms INTEGER NULL);
            INSERT INTO short_links VALUES ('legacy1', 'https://example.com/', 1700000000000, NULL);
            """);

        await new SqliteSchemaInitializer(_connections).InitializeAsync(default);
        var link = await new SqliteShortLinkRepository(_connections).GetAsync("legacy1", default);

        Assert.Equal(SqliteSchemaInitializer.LatestVersion, await VersionAsync());
        Assert.Equal("https://example.com/", link!.TargetUrl);
        Assert.Null(link.ExpiresAt);
    }

    [Fact]
    public async Task DatabaseFromNewerBuild_RefusesToStart()
    {
        await ExecAsync($"PRAGMA user_version = {SqliteSchemaInitializer.LatestVersion + 1};");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SqliteSchemaInitializer(_connections).InitializeAsync(default));
    }

    public void Dispose()
    {
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    private async Task<int> VersionAsync()
    {
        await using var connection = await _connections.OpenAsync(default);
        return await SqliteSchemaInitializer.GetVersionAsync(connection, default);
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = await _connections.OpenAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
