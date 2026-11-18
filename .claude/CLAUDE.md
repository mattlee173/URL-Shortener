# CLAUDE.md: project context for Claude Code

URL shortener built for a C#/.NET developer interview assignment ("AI-Assisted Software
Engineering System"). The engineer (Matthew) leads, reviews and approves; AI assists within tasks.
Treat this as production-grade work: modular, testable, secure, with traceable decisions.

## Commands (run from repo root, Windows PowerShell)

```powershell
dotnet build                                  # warnings are errors (Directory.Build.props)
dotnet test                                   # 171 unit + 36 integration test cases, all must pass
dotnet run --project src/UrlShortener.Api     # http://localhost:5260 (Development env)
```

Manual API checks: `src/UrlShortener.Api/UrlShortener.Api.http` (VS Code REST Client). Dev admin key:
`dev-only-admin-key-change-me` (header `X-Api-Key`). OpenAPI: `/openapi/v1.json`; the committed
snapshot `docs/openapi.v1.json` is checked by a test (`UPDATE_OPENAPI_SNAPSHOT=1` to accept changes).
Load test: `tools/UrlShortener.LoadTest` against a live Release instance (see docs/TESTING.md).

## Architecture (ports and adapters, .NET 10, Minimal APIs)

- `src/UrlShortener.Core`: domain, rules, use cases, interfaces. **No package or framework deps.**
  `Result<T>` + `DomainError` for expected failures (no exceptions for control flow).
- `src/UrlShortener.Infrastructure`: SQLite (raw ADO.NET via Microsoft.Data.Sqlite 10.0.15),
  in-memory stores, `CachedShortLinkRepository`, analytics pipeline (bounded channel →
  `ClickFlushService`), `ClickRetentionService`, `ShortenerMetrics` (BCL Meter), versioned
  migrations (`SqliteSchemaInitializer`, `PRAGMA user_version`, currently 4).
- `src/UrlShortener.Api`: endpoints (`Endpoints/`), request/response records (`Contracts/`),
  `Errors/` (`ResultMapping`: domain error → RFC 9457 problem details with `errorCode`; bad-request
  handler), startup wiring per concern in `Configuration/`, API-key filter in `Security/` (`RequireAdminKey()` = admin rate limit
  + filter + OpenAPI metadata), rate limiting, forwarded headers (opt-in), OpenAPI, OTLP telemetry
  (only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set), exception handler.
- Tests: `tests/UrlShortener.UnitTests` (including repository **contract suites** run against
  in-memory and SQLite) and `tests/UrlShortener.IntegrationTests` (`WebApplicationFactory<Program>`,
  temp SQLite file per fixture, config via `UseSetting`).

Full docs: `docs/` (REQUIREMENTS, ARCHITECTURE, SCENARIOS, TESTING, RISKS, AI_USAGE_LOG,
ENGINEERING_SUMMARY).

## Conventions and invariants (don't break these)

- Code allocation is atomic (`INSERT … ON CONFLICT DO NOTHING`). Never check-then-insert.
- Links are soft-deleted; codes are **never reissued**. Redirects are **302**, not 301.
- The redirect hot path does no DB writes; analytics is best-effort and must never block or fail a redirect.
- No raw IPs, full referrers or user agents stored (HMAC visitor hash, referrer host, device class).
- Read options lazily (`IOptions`), never eagerly in `Add*` methods (breaks WebApplicationFactory overrides).
  Sole documented exception: the OTLP exporter switch in `TelemetrySetup` (deployment switch, not test-overridable).
- Timestamps are UTC, truncated to milliseconds (storage precision).
- Schema changes: **append** a new migration to `SqliteSchemaInitializer.Migrations`; never edit a shipped one.
- Time-dependent code takes `TimeProvider`; tests use `ManualTimeProvider`.
- Every bug fix gets a regression test with a comment explaining what went wrong.
- Secrets only via user-secrets or env vars; `appsettings.Development.json` holds dev placeholders only.
- Do not suppress NuGet audit (NU19xx) warnings; upgrade the package instead.
- Admin-only endpoints use `.RequireAdminKey()`; new public lookup endpoints need a rate-limit policy.
- Any API contract change must update `docs/openapi.v1.json` in the same commit.

## Traceability (required by the assignment)

After each meaningful change, add a row to `docs/AI_USAGE_LOG.md` (status: Generated, accepted /
Edited / Considered → rejected / Defect found → fixed, plus rationale). Keep test counts in the docs
current. Use one focused commit per change, with a descriptive message. High-impact changes
(migrations, security rules, auth, rate limits, data model) need the engineer's explicit sign-off;
list them in the log's sign-off table.

## History so far

Commits: Scenario 1 greenfield → Scenario 2 brownfield (analytics) → Scenario 3 ambiguous
(expiry + abuse protection) → docs/CI → fixes found on the first real Windows run:
(1) NU1903, vulnerable SQLitePCLRaw 2.1.11 via Microsoft.Data.Sqlite 10.0.0, bumped to 10.0.15;
(2) .NET 10 `BackgroundService` start/stop race that could drop buffered clicks, fixed in
`ClickFlushService.StopAsync`.

Then a review-driven hardening iteration (2026-11-17, one commit each): metadata endpoint
rate limit + 410 for deleted links; cache/delete race; IPv4-in-IPv6 target blocking; opt-in forwarded
headers; admin throttling + SHA-256 key compare; OpenAPI + snapshot test; metrics + optional OTLP;
click retention + migration 4; load-test tool and baseline (~216k req/s, p99 0.76 ms).

The original code was generated in a sandbox without NuGet access, so the engineer's machine is the
authoritative test environment.

## Open items / possible next steps

See `docs/RISKS.md` §5. Remaining: user accounts / link ownership, homograph flagging, analytics
throughput ceiling (~32k clicks/s on SQLite), visitor-hash key rotation, multi-instance state
(distributed cache/limiter). The engineer still needs to tick the sign-off table in
`docs/AI_USAGE_LOG.md` §5 and fill in the engineer-of-record name.
