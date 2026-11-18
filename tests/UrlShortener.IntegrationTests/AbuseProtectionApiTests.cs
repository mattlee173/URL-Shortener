using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UrlShortener.Api.Contracts;

namespace UrlShortener.IntegrationTests;

/// <summary>Scenario 3: expiry and abuse protection, end to end.</summary>
public sealed class AbuseProtectionApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public AbuseProtectionApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateNonRedirectingClient();
    }

    [Fact]
    public async Task Create_WithFutureExpiry_ReturnsExpiresAt()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(1);

        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com", null, expiresAt));
        var body = await response.Content.ReadFromJsonAsync<LinkResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(body!.ExpiresAt);
        Assert.InRange(body.ExpiresAt!.Value, expiresAt.AddSeconds(-1), expiresAt.AddSeconds(1));
    }

    [Fact]
    public async Task Create_WithPastExpiry_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/links", new CreateLinkRequest("https://example.com", null, DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("expiry_in_past", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task ExpiredLink_Returns410()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/links", new CreateLinkRequest("https://example.com", null, DateTimeOffset.UtcNow.AddSeconds(2)));
        var link = await response.Content.ReadFromJsonAsync<LinkResponse>();
        Assert.Equal(HttpStatusCode.Found, (await _client.GetAsync(new Uri($"/{link!.Code}", UriKind.Relative))).StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        var expired = await _client.GetAsync(new Uri($"/{link.Code}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal("link_expired", await ErrorCodeAsync(expired));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/", "url_host_not_allowed")]
    [InlineData("http://localhost:8080/admin", "url_host_not_allowed")]
    [InlineData("https://user:secret@example.com/", "url_credentials_not_allowed")]
    [InlineData("https://sho.rt/abc1234", "url_self_reference")]
    public async Task Create_WithUnsafeDestination_Returns400(string url, string expectedCode)
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest(url, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task InternationalisedDomain_RedirectsWithAsciiLocation()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://bücher.example/", null));
        var link = await response.Content.ReadFromJsonAsync<LinkResponse>();

        var redirect = await _client.GetAsync(new Uri($"/{link!.Code}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal("https://xn--bcher-kva.example/", redirect.Headers.Location?.AbsoluteUri);
    }

    [Fact]
    public async Task Create_OverRateLimit_Returns429WithRetryAfter()
    {
        using var limited = _factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:CreatePermitsPerMinute", "3"));
        using var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com", null));
            statuses.Add(last.StatusCode);
        }

        Assert.Equal(
            new[] { HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests },
            statuses);
        Assert.NotNull(last!.Headers.RetryAfter);
        Assert.Equal("application/problem+json", last.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Metadata_SharesTheRedirectRateLimit()
    {
        // Regression: GET /api/links/{code} had no rate limit, so a scanner could enumerate codes
        // through it while the redirect endpoint's limit was in force.
        using var limited = _factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:RedirectPermitsPerMinute", "3"));
        using var client = limited.CreateClient(new() { AllowAutoRedirect = false });
        var created = await client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com", null));
        var code = (await created.Content.ReadFromJsonAsync<LinkResponse>())!.Code;

        var statuses = new List<HttpStatusCode>
        {
            (await client.GetAsync(new Uri($"/{code}", UriKind.Relative))).StatusCode,
            (await client.GetAsync(new Uri($"/{code}", UriKind.Relative))).StatusCode,
            (await client.GetAsync(new Uri($"/api/links/{code}", UriKind.Relative))).StatusCode,
            (await client.GetAsync(new Uri($"/api/links/{code}", UriKind.Relative))).StatusCode,
        };

        Assert.Equal(
            new[] { HttpStatusCode.Found, HttpStatusCode.Found, HttpStatusCode.OK, HttpStatusCode.TooManyRequests },
            statuses);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }
}
