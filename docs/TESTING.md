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
  OS-dependent URI parsing, Unicode host 500). Each is commented with what went wrong.
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
| Unit + integration tests | `dotnet test`, CI |
| Vulnerable dependency scan | CI: `dotnet list package --vulnerable --include-transitive` |
| Manual exploratory checks | redirect headers, 413/429 against real Kestrel, legacy-DB migration, SIGINT drain |

## Not covered (yet)

- Load/performance testing (suggested: k6 or NBomber against `/{code}`; target p99 < 10 ms on a cache
  hit).
- Mutation testing (Stryker.NET) to measure how strong the assertions are.
- Multi-instance behaviour (cache staleness, distributed rate limits). Out of scope with
  in-process state; see [RISKS.md](RISKS.md).
- `Kestrel` request-body limit inside `WebApplicationFactory` (TestServer does not apply Kestrel
  limits). Verified manually instead.
