using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UrlShortener.IntegrationTests;

/// <summary>
/// Hosts the real API in-process against a throwaway SQLite database, so tests exercise the
/// full pipeline: routing, binding, validation, persistence and problem-details mapping.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string AdminKey = "integration-test-admin-key";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"urlshortener-it-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:Provider", "Sqlite");
        builder.UseSetting("Storage:ConnectionString", $"Data Source={_dbPath};Pooling=False");
        builder.UseSetting("Shortener:BaseUrl", "https://sho.rt");
        builder.UseSetting("Security:AdminApiKey", AdminKey);
        builder.UseSetting("Analytics:VisitorHashKey", "integration-test-visitor-key");
        builder.UseSetting("Analytics:FlushIntervalMilliseconds", "0");

        // Tests create many links from one client; rate limiting has its own dedicated tests.
        builder.UseSetting("RateLimiting:CreatePermitsPerMinute", "100000");
        builder.UseSetting("RateLimiting:RedirectPermitsPerMinute", "100000");
    }

    /// <summary>Client that does not follow redirects, so tests can assert on the 302 itself.</summary>
    public HttpClient CreateNonRedirectingClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Client that sends the admin API key on every request.</summary>
    public HttpClient CreateAdminClient()
    {
        var client = CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", AdminKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }
}
