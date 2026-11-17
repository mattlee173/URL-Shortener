namespace UrlShortener.Infrastructure;

public enum StorageProvider
{
    Sqlite,
    InMemory,
}

/// <summary>Bound from the "Storage" configuration section.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public StorageProvider Provider { get; set; } = StorageProvider.Sqlite;

    public string ConnectionString { get; set; } = "Data Source=urlshortener.db";

    /// <summary>How long a resolved link stays in the in-process cache. 0 disables caching.</summary>
    public int CacheTtlSeconds { get; set; } = 60;

    /// <summary>Upper bound on cached links, to cap memory use.</summary>
    public long CacheSizeLimit { get; set; } = 100_000;
}
