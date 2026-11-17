using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Security;

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

/// <summary>Guards management endpoints with a shared admin API key.</summary>
public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    private readonly byte[]? _expectedKey;

    public ApiKeyEndpointFilter(IOptions<SecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = options.Value.AdminApiKey;
        _expectedKey = string.IsNullOrWhiteSpace(key) ? null : Encoding.UTF8.GetBytes(key);
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (_expectedKey is null)
        {
            return TypedResults.Problem(
                detail: "Management endpoints are disabled because no admin API key is configured.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();

        // Constant-time comparison so response timing does not leak how much of the key matched.
        if (string.IsNullOrEmpty(provided) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), _expectedKey))
        {
            return TypedResults.Problem(
                detail: $"A valid {HeaderName} header is required.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context).ConfigureAwait(false);
    }
}
