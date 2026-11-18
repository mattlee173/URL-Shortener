# Testing Approach

## Pyramid

| Layer | Project | What it proves | Speed |
|---|---|---|---|
| Unit | `tests/UrlShortener.UnitTests` | Business rules in isolation: validators, code generation, service decisions, expiry boundaries, analytics aggregation, buffer/flush behaviour | ms |
| Contract | same project (`*ContractTests`) | Every repository implementation behaves the same (in-memory vs SQLite), including concurrency | ms–100 ms |
| Integration | `tests/UrlShortener.IntegrationTests` | The real HTTP pipeline (routing, binding, filters, middleware, problem details, rate limiting) against a throwaway SQLite file via `WebApplicationFactory<Program>` | ~seconds |

Run everything with:

```bash
dotnet test
```

or one layer: `dotnet test tests/UrlShortener.UnitTests`.

## Techniques used

- **Deterministic time.** All time-dependent logic takes `TimeProvider`. Tests use
  `ManualTimeProvider`, so expiry boundaries ("valid at T−1 min, Gone at exactly T") are exact, not
  sleep-based.
- **Scripted randomness.** `SequenceCodeGenerator` forces collisions to test the retry path and the
  give-up path.
- **Contract test suites.** Abstract test classes (`ShortLinkRepositoryContractTests`,
  `ClickRepositoryContractTests`) run unchanged against each implementation, which keeps the
  in-memory fakes honest.
- **Concurrency.** 20 parallel inserts of the same code must yield exactly one winner, on both
  stores.
- **Regression tests for every defect found** (malformed JSON 500, timestamp precision mismatch,
  OS-dependent URI parsing, Unicode host 500, shutdown click loss, metadata enumeration bypass,
  cache/delete race, IPv4 hidden in IPv6, admin throttling). Each is commented with what went wrong,
  and the later ones were confirmed to fail on the old code.
- **Deterministic interleaving.** The cache race test pauses a read after it fetched its row, runs a
  delete, then releases the read. No sleeps, no flakiness.
- **API contract snapshot.** `OpenApiDocumentTests` compares the live OpenAPI document with
  `docs/openapi.v1.json`; set `UPDATE_OPENAPI_SNAPSHOT=1` to accept an intended change.
- **Metrics as tests.** A `MeterListener` scoped to a private meter checks each instrument.
- **Security as tests.** 20+ encodings of internal addresses, credential URLs, self-links,
  block-list subdomain matching.
- **No fixed sleeps for async work** in integration tests. Click counting polls the stats endpoint
  with a deadline. (The one exception is the real-clock expiry test, which waits 2.5 s on purpose to
  prove expiry end to end; the exact boundary is covered by unit tests.)
- **Configuration overrides per test fixture** (`UseSetting`) instead of global state. The rate-limit
  test spins up a variant host with a limit of 3 via `WithWebHostBuilder`.

## Quality gates

| Gate | Where |
|---|---|
| Compiler + nullable analysis + .NET analyzers (`latest-recommended`), **warnings as errors** | every build (`Directory.Build.props`) |
| Code style enforced at build | `.editorconfig` + `EnforceCodeStyleInBuild` |
| Unit + integration tests (171 + 36) | `dotnet test`, CI |
| API contract drift | OpenAPI snapshot test |
| Redirect latency budget (p99 < 10 ms) | `tools/UrlShortener.LoadTest` (manual / pre-release) |
| Vulnerable dependency scan | CI: `dotnet list package --vulnerable --include-transitive` |
| Manual exploratory checks | redirect headers, 413/429 against real Kestrel, legacy-DB migration, SIGINT drain |

## Performance (load test)

`tools/UrlShortener.LoadTest` is a dependency-free load generator for the redirect hot path. It creates
a pool of links, warms the cache, runs N concurrent workers for a fixed time, and prints throughput and
latency percentiles. It exits non-zero if any redirect fails or p99 exceeds the budget, so it can gate a
pipeline.

```powershell
# Terminal 1: API in Release, rate limiting off, throwaway database
$env:ASPNETCORE_ENVIRONMENT="Development"; $env:RateLimiting__Enabled="false"; $env:Storage__ConnectionString="Data Source=load.db"
dotnet run -c Release --no-launch-profile --project src/UrlShortener.Api --urls http://localhost:5260

# Terminal 2
dotnet run -c Release --project tools/UrlShortener.LoadTest -- --base-url http://localhost:5260 --concurrency 32 --duration 20
```

**Baseline (2026-11-17, engineer's Windows 11 workstation, client and server on the same machine,
Release build, 200 links, 32 workers, 20 s after a 3 s warm-up):**

| Metric | Result |
|---|---|
| Throughput | ~216,000 redirects/s |
| Latency p50 / p90 / p99 / p99.9 | 0.12 / 0.19 / **0.76** / 2.61 ms (max 33.6 ms) |
| Failed redirects | 0 of 4.31 M |
| Target (p99 < 10 ms on a cache hit) | **Met**, with a large margin |
| Clicks persisted during the run | ~733 k of ~5 M (≈ 32 k clicks/s sustained into SQLite) |

What the last row shows: at this rate the analytics pipeline saturates and the bounded buffer **drops**
most click events, while redirects stay fast and error-free. That is the designed degradation
(analytics are best-effort, redirects come first), and the drops are counted in
`urlshortener.clicks.dropped`. **About 32 k clicks/s is this machine's analytics ceiling with SQLite.**
Above that, click counts undercount, so a deployment expecting that traffic needs a durable queue or
a server database (see [RISKS.md](RISKS.md) §3). The numbers are indicative: a loopback client shares
the CPU with the server, and a production setup adds network and TLS.

## Not covered (yet)

- Load testing from a separate machine, over the network and TLS, and with a mixed create/redirect
  workload.
- Mutation testing (Stryker.NET) to measure how strong the assertions are.
- Multi-instance behaviour (cache staleness, distributed rate limits). Out of scope with
  in-process state; see [RISKS.md](RISKS.md).
- `Kestrel` request-body limit inside `WebApplicationFactory` (TestServer does not apply Kestrel
  limits). Verified manually instead.
