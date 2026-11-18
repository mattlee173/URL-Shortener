using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UrlShortener.Api.Contracts;

namespace UrlShortener.IntegrationTests;

public sealed class LinkApiTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public LinkApiTests(ApiFactory factory)
    {
        _client = factory.CreateNonRedirectingClient();
    }

    [Fact]
    public async Task Create_ThenFollow_RedirectsToTarget()
    {
        var created = await CreateAsync(new CreateLinkRequest("https://example.com/landing?ref=1", null));

        var redirect = await _client.GetAsync(new Uri($"/{created.Code}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal(new Uri("https://example.com/landing?ref=1"), redirect.Headers.Location);
        Assert.Equal($"https://sho.rt/{created.Code}", created.ShortUrl);
    }

    [Fact]
    public async Task Create_ReturnsCreatedWithLocationOfMetadataResource()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com", null));
        var body = await response.Content.ReadFromJsonAsync<LinkResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/links/{body!.Code}", response.Headers.Location?.OriginalString);

        var metadata = await _client.GetFromJsonAsync<LinkResponse>(response.Headers.Location);
        Assert.Equal(body, metadata);
    }

    [Fact]
    public async Task Create_WithCustomAlias_ThenDuplicate_Returns409()
    {
        var alias = UniqueAlias();
        await CreateAsync(new CreateLinkRequest("https://example.com/a", alias));

        var duplicate = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/b", alias));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("alias_taken", await ErrorCodeAsync(duplicate));
    }

    [Theory]
    [InlineData("javascript:alert(1)", "url_scheme_not_allowed")]
    [InlineData("not-a-url", "url_invalid")]
    [InlineData("", "url_required")]
    public async Task Create_WithInvalidUrl_Returns400ProblemDetails(string url, string expectedCode)
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest(url, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedCode, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Create_WithMalformedJson_Returns400NotServerError()
    {
        using var content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync(new Uri("/api/links", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Follow_UnknownCode_Returns404()
    {
        var response = await _client.GetAsync(new Uri("/doesNotExist", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutApiKey_Returns401AndLinkStillWorks()
    {
        var created = await CreateAsync(new CreateLinkRequest("https://example.com", null));

        var delete = await _client.DeleteAsync(new Uri($"/api/links/{created.Code}", UriKind.Relative));
        var follow = await _client.GetAsync(new Uri($"/{created.Code}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Found, follow.StatusCode);
    }

    [Fact]
    public async Task Delete_WithApiKey_ThenFollow_Returns410()
    {
        var created = await CreateAsync(new CreateLinkRequest("https://example.com", null));

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/links/{created.Code}");
        request.Headers.Add("X-Api-Key", ApiFactory.AdminKey);
        var delete = await _client.SendAsync(request);
        var follow = await _client.GetAsync(new Uri($"/{created.Code}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, follow.StatusCode);
    }

    [Fact]
    public async Task Metadata_OfDeletedLink_Returns410WithoutTarget()
    {
        // Regression: metadata for a deleted link was public and included targetUrl, so a link taken
        // down for abuse kept advertising its destination.
        var created = await CreateAsync(new CreateLinkRequest("https://phish.example/login", null));
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/links/{created.Code}");
        request.Headers.Add("X-Api-Key", ApiFactory.AdminKey);
        await _client.SendAsync(request);

        var metadata = await _client.GetAsync(new Uri($"/api/links/{created.Code}", UriKind.Relative));
        var body = await metadata.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Gone, metadata.StatusCode);
        Assert.Equal("link_deleted", await ErrorCodeAsync(metadata));
        Assert.DoesNotContain("phish.example", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_ReturnsHealthy()
    {
        var response = await _client.GetStringAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal("Healthy", response);
    }

    private async Task<LinkResponse> CreateAsync(CreateLinkRequest request)
    {
        var response = await _client.PostAsJsonAsync("/api/links", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LinkResponse>())!;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    private static string UniqueAlias() => "it-" + Guid.NewGuid().ToString("N")[..12];
}
