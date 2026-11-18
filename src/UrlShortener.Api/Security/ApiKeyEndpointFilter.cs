using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Security;

/// <summary>Guards management endpoints with a shared admin API key.</summary>
public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    // SHA-256 of the key, not the key itself: comparing fixed-length digests keeps the comparison
    // constant-time even when the supplied key has a different length (FixedTimeEquals returns
    // early on a length mismatch, which would leak the real key's length).
    private readonly byte[]? _expectedKeyHash;

    public ApiKeyEndpointFilter(IOptions<SecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = options.Value.AdminApiKey;
        _expectedKeyHash = string.IsNullOrWhiteSpace(key) ? null : Hash(key);
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (_expectedKeyHash is null)
        {
            return TypedResults.Problem(
                detail: "Management endpoints are disabled because no admin API key is configured.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();

        // Constant-time comparison so response timing leaks neither how much of the key matched nor its length.
        if (string.IsNullOrEmpty(provided) ||
            !CryptographicOperations.FixedTimeEquals(Hash(provided), _expectedKeyHash))
        {
            return TypedResults.Problem(
                detail: $"A valid {HeaderName} header is required.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context).ConfigureAwait(false);
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
