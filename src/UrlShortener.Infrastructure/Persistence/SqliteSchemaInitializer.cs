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

            -- Analytics (Scenario 2). Append-only; no FK so click inserts never contend with
            -- link writes, and links are never hard-deleted anyway.
            CREATE TABLE IF NOT EXISTS click_events (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                code            TEXT    NOT NULL,
                occurred_at_ms  INTEGER NOT NULL,
                referrer_host   TEXT    NULL,
                client_kind     INTEGER NOT NULL,
                visitor_hash    TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_click_events_code_time
                ON click_events (code, occurred_at_ms);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
