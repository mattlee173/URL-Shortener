using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Contracts;
using UrlShortener.Core.Analytics;

namespace UrlShortener.IntegrationTests;

public sealed class AnalyticsApiTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;
    private readonly HttpClient _admin;

    public AnalyticsApiTests(ApiFactory factory)
    {
        _client = factory.CreateNonRedirectingClient();
        _admin = factory.CreateAdminClient();
    }

    [Fact]
    public async Task Redirects_AreCountedInStats()
    {
        var link = await CreateLinkAsync();

        await FollowAsync(link.Code, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0", "https://news.example/item?id=1");
        await FollowAsync(link.Code, "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0) Mobile/15E148", "https://news.example/other");
        await FollowAsync(link.Code, "curl/8.5.0", null);

        var stats = await WaitForClicksAsync(link.Code, expected: 3);

        Assert.Equal(3, stats.TotalClicks);
        var referrer = Assert.Single(stats.TopReferrers);
        Assert.Equal(new ReferrerClicks("news.example", 2), referrer);
        Assert.Equal(3, stats.ByClientKind.Count);
        Assert.Single(stats.ClicksByDay);
    }

    [Fact]
    public async Task FailedRedirects_AreNotCounted()
    {
        var link = await CreateLinkAsync();
        await _admin.DeleteAsync(new Uri($"/api/links/{link.Code}", UriKind.Relative));

        var follow = await _client.GetAsync(new Uri($"/{link.Code}", UriKind.Relative));
        await Task.Delay(300);
        var stats = await _admin.GetFromJsonAsync<LinkStats>(new Uri($"/api/links/{link.Code}/stats", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Gone, follow.StatusCode);
        Assert.Equal(0, stats!.TotalClicks);
    }

    [Fact]
    public async Task Stats_WithoutApiKey_Returns401()
    {
        var link = await CreateLinkAsync();

        var response = await _client.GetAsync(new Uri($"/api/links/{link.Code}/stats", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Stats_UnknownCode_Returns404()
    {
        var response = await _admin.GetAsync(new Uri("/api/links/nope-nope/stats", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<LinkResponse> CreateLinkAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/analytics", null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LinkResponse>())!;
    }

    private async Task FollowAsync(string code, string userAgent, string? referrer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/{code}");
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (referrer is not null)
        {
            request.Headers.Referrer = new Uri(referrer);
        }

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    /// <summary>Clicks are persisted asynchronously, so poll briefly rather than sleeping a fixed time.</summary>
    private async Task<LinkStats> WaitForClicksAsync(string code, long expected)
    {
        LinkStats? stats = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            stats = await _admin.GetFromJsonAsync<LinkStats>(new Uri($"/api/links/{code}/stats", UriKind.Relative));
            if (stats!.TotalClicks >= expected)
            {
                return stats;
            }

            await Task.Delay(100);
        }

        return stats!;
    }
}
