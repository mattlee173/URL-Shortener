using Microsoft.Data.Sqlite;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;

namespace UrlShortener.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed repository using plain ADO.NET. Each call opens a pooled connection,
/// which keeps the repository safe to register as a singleton.
/// Timestamps are stored as UTC Unix milliseconds so they compare correctly in SQL.
/// </summary>
public sealed class SqliteShortLinkRepository : IShortLinkRepository
{
    private readonly SqliteConnectionFactory _connections;

    public SqliteShortLinkRepository(SqliteConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // ON CONFLICT DO NOTHING makes "claim this code" a single atomic statement:
        // the primary key, not application code, arbitrates concurrent inserts.
        command.CommandText = """
            INSERT INTO short_links (code, target_url, created_at_ms, deleted_at_ms)
            VALUES ($code, $targetUrl, $createdAt, NULL)
            ON CONFLICT (code) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$code", link.Code);
        command.Parameters.AddWithValue("$targetUrl", link.TargetUrl);
        command.Parameters.AddWithValue("$createdAt", link.CreatedAt.ToUnixTimeMilliseconds());

        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return inserted == 1;
    }

    public async Task<ShortLink?> GetAsync(string code, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT code, target_url, created_at_ms, deleted_at_ms
            FROM short_links
            WHERE code = $code;
            """;
        command.Parameters.AddWithValue("$code", code);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ShortLink
        {
            Code = reader.GetString(0),
            TargetUrl = reader.GetString(1),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            DeletedAt = reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
        };
    }

    public async Task<bool> MarkDeletedAsync(string code, DateTimeOffset deletedAt, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE short_links
            SET deleted_at_ms = $deletedAt
            WHERE code = $code AND deleted_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$deletedAt", deletedAt.ToUnixTimeMilliseconds());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
