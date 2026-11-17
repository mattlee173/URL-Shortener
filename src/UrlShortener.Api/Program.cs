using Microsoft.Extensions.Options;
using UrlShortener.Api;
using UrlShortener.Api.Endpoints;
using UrlShortener.Api.Security;
using UrlShortener.Core;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Analytics;
using UrlShortener.Core.Services;
using UrlShortener.Core.Validation;
using UrlShortener.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration (validated at startup so misconfiguration fails fast) ----
builder.Services.AddOptions<ShortenerOptions>()
    .Bind(builder.Configuration.GetSection(ShortenerOptions.SectionName))
    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "Shortener:BaseUrl must be an absolute URL.")
    .Validate(o => o.CodeLength is >= 4 and <= 32, "Shortener:CodeLength must be 4-32.")
    .Validate(o => o.MaxGenerationAttempts >= 1, "Shortener:MaxGenerationAttempts must be >= 1.")
    .Validate(o => o.MaxExpiryDays >= 1, "Shortener:MaxExpiryDays must be >= 1.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ShortenerOptions>>().Value);
builder.Services.AddOptions<AnalyticsOptions>()
    .Bind(builder.Configuration.GetSection(AnalyticsOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.VisitorHashKey), "Analytics:VisitorHashKey must be configured (use user-secrets or an environment variable).")
    .Validate(o => o.BufferCapacity > 0 && o.BatchSize > 0 && o.FlushIntervalMilliseconds >= 0, "Analytics buffer settings must be positive.")
    .Validate(o => o.StatsWindowDays is >= 1 and <= 366 && o.TopReferrers is >= 1 and <= 100, "Analytics stats settings out of range.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AnalyticsOptions>>().Value);
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));

// ---- Application + infrastructure ----
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IShortCodeGenerator, RandomShortCodeGenerator>();
builder.Services.AddSingleton<TargetUrlValidator>();
builder.Services.AddSingleton<ShortLinkService>();
builder.Services.AddSingleton(sp => new ClickEventFactory(sp.GetRequiredService<AnalyticsOptions>().VisitorHashKey));
builder.Services.AddSingleton<LinkAnalyticsService>();
builder.Services.AddInfrastructure(builder.Configuration);

// ---- HTTP concerns ----
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddUrlShortenerRateLimiting(builder.Configuration);

// Requests are tiny JSON documents; cap bodies well below Kestrel's 30 MB default to blunt
// memory-exhaustion attempts.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 16 * 1024);

var app = builder.Build();

await app.Services.GetRequiredService<IStorageInitializer>().InitializeAsync(CancellationToken.None);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

app.MapHealthChecks("/health");
app.MapLinkEndpoints();

await app.RunAsync();

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
