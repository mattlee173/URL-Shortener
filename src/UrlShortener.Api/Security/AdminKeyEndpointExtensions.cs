using UrlShortener.Api.Configuration;

namespace UrlShortener.Api.Security;

/// <summary>Endpoint metadata marking an operation as admin-only (read by the OpenAPI document).</summary>
public sealed class AdminKeyRequired;

internal static class AdminKeyEndpointExtensions
{
    /// <summary>
    /// Everything an admin-only endpoint needs, applied together so none can be forgotten: the per-IP
    /// admin rate limit (enforced before the key check, so guesses are throttled), the API-key filter,
    /// and the metadata that documents the requirement.
    /// </summary>
    public static RouteHandlerBuilder RequireAdminKey(this RouteHandlerBuilder builder) =>
        builder
            .RequireRateLimiting(RateLimitingSetup.AdminPolicy)
            .AddEndpointFilter<ApiKeyEndpointFilter>()
            .WithMetadata(new AdminKeyRequired());
}
