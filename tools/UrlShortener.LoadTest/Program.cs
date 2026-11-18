using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;

// Load test for GET /{code}, the redirect hot path. It creates a pool of links, warms the cache, then
// runs N concurrent workers for a fixed duration and reports throughput and latency percentiles.
// The exit code is non-zero if the p99 budget is exceeded or any redirect fails, so it can gate a pipeline.
//
// Usage (start the API with rate limiting off first, see README, "Testing approach"):
//   dotnet run -c Release --project tools/UrlShortener.LoadTest -- --base-url http://localhost:5260
//
// Options: --base-url, --links (200), --concurrency (32), --duration (20 s), --warmup (3 s), --p99-ms (10)

var settings = Settings.Parse(args);
Console.WriteLine(
    $"Target {settings.BaseUrl}  links={settings.Links}  concurrency={settings.Concurrency}  " +
    $"duration={settings.Duration.TotalSeconds:0}s  warmup={settings.Warmup.TotalSeconds:0}s  p99 budget={settings.P99BudgetMs}ms");

using var handler = new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    MaxConnectionsPerServer = settings.Concurrency,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
};
using var http = new HttpClient(handler) { BaseAddress = settings.BaseUrl, Timeout = TimeSpan.FromSeconds(10) };

var codes = await CreateLinksAsync(http, settings.Links);
Console.WriteLine($"Created {codes.Length} links.");

await RunAsync(http, codes, settings.Concurrency, settings.Warmup, record: false);
var result = await RunAsync(http, codes, settings.Concurrency, settings.Duration, record: true);

result.Print(settings.Duration);
var passed = result.Failures == 0 && result.Percentile(0.99) <= settings.P99BudgetMs;
Console.WriteLine(passed ? "PASS" : "FAIL");
return passed ? 0 : 1;

static async Task<string[]> CreateLinksAsync(HttpClient http, int count)
{
    var codes = new string[count];
    for (var i = 0; i < count; i++)
    {
        using var response = await http.PostAsJsonAsync("/api/links", new { url = $"https://example.com/load/{i}" });
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException(
                $"Creating link {i} returned {(int)response.StatusCode}. Is rate limiting disabled (RateLimiting__Enabled=false)?");
        }

        var body = await response.Content.ReadFromJsonAsync<CreatedLink>();
        codes[i] = body!.Code;
    }

    return codes;
}

static async Task<RunResult> RunAsync(HttpClient http, string[] codes, int concurrency, TimeSpan duration, bool record)
{
    var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
    var workers = Enumerable.Range(0, concurrency).Select(async worker =>
    {
        var latencies = new List<double>(capacity: 50_000);
        long failures = 0;
        var random = new Random(worker);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var code = codes[random.Next(codes.Length)];
            var start = Stopwatch.GetTimestamp();
            try
            {
                using var response = await http.GetAsync(new Uri($"/{code}", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead);
                if (response.StatusCode != HttpStatusCode.Found)
                {
                    failures++;
                }
            }
            catch (HttpRequestException)
            {
                failures++;
            }
            catch (TaskCanceledException)
            {
                failures++; // client timeout
            }

            if (record)
            {
                latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }

        return (latencies, failures);
    }).ToArray();

    var results = await Task.WhenAll(workers);
    var all = results.SelectMany(r => r.latencies).ToArray();
    Array.Sort(all);
    return new RunResult(all, results.Sum(r => r.failures));
}

internal sealed record CreatedLink(string Code);

internal sealed record RunResult(double[] SortedLatenciesMs, long Failures)
{
    public double Percentile(double p) =>
        SortedLatenciesMs.Length == 0 ? double.NaN : SortedLatenciesMs[(int)Math.Ceiling(p * SortedLatenciesMs.Length) - 1];

    public void Print(TimeSpan duration)
    {
        var n = SortedLatenciesMs.Length;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Requests {n:N0}  ({n / duration.TotalSeconds:N0} req/s)  failures {Failures}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Latency ms  p50 {Percentile(0.50):0.00}  p90 {Percentile(0.90):0.00}  p99 {Percentile(0.99):0.00}  " +
            $"p99.9 {Percentile(0.999):0.00}  max {(n == 0 ? double.NaN : SortedLatenciesMs[^1]):0.00}"));
    }
}

internal sealed record Settings(Uri BaseUrl, int Links, int Concurrency, TimeSpan Duration, TimeSpan Warmup, double P99BudgetMs)
{
    public static Settings Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        int Int(string name, int fallback) => int.Parse(Get(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

        return new Settings(
            new Uri(Get("--base-url", "http://localhost:5260")),
            Int("--links", 200),
            Int("--concurrency", 32),
            TimeSpan.FromSeconds(Int("--duration", 20)),
            TimeSpan.FromSeconds(Int("--warmup", 3)),
            double.Parse(Get("--p99-ms", "10"), CultureInfo.InvariantCulture));
    }
}
