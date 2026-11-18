using System.Globalization;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Configuration;

/// <summary>Bound from the "RateLimiting" configuration section.</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Link creations allowed per client IP per minute. Creation is the abuse vector (spam, phishing).</summary>
    public int CreatePermitsPerMinute { get; set; } = 20;

    /// <summary>Redirects allowed per client IP per minute. Generous: real users click a few links, scanners click thousands.</summary>
    public int RedirectPermitsPerMinute { get; set; } = 300;

    /// <summary>
    /// Management calls (delete, stats) allowed per client IP per minute, counted before the API key is
    /// checked. Operators make a handful of calls; a key-guessing script makes thousands.
    /// </summary>
    public int AdminPermitsPerMinute { get; set; } = 30;
}

/// <summary>
/// Per-client-IP rate limits. Policies read options per request (not at registration) so tests
/// and config reloads can change limits.
/// </summary>
/// <remarks>
/// Partitioning uses the connection's remote IP. Behind a reverse proxy that is the proxy's IP, so
/// ForwardedHeaders must be configured with trusted proxies before deploying (see README, "Limitations").
/// State is in-process: with N instances the effective limit is N× the configured value.
/// </remarks>
internal static class RateLimitingSetup
{
    public const string CreatePolicy = "create";
    public const string RedirectPolicy = "redirect";
    public const string AdminPolicy = "admin";

    public static IServiceCollection AddUrlShortenerRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(CreatePolicy, http => Partition(http, o => o.CreatePermitsPerMinute));
            limiter.AddPolicy(RedirectPolicy, http => Partition(http, o => o.RedirectPermitsPerMinute));
            limiter.AddPolicy(AdminPolicy, http => Partition(http, o => o.AdminPermitsPerMinute));
            limiter.OnRejected = WriteRejectionAsync;
        });

        return services;
    }

    private static RateLimitPartition<string> Partition(HttpContext http, Func<RateLimitingOptions, int> permits)
    {
        var options = http.RequestServices.GetRequiredService<IOptionsMonitor<RateLimitingOptions>>().CurrentValue;
        if (!options.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("disabled");
        }

        var limit = permits(options);
        var client = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Key includes the limit so a config change starts a fresh limiter instead of reusing the old one.
        return RateLimitPartition.GetFixedWindowLimiter($"{client}|{limit}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    }

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests.",
                Detail = "Rate limit exceeded. Retry after the period indicated by the Retry-After header.",
            },
        }).ConfigureAwait(false);
    }
}
