using UrlShortener.Core.Domain;

namespace UrlShortener.Api.Errors;

/// <summary>Translates domain errors into RFC 9457 problem details responses.</summary>
internal static class ResultMapping
{
    public static IResult ToProblem(this DomainError error)
    {
        if (error.Kind == ErrorKind.Validation)
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]> { [error.Field ?? "request"] = [error.Message] },
                title: "The request is invalid.",
                extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
        }

        var status = error.Kind switch
        {
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Gone => StatusCodes.Status410Gone,
            ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        return TypedResults.Problem(
            detail: error.Message,
            statusCode: status,
            extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
    }
}
