using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Analytics;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.Infrastructure.Analytics;

public sealed class SqliteClickRepository : IClickRepository
{
    private readonly SqliteConnectionFactory _connections;

    public SqliteClickRepository(SqliteConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task AddBatchAsync(IReadOnlyCollection<ClickEvent> clicks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clicks);
        if (clicks.Count == 0)
        {
            return;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO click_events (code, occurred_at_ms, referrer_host, client_kind, visitor_hash)
            VALUES ($code, $occurredAt, $referrer, $clientKind, $visitor);
            """;
        var code = command.Parameters.AddWithValue("$code", string.Empty);
        var occurredAt = command.Parameters.AddWithValue("$occurredAt", 0L);
        var referrer = command.Parameters.AddWithValue("$referrer", DBNull.Value);
        var clientKind = command.Parameters.AddWithValue("$clientKind", 0);
        var visitor = command.Parameters.AddWithValue("$visitor", string.Empty);

        // One prepared statement re-executed per row inside one transaction: a single fsync per batch.
        foreach (var click in clicks)
        {
            code.Value = click.Code;
            occurredAt.Value = click.OccurredAt.ToUnixTimeMilliseconds();
            referrer.Value = (object?)click.ReferrerHost ?? DBNull.Value;
            clientKind.Value = (int)click.ClientKind;
            visitor.Value = click.VisitorHash;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LinkStats> GetStatsAsync(
        string code, DateTimeOffset since, int topReferrers, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Four small aggregate queries in one round trip; all are served by ix_click_events_code_time.
        command.CommandText = """
            SELECT COUNT(*), COUNT(DISTINCT visitor_hash), MAX(occurred_at_ms)
            FROM click_events WHERE code = $code;

            SELECT date(occurred_at_ms / 1000, 'unixepoch') AS day, COUNT(*)
            FROM click_events WHERE code = $code AND occurred_at_ms >= $since
            GROUP BY day ORDER BY day;

            SELECT referrer_host, COUNT(*) AS clicks
            FROM click_events WHERE code = $code AND referrer_host IS NOT NULL
            GROUP BY referrer_host ORDER BY clicks DESC, referrer_host LIMIT $top;

            SELECT client_kind, COUNT(*) AS clicks
            FROM click_events WHERE code = $code
            GROUP BY client_kind ORDER BY clicks DESC, client_kind;
            """;
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$top", topReferrers);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var total = reader.GetInt64(0);
        var unique = reader.GetInt64(1);
        DateTimeOffset? last = reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));

        var byDay = new List<DailyClicks>();
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            byDay.Add(new DailyClicks(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt64(1)));
        }

        var referrers = new List<ReferrerClicks>();
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            referrers.Add(new ReferrerClicks(reader.GetString(0), reader.GetInt64(1)));
        }

        var kinds = new List<ClientKindClicks>();
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            kinds.Add(new ClientKindClicks((ClientKind)reader.GetInt32(0), reader.GetInt64(1)));
        }

        return new LinkStats(code, total, unique, last, byDay, referrers, kinds);
    }
}
