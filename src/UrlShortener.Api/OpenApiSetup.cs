using Microsoft.OpenApi;
using UrlShortener.Api.Security;

namespace UrlShortener.Api;

/// <summary>
/// Serves the OpenAPI 3.1 description at <c>/openapi/v1.json</c>. A committed snapshot
/// (<c>docs/openapi.v1.json</c>) is checked by an integration test, so any change to the public
/// contract shows up as a reviewable diff.
/// </summary>
internal static class OpenApiSetup
{
    public const string ApiKeyScheme = "ApiKey";

    public static IServiceCollection AddUrlShortenerOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "URL Shortener API";
                document.Info.Description =
                    "Create short links, follow them, and read click analytics. Errors are RFC 9457 problem " +
                    "details; domain errors carry a stable `errorCode`.";

                // Servers depend on the host the document is requested from; leaving them out keeps the
                // document identical across environments (and the committed snapshot stable).
                document.Servers = [];

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[ApiKeyScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = ApiKeyEndpointFilter.HeaderName,
                    Description = "Admin API key for management operations.",
                };
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<AdminKeyRequired>().Any())
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(ApiKeyScheme, context.Document)] = [],
                    });
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }
}
