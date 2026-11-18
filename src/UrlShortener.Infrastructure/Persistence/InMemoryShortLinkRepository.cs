using System.Collections.Concurrent;

using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;

namespace UrlShortener.Infrastructure.Persistence;

/// <summary>
/// Non-durable repository for local experiments and tests (Storage:Provider = "InMemory").
/// Data is lost on restart and is not shared between instances.
/// </summary>
public sealed class InMemoryShortLinkRepository : IShortLinkRepository
{
    private readonly ConcurrentDictionary<string, ShortLink> _links = new(StringComparer.Ordinal);

    public Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        return Task.FromResult(_links.TryAdd(link.Code, link));
    }

    public Task<ShortLink?> GetAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(_links.TryGetValue(code, out var link) ? link : null);

    public Task<bool> MarkDeletedAsync(string code, DateTimeOffset deletedAt, CancellationToken cancellationToken)
    {
        while (_links.TryGetValue(code, out var current))
        {
            if (current.IsDeleted)
            {
                return Task.FromResult(false);
            }

            // Compare-and-swap so a concurrent delete cannot both report success.
            if (_links.TryUpdate(code, current with { DeletedAt = deletedAt }, current))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }
}
