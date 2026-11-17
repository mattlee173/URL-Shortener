using Microsoft.Extensions.Options;
using UrlShortener.Api;
using UrlShortener.Api.Endpoints;
using UrlShortener.Api.Security;
using UrlShortener.Core;
using UrlShortener.Core.Abstractions;
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
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ShortenerOptions>>().Value);
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));

// ---- Application + infrastructure ----
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IShortCodeGenerator, RandomShortCodeGenerator>();
builder.Services.AddSingleton<TargetUrlValidator>();
builder.Services.AddSingleton<ShortLinkService>();
builder.Services.AddInfrastructure(builder.Configuration);

// ---- HTTP concerns ----
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddHealthChecks();

var app = builder.Build();

await app.Services.GetRequiredService<IStorageInitializer>().InitializeAsync(CancellationToken.None);

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthChecks("/health");
app.MapLinkEndpoints();

await app.RunAsync();

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
