namespace UrlShortener.Infrastructure.Persistence;

/// <summary>
/// Creates the schema if missing. Statements are idempotent so this is safe to run on every start.
/// A production system would use versioned migrations instead (see docs/RISKS.md).
/// </summary>
public sealed class SqliteSchemaInitializer : IStorageInitializer
{
    private readonly SqliteConnectionFactory _connections;

    public SqliteSchemaInitializer(SqliteConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // WAL lets redirects (readers) proceed while a write is in progress.
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS short_links (
                code           TEXT    NOT NULL PRIMARY KEY,
                target_url     TEXT    NOT NULL,
                created_at_ms  INTEGER NOT NULL,
                deleted_at_ms  INTEGER NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
