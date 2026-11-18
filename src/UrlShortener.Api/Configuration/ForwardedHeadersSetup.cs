using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Configuration;

/// <summary>Bound from the "ForwardedHeaders" configuration section.</summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// Off by default: on bare Kestrel the connection's address is the client, and honouring
    /// X-Forwarded-For would let any client choose its own IP (and so its rate-limit bucket).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Exact proxy addresses (e.g. "10.0.0.5") allowed to set X-Forwarded-For.</summary>
    public IReadOnlyList<string> KnownProxies { get; set; } = [];

    /// <summary>Proxy networks in CIDR form (e.g. "10.0.0.0/8") allowed to set X-Forwarded-For.</summary>
    public IReadOnlyList<string> KnownNetworks { get; set; } = [];

    /// <summary>How many proxy hops to unwind. 1 = a single load balancer in front of the app.</summary>
    public int ForwardLimit { get; set; } = 1;
}

/// <summary>
/// Restores the real client IP behind a reverse proxy, so per-IP rate limiting and visitor hashing
/// work. Only explicitly listed proxies are trusted, and enabling it with an empty list fails startup:
/// ASP.NET Core treats empty known lists as "trust everyone", which is exactly the spoofing hole.
/// </summary>
internal static class ForwardedHeadersSetup
{
    public static IServiceCollection AddForwardedHeadersSupport(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ForwardedHeadersSettings>()
            .Bind(configuration.GetSection(ForwardedHeadersSettings.SectionName))
            .Validate(
                o => !o.Enabled || o.KnownProxies.Count + o.KnownNetworks.Count > 0,
                "ForwardedHeaders:Enabled requires at least one KnownProxies or KnownNetworks entry. " +
                "Trusting X-Forwarded-For from any source lets clients spoof their IP.")
            .Validate(o => o.KnownProxies.All(p => IPAddress.TryParse(p, out _)),
                "ForwardedHeaders:KnownProxies must contain IP addresses.")
            .Validate(o => o.KnownNetworks.All(n => System.Net.IPNetwork.TryParse(n, out _)),
                "ForwardedHeaders:KnownNetworks must contain CIDR networks (e.g. 10.0.0.0/8).")
            .Validate(o => o.ForwardLimit >= 1, "ForwardedHeaders:ForwardLimit must be >= 1.")
            .ValidateOnStart();

        // Read lazily so WebApplicationFactory and environment overrides apply.
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ForwardedHeadersSettings>>((forwarded, settings) =>
            {
                var s = settings.Value;

                // Only the client address is needed. Short URLs are built from Shortener:BaseUrl, not
                // from the request, so X-Forwarded-Host/Proto are deliberately not honoured.
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                forwarded.ForwardLimit = s.ForwardLimit;

                // Replace the framework default (loopback) with exactly what the operator configured.
                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();
                foreach (var proxy in s.KnownProxies)
                {
                    forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
                }

                foreach (var network in s.KnownNetworks)
                {
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }
            });

        return services;
    }

    /// <summary>Must run first, so everything after it (rate limiter, endpoints) sees the client IP.</summary>
    public static WebApplication UseForwardedHeadersSupport(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value.Enabled)
        {
            app.UseForwardedHeaders();
        }

        return app;
    }
}
