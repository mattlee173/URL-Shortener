using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

using UrlShortener.Infrastructure.Observability;

namespace UrlShortener.Api.Configuration;

/// <summary>
/// Exports metrics over OTLP (to an OpenTelemetry Collector, Prometheus with OTLP ingestion, Azure
/// Monitor, Grafana, etc.) when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set. When it is not set, the SDK
/// is not registered at all: there is no background export loop retrying a collector that isn't there,
/// and the meters stay observable locally with <c>dotnet-counters</c>.
/// </summary>
internal static class TelemetrySetup
{
    /// <summary>Meters exported: the app's own, plus the ASP.NET Core and runtime built-ins.</summary>
    internal static readonly string[] Meters =
    [
        ShortenerMetrics.MeterName,
        "Microsoft.AspNetCore.Hosting",        // http.server.request.duration (redirect latency, by route + status)
        "Microsoft.AspNetCore.Server.Kestrel", // connections, TLS handshakes
        "Microsoft.AspNetCore.RateLimiting",   // rejected/queued leases per policy
        "System.Runtime",                      // GC, thread pool, exceptions
    ];

    /// <remarks>
    /// Reads configuration at registration, unlike the options classes (see CLAUDE.md). This is a
    /// deliberate exception: whether an exporter exists is a deployment switch set through the
    /// standard OpenTelemetry environment variable, not behaviour tests override; the
    /// OpenTelemetry/.NET Aspire templates use the same pattern. Exporter settings (endpoint,
    /// protocol, headers, interval) are still read lazily by the SDK from the standard OTEL_* variables.
    /// </remarks>
    public static IServiceCollection AddUrlShortenerTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("UrlShortener.Api"))
            .WithMetrics(metrics => metrics.AddMeter(Meters).AddOtlpExporter());

        return services;
    }
}
