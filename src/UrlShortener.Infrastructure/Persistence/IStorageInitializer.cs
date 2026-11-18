namespace UrlShortener.Infrastructure.Persistence;

/// <summary>Prepares the backing store (e.g. creates schema) before the app starts serving.</summary>
public interface IStorageInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

/// <summary>Used by the in-memory provider, which has no schema to prepare.</summary>
internal sealed class NoOpStorageInitializer : IStorageInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
