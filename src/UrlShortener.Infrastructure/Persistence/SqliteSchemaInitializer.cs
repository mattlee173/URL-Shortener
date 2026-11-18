using Microsoft.Data.Sqlite;

namespace UrlShortener.Infrastructure.Persistence;

/// <summary>
/// Applies forward-only schema migrations, tracking the applied version in SQLite's
/// <c>PRAGMA user_version</c>. Each migration runs in its own transaction, so a failed
/// migration leaves the database at the previous version rather than half-upgraded.
/// </summary>
/// <remarks>
/// Migrations 1 and 2 use IF NOT EXISTS because databases created before versioning was
/// introduced (Scenarios 1–2) already have those tables but report user_version = 0.
/// Never edit a shipped migration; append a new one.
/// </remarks>
public sealed class SqliteSchemaInitializer : IStorageInitializer
{
    internal static readonly string[] Migrations =
    [
        // 1: links
        """
        CREATE TABLE IF NOT EXISTS short_links (
            code           TEXT    NOT NULL PRIMARY KEY,
            target_url     TEXT    NOT NULL,
            created_at_ms  INTEGER NOT NULL,
            deleted_at_ms  INTEGER NULL
        );
        """,

        // 2: analytics (Scenario 2). Append-only; no FK so click inserts never contend with link
        // writes, and links are never hard-deleted anyway.
        """
        CREATE TABLE IF NOT EXISTS click_events (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            code            TEXT    NOT NULL,
            occurred_at_ms  INTEGER NOT NULL,
            referrer_host   TEXT    NULL,
            client_kind     INTEGER NOT NULL,
            visitor_hash    TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_click_events_code_time ON click_events (code, occurred_at_ms);
        """,

        // 3: expiry (Scenario 3). Nullable, so existing rows mean "never expires".
        """
        ALTER TABLE short_links ADD COLUMN expires_at_ms INTEGER NULL;
        """,

        // 4: click retention (2026-11-17). The purge filters on time alone, which the (code, time) index
        // cannot serve; without this, every sweep would scan the whole table.
        """
        CREATE INDEX IF NOT EXISTS ix_click_events_time ON click_events (occurred_at_ms);
        """,
    ];

    private readonly SqliteConnectionFactory _connections;

    public SqliteSchemaInitializer(SqliteConnectionFactory connections)
    {
        _connections = connections;
    }

    public static int LatestVersion => Migrations.Length;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _connections.EnsureDatabaseDirectoryExists();
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        // WAL lets redirects (readers) proceed while a write is in progress. Persistent per file.
        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);

        var current = await GetVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {current} is newer than this build supports ({LatestVersion}). Refusing to start.");
        }

        for (var version = current + 1; version <= LatestVersion; version++)
        {
            await using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, Migrations[version - 1], cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, $"PRAGMA user_version = {version};", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<int> GetVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
