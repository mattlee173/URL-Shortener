using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UrlShortener.IntegrationTests;

/// <summary>
/// Guards the public API contract. The live OpenAPI document must match the committed snapshot
/// (docs/openapi.v1.json), so any contract change appears as a reviewable diff in the same commit.
/// To accept an intended change, run the tests once with UPDATE_OPENAPI_SNAPSHOT=1.
/// </summary>
public sealed class OpenApiDocumentTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public OpenApiDocumentTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Document_MatchesCommittedSnapshot()
    {
        var live = await GetDocumentAsync();
        var snapshotPath = Path.Combine(RepoRoot(), "docs", "openapi.v1.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            await File.WriteAllTextAsync(snapshotPath, live.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        Assert.True(File.Exists(snapshotPath), $"Missing {snapshotPath}. Run with UPDATE_OPENAPI_SNAPSHOT=1 to create it.");
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath));
        Assert.True(
            JsonNode.DeepEquals(committed, live),
            "The OpenAPI document differs from docs/openapi.v1.json. If the API change is intended, " +
            "rerun the tests with UPDATE_OPENAPI_SNAPSHOT=1 and commit the updated snapshot.");
    }

    [Fact]
    public async Task Document_MarksOnlyManagementOperationsAsRequiringTheApiKey()
    {
        var document = await GetDocumentAsync();
        var paths = document["paths"]!.AsObject();

        bool Secured(string path, string method) => paths[path]?[method]?["security"] is JsonArray { Count: > 0 };

        Assert.True(Secured("/api/links/{code}", "delete"));
        Assert.True(Secured("/api/links/{code}/stats", "get"));
        Assert.False(Secured("/api/links", "post"));
        Assert.False(Secured("/api/links/{code}", "get"));
        Assert.False(Secured("/{code}", "get"));
        Assert.NotNull(document["components"]?["securitySchemes"]?["ApiKey"]);
    }

    private async Task<JsonNode> GetDocumentAsync()
    {
        var response = await _client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    /// <remarks>
    /// Regression: this used to walk up from the build output folder, so the test threw "Could not
    /// locate the repository root" whenever output lived outside the repo (dotnet test --artifacts-path,
    /// some CI layouts). The compiler-supplied path of this source file is always inside the repo.
    /// </remarks>
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UrlShortener.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
