using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Analytics;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.Infrastructure.Caching;
using UrlShortener.Infrastructure.Observability;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.Infrastructure;

public static class DependencyInjection
{
    /// <remarks>
    /// Storage options are read lazily (when services are first resolved), not while registering.
    /// Reading configuration eagerly here would ignore overrides that hosts such as
    /// WebApplicationFactory apply after Program.cs has run its registrations.
    /// </remarks>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .Validate(o => o.Provider != StorageProvider.Sqlite || !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Storage:ConnectionString is required for the Sqlite provider.")
            .Validate(o => o.CacheSizeLimit > 0, "Storage:CacheSizeLimit must be positive.")
            .ValidateOnStart();

        services.AddMetrics();
        services.AddSingleton<ShortenerMetrics>();

        services.AddMemoryCache();
        services.AddOptions<MemoryCacheOptions>()
            .Configure<IOptions<StorageOptions>>((cache, storage) => cache.SizeLimit = storage.Value.CacheSizeLimit);

        services.AddSingleton(sp => new SqliteConnectionFactory(Options(sp).ConnectionString));
        services.AddSingleton<SqliteShortLinkRepository>();
        services.AddSingleton<InMemoryShortLinkRepository>();

        services.AddSingleton<IStorageInitializer>(sp => Options(sp).Provider switch
        {
            StorageProvider.Sqlite => new SqliteSchemaInitializer(sp.GetRequiredService<SqliteConnectionFactory>()),
            _ => new NoOpStorageInitializer(),
        });

        services.AddSingleton<IShortLinkRepository>(sp =>
        {
            var options = Options(sp);
            IShortLinkRepository store = options.Provider switch
            {
                StorageProvider.Sqlite => sp.GetRequiredService<SqliteShortLinkRepository>(),
                StorageProvider.InMemory => sp.GetRequiredService<InMemoryShortLinkRepository>(),
                _ => throw new InvalidOperationException($"Unsupported storage provider '{options.Provider}'."),
            };

            return options.CacheTtlSeconds > 0
                ? new CachedShortLinkRepository(
                    store,
                    sp.GetRequiredService<IMemoryCache>(),
                    TimeSpan.FromSeconds(options.CacheTtlSeconds),
                    sp.GetRequiredService<ShortenerMetrics>())
                : store;
        });

        AddAnalytics(services);

        return services;
    }

    private static void AddAnalytics(IServiceCollection services)
    {
        services.AddSingleton<SqliteClickRepository>();
        services.AddSingleton<InMemoryClickRepository>();
        services.AddSingleton<IClickRepository>(sp => Options(sp).Provider switch
        {
            StorageProvider.Sqlite => sp.GetRequiredService<SqliteClickRepository>(),
            _ => sp.GetRequiredService<InMemoryClickRepository>(),
        });

        services.AddSingleton(sp => new ChannelClickRecorder(
            sp.GetRequiredService<AnalyticsOptions>(), sp.GetRequiredService<ShortenerMetrics>()));
        services.AddSingleton<IClickRecorder>(sp => sp.GetRequiredService<ChannelClickRecorder>());
        services.AddHostedService<ClickFlushService>();
        services.AddHostedService<ClickRetentionService>();
    }

    private static StorageOptions Options(IServiceProvider sp) => sp.GetRequiredService<IOptions<StorageOptions>>().Value;
}

/// <summary>Prepares the backing store (e.g. creates schema) before the app starts serving.</summary>
public interface IStorageInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

internal sealed class NoOpStorageInitializer : IStorageInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
