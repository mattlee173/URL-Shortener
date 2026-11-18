namespace UrlShortener.Api.Security;

/// <summary>Bound from the "Security" configuration section.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Key required (in the X-Api-Key header) for management operations such as delete.
    /// Supply via user-secrets or environment variable (Security__AdminApiKey), never source control.
    /// If unset, management endpoints are disabled (fail closed) rather than left open.
    /// </summary>
    public string? AdminApiKey { get; set; }
}
