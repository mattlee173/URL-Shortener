using UrlShortener.Api.Contracts;
using UrlShortener.Api.Security;
using UrlShortener.Core;
using UrlShortener.Core.Analytics;
using UrlShortener.Core.Domain;
using UrlShortener.Core.Services;
using UrlShortener.Core.Validation;

namespace UrlShortener.Api.Endpoints;

internal static class LinkEndpoints
{
    public static IEndpointRouteBuilder MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/links").WithTags("Links");

        api.MapPost("/", CreateAsync)
            .RequireRateLimiting(RateLimitingSetup.CreatePolicy)
            .WithName("CreateLink")
            .WithSummary("Create a short link, optionally with a custom alias.");

        api.MapGet("/{code}", GetAsync)
            .WithName("GetLink")
            .WithSummary("Get metadata for a short link.");

        api.MapDelete("/{code}", DeleteAsync)
            .WithName("DeleteLink")
            .WithSummary("Soft-delete a short link (requires X-Api-Key). The code is never reissued.")
            .AddEndpointFilter<ApiKeyEndpointFilter>();

        api.MapGet("/{code}/stats", GetStatsAsync)
            .WithName("GetLinkStats")
            .WithSummary("Click analytics for a link (requires X-Api-Key).")
            .AddEndpointFilter<ApiKeyEndpointFilter>();

        app.MapGet("/{code}", RedirectAsync)
            .RequireRateLimiting(RateLimitingSetup.RedirectPolicy)
            .WithName("FollowLink")
            .WithTags("Redirect")
            .WithSummary("Redirect to the link's destination.");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateLinkRequest request, ShortLinkService service, ShortenerOptions options, CancellationToken ct)
    {
        var result = await service.CreateAsync(new CreateShortLinkCommand(request.Url, request.CustomAlias, request.ExpiresAt), ct);
        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        var response = ToResponse(result.Value, options);
        return TypedResults.Created($"/api/links/{response.Code}", response);
    }

    private static async Task<IResult> GetAsync(
        string code, ShortLinkService service, ShortenerOptions options, CancellationToken ct)
    {
        var result = await service.GetAsync(code, ct);
        return result.IsSuccess ? TypedResults.Ok(ToResponse(result.Value, options)) : result.Error!.ToProblem();
    }

    private static async Task<IResult> DeleteAsync(string code, ShortLinkService service, CancellationToken ct)
    {
        var result = await service.DeleteAsync(code, ct);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
    }

    private static async Task<IResult> GetStatsAsync(string code, LinkAnalyticsService analytics, CancellationToken ct)
    {
        var result = await analytics.GetStatsAsync(code, ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> RedirectAsync(
        string code, ShortLinkService service, LinkAnalyticsService analytics, HttpContext http, CancellationToken ct)
    {
        if (!AliasValidator.IsWellFormedCode(code))
        {
            return TypedResults.NotFound();
        }

        var result = await service.ResolveAsync(code, ct);
        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        // Best-effort and non-blocking: an analytics backlog or outage never delays or fails a redirect.
        analytics.RecordClick(
            result.Value.Code,
            http.Connection.RemoteIpAddress,
            http.Request.Headers.UserAgent.ToString(),
            http.Request.Headers.Referer.ToString());

        // 302 rather than 301: browsers cache 301s indefinitely, which would make deletion
        // ineffective for anyone who has already followed the link.
        http.Response.Headers.CacheControl = "private, max-age=0";
        return TypedResults.Redirect(result.Value.TargetUrl, permanent: false);
    }

    private static LinkResponse ToResponse(ShortLink link, ShortenerOptions options) => new(
        link.Code,
        $"{options.BaseUrl.TrimEnd('/')}/{link.Code}",
        link.TargetUrl,
        link.CreatedAt,
        link.ExpiresAt,
        link.IsDeleted);
}
