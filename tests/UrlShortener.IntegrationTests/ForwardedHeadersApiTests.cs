using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Api.Contracts;

namespace UrlShortener.IntegrationTests;

/// <summary>Client-IP resolution behind a reverse proxy, observed through per-IP rate limiting.</summary>
public sealed class ForwardedHeadersApiTests : IClassFixture<ApiFactory>
{
    private static readonly IPAddress Proxy = IPAddress.Parse("10.0.0.5");

    private readonly ApiFactory _factory;

    public ForwardedHeadersApiTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TrustedProxy_EachForwardedClientGetsItsOwnRateLimitBucket()
    {
        using var host = Host(remoteIp: Proxy, enabled: true, knownProxy: "10.0.0.5");
        using var client = host.CreateClient();

        var clientA = await CreateManyAsync(client, forwardedFor: "198.51.100.1", count: 3);
        var clientB = await CreateManyAsync(client, forwardedFor: "198.51.100.2", count: 1);

        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests }, clientA);
        Assert.Equal(new[] { HttpStatusCode.Created }, clientB);
    }

    [Fact]
    public async Task UntrustedSource_ForwardedHeaderIsIgnored_SoItCannotDodgeTheLimit()
    {
        // The connection does not come from a configured proxy, so X-Forwarded-For is attacker-controlled.
        using var host = Host(remoteIp: IPAddress.Parse("203.0.113.50"), enabled: true, knownProxy: "10.0.0.5");
        using var client = host.CreateClient();

        var first = await CreateManyAsync(client, forwardedFor: "198.51.100.1", count: 2);
        var spoofed = await CreateManyAsync(client, forwardedFor: "198.51.100.2", count: 1);

        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Created }, first);
        Assert.Equal(new[] { HttpStatusCode.TooManyRequests }, spoofed);
    }

    [Fact]
    public async Task DisabledByDefault_ForwardedHeaderIsIgnored()
    {
        using var host = Host(remoteIp: Proxy, enabled: false, knownProxy: null);
        using var client = host.CreateClient();

        await CreateManyAsync(client, forwardedFor: "198.51.100.1", count: 2);
        var other = await CreateManyAsync(client, forwardedFor: "198.51.100.2", count: 1);

        Assert.Equal(new[] { HttpStatusCode.TooManyRequests }, other);
    }

    [Fact]
    public void Enabled_WithoutAnyTrustedProxy_FailsStartup()
    {
        using var host = Host(remoteIp: Proxy, enabled: true, knownProxy: null);

        var error = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("ForwardedHeaders:Enabled requires", error.ToString(), StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> Host(IPAddress remoteIp, bool enabled, string? knownProxy) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:CreatePermitsPerMinute", "2");
            builder.UseSetting("ForwardedHeaders:Enabled", enabled ? "true" : "false");
            if (knownProxy is not null)
            {
                builder.UseSetting("ForwardedHeaders:KnownProxies:0", knownProxy);
            }

            // TestServer has no real socket; pretend every request arrives from remoteIp.
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(remoteIp)));
        });

    private static async Task<List<HttpStatusCode>> CreateManyAsync(HttpClient client, string forwardedFor, int count)
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/links")
            {
                Content = JsonContent.Create(new CreateLinkRequest("https://example.com", null)),
            };
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }

        return statuses;
    }

    private sealed class RemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remoteIp;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
