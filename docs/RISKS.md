# Risks, Trade-offs, Failure Scenarios & Limitations

## 1. Failure scenarios and how the system behaves

| Scenario | Behaviour | Guardrail |
|---|---|---|
| Two requests claim the same alias or code at the same instant | Exactly one wins; the other gets 409 (alias) or retries (generated) | DB primary key + `ON CONFLICT DO NOTHING`; concurrency test |
| Code space congested / RNG collision streak | 503 `code_generation_failed` after N attempts, never an infinite loop | `MaxGenerationAttempts` |
| Database unavailable | Create/redirect return 500 problem details; cached links keep redirecting until TTL | Exception handler; health endpoint for orchestration |
| Analytics DB writes failing | Redirects unaffected; batch dropped and logged | Bounded channel + try/catch in consumer |
| Traffic spike beyond analytics throughput | Redirects unaffected; excess click events dropped and counted | `BufferCapacity`, `DroppedCount` |
| Graceful shutdown with clicks buffered | Buffer drained before exit | `StopAsync` completes channel; drain test |
| Crash / kill -9 with clicks buffered | Up to `BufferCapacity` clicks lost (link data is unaffected) | Accepted trade-off (see §3) |
| Missing secrets in production | Process refuses to start | `ValidateOnStart` on options |
| Admin key not configured | Delete/stats return 403 (fail closed) | `ApiKeyEndpointFilter` |
| DB from a newer build | Process refuses to start | Migration runner version check |
| Malformed or oversized request | 400 / 413, never 500 | `BadRequestExceptionHandler`, Kestrel body cap |
| Scripted creation/scanning | 429 with `Retry-After` | Per-IP rate limiting |
| `ForwardedHeaders:Enabled` with no trusted proxy listed | Process refuses to start | Options validation (no "trust everyone" mode) |

## 2. Security review summary

| Threat | Mitigation | Residual risk |
|---|---|---|
| XSS / local file via redirect (`javascript:`, `data:`, `file:`) | http/https allow-list | — |
| Phishing via credentials in URL (`https://bank.com@evil`) | Rejected | — |
| Trusted domain pointing into intranets / cloud metadata | IP-literal + internal-TLD checks on canonicalised host, including IPv4 embedded in IPv6 (compatible, NAT64, 6to4) and special-purpose ranges | **DNS names that resolve to private IPs are not caught** (the service never resolves DNS, by design: avoids SSRF in the shortener itself and DNS-rebinding TOCTOU). |
| Malware/phishing destinations | Operator block list | No reputation feed (Safe Browsing etc.); list is static. |
| Redirect loops / chain obfuscation | Self-host rejected | Other shorteners are not blocked (could add their domains to `BlockedHosts`). |
| Link enumeration | 62⁷ random keyspace; redirect **and** metadata lookups share one per-IP rate limit | Custom aliases are guessable by nature. |
| Taken-down link still advertising its target | Metadata for deleted links returns 410 without `targetUrl` | — |
| Hijacking a deleted code | Codes never reissued | — |
| Brute-forcing the admin key | Per-IP `admin` rate limit (30/min) applied before the key check; SHA-256 digests compared in constant time (no length leak) | Distributed guessing from many IPs; mitigate with a high-entropy key (≥ 32 random bytes) and key rotation. |
| Host-header injection in short URLs | `BaseUrl` from config, not request | — |
| Personal data in analytics | HMAC visitor hash, referrer host only, device class only | Hash key rotation and retention policy not automated. |
| Secrets in source control | Only dev placeholders in `appsettings.Development.json` | Must use user-secrets/env/Key Vault in real environments. |
| Vulnerable dependencies | CI runs `dotnet list package --vulnerable` | — |

## 3. Trade-offs taken deliberately

- **SQLite** gives zero setup and real constraints, but has a single writer and is single-node. Fine
  for a prototype and modest traffic. For horizontal scale, move to Postgres/SQL Server (new adapter;
  Core unchanged).
- **In-process cache and rate limiter.** With N instances: a deleted link can keep redirecting on
  other nodes for up to `CacheTtlSeconds` (default 60 s), and effective rate limits are N×. Fix:
  Redis-backed cache invalidation and limiter, or sticky partitioning at the gateway.
- **Best-effort analytics.** Clicks can be dropped under overload or lost on a hard crash. This
  keeps redirect latency and availability first. For billing-grade counts, use a durable queue
  (Kafka/Azure Service Bus) or the outbox pattern.
- **Raw ADO.NET over EF Core.** Explicit, reviewable SQL and fewer dependencies, at the cost of
  hand-written mapping. Repository contract tests cover the mapping.
- **Fixed-window rate limiting** is simple and predictable but allows a burst of up to 2× at window
  boundaries. A sliding window or token bucket is a drop-in swap.
- **No URL deduplication.** Every create gets a new code. This avoids cross-user information leaks;
  it costs storage.

## 4. Assumptions

- Anonymous creation is acceptable, with abuse controlled by rate limits and destination rules.
- One admin role is sufficient for delete and stats (no per-user ownership yet).
- UTC is the analytics day boundary.
- The service runs directly on Kestrel, or behind a proxy whose addresses are listed in `ForwardedHeaders`.

## 5. Known limitations / next steps

1. ~~**Reverse proxies.**~~ **Done (2026-11-17).** The `ForwardedHeaders` section (off by default)
   trusts `X-Forwarded-For` only from the listed `KnownProxies`/`KnownNetworks`, and startup fails if
   it is enabled with no proxies listed. **Before deploying behind a load balancer, set these to the
   balancer's addresses.** Otherwise every client shares the balancer's rate-limit bucket.
2. **No user accounts or link ownership**, so stats and delete are admin-only.
3. ~~**No OpenAPI document.**~~ **Done (2026-11-17).** `/openapi/v1.json` is served, and the snapshot
   `docs/openapi.v1.json` is checked by a contract test.
4. **No data retention job** for `click_events`. Add a scheduled purge (e.g. older than 13 months).
5. **No metrics export** (OpenTelemetry). `DroppedCount`, cache hit ratio, and redirect latency are
   the first three metrics to add.
6. ~~**Admin brute-force throttling.**~~ **Done (2026-11-17).** See §2.
7. **Unicode lookalike (homograph) domains** are stored as punycode, which makes them visible in the
   API, but they are not flagged.
