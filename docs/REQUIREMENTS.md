# Requirements Analysis & Task Decomposition

The assignment states the goal ("a URL shortener service with core APIs, analytics, and reliability
features") but not the behaviour. This document turns that into an engineering problem: what is
explicit, what is ambiguous, which interpretation was chosen, and how the work was sequenced.

## 1. What the brief states explicitly

| # | Requirement (from brief) | Interpretation |
|---|---|---|
| R1 | Core APIs | Create a short link, follow it (redirect), read link metadata, delete a link |
| R2 | Analytics | Record each redirect; expose per-link stats |
| R3 | Reliability features | Durable storage, input validation, safe failure modes, health check, tests |
| R4 | Production-quality code, API definitions, unit/integration tests, docs | Layered solution, warnings-as-errors, xUnit unit + integration suites, docs/ |
| R5 | Three scenarios: greenfield, brownfield, ambiguous | Built as three sequential git commits on one codebase (see [SCENARIOS.md](SCENARIOS.md)) |

## 2. Ambiguities identified and decisions taken

Each ambiguity below would normally be raised with a product owner. Because the brief is fixed, a
default was chosen, written down, and kept easy to change.

| Ambiguity | Options considered | Decision & rationale |
|---|---|---|
| Who may create links? | Anonymous / API key / user accounts | **Anonymous create**, rate-limited per client IP (Scenario 3). Accounts are out of scope for a prototype. |
| Who may delete links / view stats? | Anyone / link owner / admin | **Admin API key** (`X-Api-Key`). Owner tokens need accounts. Fails closed: with no key configured, management endpoints return 403. |
| Redirect status code | 301 / 302 / 307 / 308 | **302**. 301/308 are cached by browsers indefinitely, so deletion/expiry would stop working and repeat visits would go uncounted. |
| Code format | Sequential IDs / hash of URL / random | **7-char random Base62 from a CSPRNG**. Sequential and hash codes are guessable or enumerable. 62⁷ ≈ 3.5×10¹² keyspace. |
| Same URL shortened twice | Return existing code / new code | **New code each time**. Deduplication would leak whether someone else shortened a URL and couple unrelated users' analytics. |
| Custom aliases | Not supported / supported | **Supported** (3–32 chars, `[A-Za-z0-9_-]`, reserved words blocked, case-sensitive). |
| Can a deleted code be reused? | Yes / no | **Never.** Soft delete keeps the row so an old printed link can't be hijacked to a new destination. |
| What does "analytics" mean? | Counters only / full event log / external pipeline | **Per-click events** (time, referrer host, user-agent family, hashed visitor id) plus an aggregated stats endpoint. Raw IPs are **not** stored (privacy). |
| Expiry | None / TTL / absolute date | **Optional absolute `expiresAt`**, must be in the future and ≤ a configured maximum. Expired → 410 Gone. |
| Storage | In-memory / SQLite / SQL Server / Redis | **SQLite** behind an `IShortLinkRepository` interface: zero setup for reviewers, real SQL constraints. Swappable for SQL Server/Postgres. |
| Which URLs are allowed? | Anything / http(s) only / + safety checks | **http(s) only**, length-limited, no embedded credentials, no private/loopback hosts, no links back to the shortener itself (Scenario 3). |

## 3. Non-functional targets (assumed)

- **Redirect latency**: the redirect path does one cached lookup; analytics recording must not add a
  database write to it (Scenario 2).
- **Correctness under concurrency**: two simultaneous requests can never receive the same code
  (enforced by the database primary key, not check-then-insert).
- **Security**: OWASP-relevant risks covered: open-redirect abuse, XSS via `javascript:` URLs,
  SSRF-style internal targets, credential leakage, brute-force/abuse via rate limits, timing-safe key
  comparison, no secrets in source control.
- **Operability**: health endpoint, structured logs, RFC 9457 problem-details errors, options
  validated at startup (fail fast on misconfiguration).

## 4. Task decomposition

Tasks are ordered by dependency; each scenario ends in a green build and a commit.

```
Scenario 1 – Greenfield (core)
  T1  Solution skeleton, quality gates (nullable, analyzers, warnings-as-errors)
  T2  Domain model + Result/DomainError types                 (needs T1)
  T3  Validation: target URL, alias rules                      (needs T2)
  T4  Code generator (CSPRNG Base62)                           (needs T2)
  T5  ShortLinkService: create / resolve / get / delete        (needs T3, T4)
  T6  Repository interface + SQLite + in-memory implementations (needs T2)
  T7  Read-through cache decorator                             (needs T6)
  T8  Minimal API endpoints + problem-details mapping + API-key filter (needs T5, T7)
  T9  Unit tests (validators, generator, service, repository contract, cache)
  T10 Integration tests (WebApplicationFactory + temp SQLite)

Scenario 2 – Brownfield (analytics on the existing redirect path)
  T11 Impact analysis of existing modules (see SCENARIOS.md)
  T12 Click event model + repository (schema change: new table + index)
  T13 Non-blocking recording: bounded Channel + background writer   (needs T12)
  T14 Hook into redirect endpoint without changing its contract     (needs T13)
  T15 Stats query + GET /api/links/{code}/stats                     (needs T12)
  T16 Tests: aggregation, queue overflow, end-to-end click counting

Scenario 3 – Ambiguous ("make links expire and stop abuse")
  T17 Clarify & record interpretation (see SCENARIOS.md)
  T18 Expiry: domain + schema migration + validation + 410 handling
  T19 Destination safety rules (private hosts, credentials, self-links)
  T20 Rate limiting (create vs redirect policies) with 429 problem details
  T21 Tests for each rule; regression run of Scenario 1 & 2 suites

Final
  T22 Architecture, risks, AI-usage log, README; full verification run
```
