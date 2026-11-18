using Microsoft.AspNetCore.Diagnostics;

namespace UrlShortener.Api.Errors;

/// <summary>
/// Converts request-binding failures (malformed JSON, wrong content type, oversized body)
/// into 4xx problem details. Without this, ASP.NET Core rethrows them in Development and the
/// client receives a misleading 500 for what is a client error.
/// </summary>
internal sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        httpContext.Response.StatusCode = badRequest.StatusCode;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = badRequest.StatusCode,
                Title = "The request body could not be read.",
                Detail = "Ensure the body is valid JSON matching the documented schema.",
            },
        }).ConfigureAwait(false);
    }
}
