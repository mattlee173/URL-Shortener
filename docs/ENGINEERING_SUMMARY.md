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

## Artifacts

| Artifact | Location |
|---|---|
| Runnable service | `src/UrlShortener.Api` (`dotnet run`) |
| API definition | README "API" table, endpoint summaries in code, `UrlShortener.http` |
| Schema | `SqliteSchemaInitializer.Migrations` (versioned, forward-only) |
| Unit tests | `tests/UrlShortener.UnitTests` (141 cases) |
| Integration tests | `tests/UrlShortener.IntegrationTests` (24 cases) |
| CI pipeline | `.github/workflows/ci.yml` |
| Docs | `docs/` |
| Git history | One commit per scenario |

## Validation summary

- Concurrency correctness: 20-way race on one code yields exactly one winner (SQLite and in-memory).
- Security: 20+ internal-address encodings, credential URLs, self-links and block-list matching
  rejected; allowed boundary cases accepted; constant-time admin key check; fail-closed when
  unconfigured.
- Reliability: analytics overflow, storage failure and graceful-shutdown drain tested; malformed or
  oversized input never yields 500.
- **Four defects** were caught by verification during development and fixed with regression tests:
  malformed JSON returning 500, create/read timestamp mismatch, OS-dependent URL parsing, and
  Unicode hosts causing 500 on redirect.

## Key risks and trade-offs

Full detail in [RISKS.md](RISKS.md). The most important:

- **Single-node by design** (SQLite, in-process cache and rate limiter). Scaling out needs a server
  database plus distributed cache and limiter. The adapter seams for that exist.
- **Best-effort analytics.** Clicks may be dropped under overload or on a hard crash, by choice, to
  protect redirect latency.
- **Private-network check covers IP literals and internal TLDs, not DNS names** that resolve
  internally. Resolving DNS would add SSRF and rebinding risk to the shortener itself.
- **Behind a reverse proxy**, forwarded headers must be configured with trusted proxies before
  per-IP rate limiting and visitor hashing are meaningful.

## Assumptions

Anonymous creation is acceptable with rate limits; one admin role governs delete and stats; expiry is
optional and absolute; UTC day boundaries for analytics; no URL deduplication.

## Limitations / next steps

User accounts and link ownership; OpenAPI document generation; OpenTelemetry metrics (dropped clicks,
cache hit ratio, redirect latency); retention job for click data; reputation feed for malicious
URLs; brute-force throttling on admin endpoints; load testing.

## Ownership

AI assistance was used across implementation, debugging, test generation and documentation.
Traceability, the quality gates, and the changes that need human sign-off are listed in
[AI_USAGE_LOG.md](AI_USAGE_LOG.md). The engineer of record owns correctness and production readiness.
