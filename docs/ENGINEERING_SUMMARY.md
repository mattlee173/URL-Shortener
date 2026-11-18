# Final Engineering Summary

## Plan and rationale

1. **Normalise the brief** ([REQUIREMENTS.md](REQUIREMENTS.md)). The assignment names capabilities
   ("core APIs, analytics, reliability") but not behaviour. Eleven ambiguities were identified and
   each was resolved with a written, reversible default (redirect status, code format, dedupe,
   deletion semantics, auth, privacy, expiry, storage, URL policy).
2. **Choose an architecture that makes the scenarios cheap and safe** ([ARCHITECTURE.md](ARCHITECTURE.md)):
   ports-and-adapters with a dependency-free Core, so business rules are unit testable and storage
   or analytics can change without touching them.
3. **Execute as three scenarios on one codebase** ([SCENARIOS.md](SCENARIOS.md)), each ending in a
   green build and a commit:
   - *Greenfield:* shorten / redirect / metadata / delete, atomic code allocation, persistence, cache.
   - *Brownfield:* analytics added to the live redirect path after an explicit impact analysis,
     without changing existing contracts or `ShortLinkService`.
   - *Ambiguous:* "expiry and abuse protection" turned into explicit questions and assumptions, then
     implemented: expiry with versioned migrations, destination safety rules, rate limiting, body cap.
4. **Validate continuously**: warnings-as-errors build, unit + contract + integration tests, manual
   exploratory probes, and a regression test for each defect found.
5. **Review, then harden** ([SCENARIOS.md](SCENARIOS.md#iteration-4-review-driven-hardening-brownfield)):
   a fresh AI session verified the build on real infrastructure, reviewed the code against the docs,
   and proposed a prioritised list. The engineer approved it, and each item landed as one focused
   commit with tests and a log row.

## Artifacts

| Artifact | Location |
|---|---|
| Runnable service | `src/UrlShortener.Api` (`dotnet run`) |
| API definition | OpenAPI 3.1 at `/openapi/v1.json`, committed as `docs/openapi.v1.json` and guarded by a contract test; README "API" table; `src/UrlShortener.Api/UrlShortener.Api.http` |
| Schema | `SqliteSchemaInitializer.Migrations` (versioned, forward-only; 4 migrations) |
| Unit tests | `tests/UrlShortener.UnitTests` (170 cases) |
| Integration tests | `tests/UrlShortener.IntegrationTests` (36 cases) |
| Load test | `tools/UrlShortener.LoadTest`; baseline in TESTING.md |
| CI pipeline | `.github/workflows/ci.yml` |
| Docs | `docs/` |
| Git history | One commit per scenario, then one per fix or enhancement |

## Validation summary

- Concurrency correctness: 20-way race on one code yields exactly one winner (SQLite and in-memory).
- Security: 30+ internal-address encodings (including IPv4 embedded in IPv6), credential URLs,
  self-links and block-list matching rejected; allowed boundary cases accepted; constant-time admin
  key check with no length leak; admin endpoints throttled; fail-closed when unconfigured.
- Performance: ~216 k redirects/s at p99 0.76 ms on the engineer's machine (target < 10 ms). The
  analytics ceiling is ~32 k clicks/s with SQLite; above that, clicks are dropped and counted.
- Reliability: analytics overflow, storage failure and graceful-shutdown drain tested; malformed or
  oversized input never yields 500.
- **Four defects** were caught by verification during development and fixed with regression tests:
  malformed JSON returning 500, create/read timestamp mismatch, OS-dependent URL parsing, and
  Unicode hosts causing 500 on redirect.
- **Two more were caught on the engineer's first real run** (see AI_USAGE_LOG.md): a vulnerable
  transitive SQLite package (CVE-2025-6965, fixed by upgrading `Microsoft.Data.Sqlite`), and a
  .NET 10 start/stop race that could drop buffered clicks on a fast shutdown.
- **Four more were found by the post-delivery review** and fixed, each with a regression test shown
  to fail before the fix: rate-limit bypass and deleted-target leak on the metadata endpoint; a cache
  race that kept deleted links redirecting; IPv4 targets hidden in IPv6 literals; and unthrottled
  admin endpoints with a key-length timing leak.

## Key risks and trade-offs

Full detail in [RISKS.md](RISKS.md). The most important:

- **Single-node by design** (SQLite, in-process cache and rate limiter). Scaling out needs a server
  database plus distributed cache and limiter. The adapter seams for that exist.
- **Best-effort analytics.** Clicks may be dropped under overload or on a hard crash, by choice, to
  protect redirect latency.
- **Private-network check covers IP literals and internal TLDs, not DNS names** that resolve
  internally. Resolving DNS would add SSRF and rebinding risk to the shortener itself.
- **Behind a reverse proxy**, `ForwardedHeaders` must list the proxy's addresses before per-IP rate
  limiting and visitor hashing mean anything. It's off by default and fails closed when misconfigured.

## Assumptions

Anonymous creation is acceptable with rate limits; one admin role governs delete and stats; expiry is
optional and absolute; UTC day boundaries for analytics; no URL deduplication.

## Limitations / next steps

User accounts and link ownership; reputation feed for malicious URLs; distributed cache, rate
limiter and database for multi-instance deployments; durable queue for billing-grade click counts;
visitor-hash key rotation; dashboards and alerts on the exported metrics; load tests from a
separate machine over TLS.

## Ownership

AI assistance was used across implementation, debugging, test generation and documentation.
Traceability, the quality gates, and the changes that need human sign-off are listed in
[AI_USAGE_LOG.md](AI_USAGE_LOG.md). The engineer of record owns correctness and production readiness.
