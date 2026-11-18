# The Three Scenarios

All three were built on one codebase, as three sequential commits, so each later scenario is a real
change to existing code rather than a separate toy:

```
git log --oneline
16f96c8 Scenario 3 (ambiguous): link expiry and abuse protection
9961f7c Scenario 2 (brownfield): click analytics on the existing redirect path
d1e033f Scenario 1 (greenfield): core URL shortener
```

`git show <sha> --stat` gives the exact footprint of each.

---

## Scenario 1: Greenfield: core shortener

**Requirement:** "Build a URL shortener service from scratch with core APIs."

**Normalised problem:** a service that turns a long http(s) URL into a short, unguessable code;
redirects the code to the URL; lets an operator read and delete links; and never hands the same code
to two destinations, even under concurrency.

**Decomposition:** tasks T1–T10 in [REQUIREMENTS.md](REQUIREMENTS.md#4-task-decomposition).

**Execution highlights**
- Ports-and-adapters layout chosen first (T1), so Core has no framework dependencies and every rule
  is unit testable.
- Code allocation is a single atomic `INSERT … ON CONFLICT DO NOTHING`. Collisions retry with a fresh
  random code up to a configured limit, then return 503 rather than looping forever.
- Two defects were found by running the app and its tests during this scenario, and fixed before the
  commit:
  1. Malformed JSON returned **500** in Development (ASP.NET rethrows `BadHttpRequestException`).
     Fixed with an `IExceptionHandler` that maps it to 400 problem details.
  2. `createdAt` in the create response had tick precision but millisecond precision when read back,
     so the same link reported two timestamps. Fixed by truncating to storage precision in the
     service, with a regression test.
- A third finding: on Linux, `Uri` parses `/relative/path` as an absolute `file://` URI, so the
  error code for the same input differed by OS. The validator now rejects rooted paths explicitly.

**Validation:** validator, generator and service unit tests; a repository **contract suite** run
against both in-memory and SQLite (including a 20-way concurrent insert race); cache tests; and
integration tests over the real HTTP pipeline (201 + Location, 302, 404, 409, 400 problem details,
401 without key, 204 then 410).

---

## Scenario 2: Brownfield: add click analytics

**Requirement:** "The service needs analytics" — added to the working Scenario 1 code.

### Impact analysis (done before any code change)

| Area | Impact | Decision |
|---|---|---|
| `GET /{code}` redirect endpoint | **Hot path.** Must record a click without adding latency or a new failure mode. | Hand off to a non-blocking `IClickRecorder`. No DB write on this path. |
| `ShortLinkService` | Could grow a "record click" method, but that couples link lifecycle to analytics. | **Unchanged.** New `LinkAnalyticsService` alongside it (single responsibility). |
| SQLite schema | New table, plus an index for per-code time-range queries. | Additive only (`click_events`), so existing data is unaffected. |
| Cache decorator | Cache hits skip the DB, so counting can't sit in the repository. | Record at the endpoint, after a successful resolve. |
| DI / hosting | Needs a background consumer and graceful-shutdown behaviour. | `ClickFlushService : BackgroundService`, which drains on stop. |
| API contract | New read endpoint. Existing endpoints' contracts must not change. | `GET /api/links/{code}/stats`, behind the admin key. Redirect response unchanged. |
| Privacy | Clicks carry IPs, user agents and referrers (personal data). | Store an HMAC visitor hash, the referrer **host** only, and a device class only. |

**Data flow after the change:** redirect → `ClickEventFactory` (minimise data) → bounded channel
(capacity 10k, drop + count when full) → `ClickFlushService` (batch ≤ 500 or ≤ 1 s, one transaction)
→ `click_events` → aggregate queries → stats endpoint.

**Validation**
- Unit: UA classification and referrer reduction (theories); the hash is stable per visitor and
  keyed (a different key gives a different hash, and the raw IP never appears); buffer overflow drops
  instead of blocking; **drain-on-stop** persists events queued before shutdown; a failed batch is
  logged and the consumer keeps running; aggregation contract tested on both stores; stats window
  starts at UTC midnight.
- Integration: three redirects with different UAs and referrers show up correctly in stats; failed
  (410) redirects are **not** counted; 401 without key; 404 for unknown code.
- Manual: killed the app with SIGINT straight after two clicks. Both were persisted (drain verified
  end to end).
- **Regression:** the full Scenario 1 suite ran unchanged and stayed green.

---

## Scenario 3: Ambiguous: "links should expire, and protect the service from abuse"

The brief deliberately leaves this open. Questions a product owner would be asked, and the assumption
made for each:

| Question | Assumption taken | Why |
|---|---|---|
| Expire after a fixed TTL, or at a date the caller picks? | Caller-supplied **absolute** `expiresAt`, optional. | Covers both cases: a client wanting a TTL sends now + TTL. |
| Do links expire by default? | **No.** Omitted means never expires. | Changing existing behaviour silently would break current users (brownfield safety). |
| Upper bound? | `MaxExpiryDays` (365, configurable). | Stops absurd dates, keeps the option open to purge later. |
| What does an expired link return? | **410 Gone** with `errorCode: link_expired`. | Tells clients the link existed but is no longer valid (different from 404). |
| Which kinds of abuse? | (a) volume (scripted mass creation or scanning), (b) dangerous destinations, (c) oversized payloads. | The common URL-shortener abuse cases. |
| Rate-limit by what key? | Client IP, separate budgets for create (20/min) and redirect (300/min). | No accounts exist; creation is the abuse vector, so it gets the tighter budget. |
| What counts as a dangerous destination? | Embedded credentials, loopback/private/link-local/metadata IPs (in any spelling), internal TLDs, links back to the shortener, operator block list. | Stops the trusted short domain being used to reach intranets or cloud metadata, to disguise phishing, or to build redirect loops. |
| Malware/phishing reputation lookups? | **Out of scope**, with an extension point. | Needs an external service (e.g. Google Safe Browsing). The block list is the seam for it. |

**Decomposition:** T17–T21 in [REQUIREMENTS.md](REQUIREMENTS.md#4-task-decomposition).

**Execution highlights**
- Expiry needed a schema change on databases that **already exist**. A plain
  `CREATE TABLE IF NOT EXISTS` approach can't add a column, so a versioned migration runner
  (`PRAGMA user_version`) was introduced. It upgrades unversioned Scenario 1/2 databases in place
  (tested with a hand-built legacy database) and refuses to start against a schema from a newer build.
- Expiry is evaluated at read time against `TimeProvider`, so cached links expire exactly.
- **Defect found during exploratory testing:** shortening `https://bücher.example/` succeeded, but
  every redirect for that link returned **500**, because `Uri.AbsoluteUri` keeps Unicode hosts and
  Kestrel rejects non-ASCII header values. Hosts are now stored as punycode. Regression tests at unit
  and integration level.
- Rate-limit policies read options per request, so tests (and config reloads) can change limits
  without rebuilding the app.

**Validation:** 20 internal-address spellings rejected (decimal/hex IPv4, IPv4-mapped IPv6, ULA,
link-local, `.local`, `.internal`, the metadata IP); boundary addresses just outside private ranges
allowed; credentials, self-links and block-list subdomain matching; expiry boundary (valid at
T-1 min, Gone at exactly T); UTC normalisation; migration upgrade, idempotence and newer-schema
refusal; end-to-end 410 after real expiry, 429 with `Retry-After` and problem details, and 413 for a
20 KB body (verified manually against Kestrel). The full Scenario 1 and 2 suites stayed green.

---

## Iteration 4: Review-driven hardening (brownfield)

**Trigger:** the engineer's first run on real infrastructure (two defects, see AI_USAGE_LOG.md), then
a fresh AI session asked to "get up to speed, verify, and report, then stop". It verified the build
and tests, compared the code with the docs, and reported findings and a prioritised plan. **No code
changed until the engineer approved the list.**

**Decomposition** (ordered by risk; each item is one commit with tests and a log row):

| # | Task | Kind | Depends on |
|---|---|---|---|
| H1 | Metadata endpoint: shared rate limit, 410 for deleted links | Bug fix (security) | — |
| H2 | Cache/delete race | Bug fix (correctness) | — |
| H3 | IPv4 hidden in IPv6 literals | Bug fix (security) | — |
| H4 | Forwarded headers with a trusted-proxy list | Enhancement (deployability) | — |
| H5 | Admin throttling + hashed key compare | Bug fix (security) | rate limiter (H1 pattern) |
| H6 | OpenAPI document + snapshot test | Enhancement (contract) | H1, H5 (final status codes) |
| H7 | Metrics + optional OTLP export | Enhancement (operability) | — |
| H8 | Click retention job + migration 4 | Enhancement (privacy, data growth) | H7 (purge metric) |
| H9 | Load test harness + baseline | Validation | all of the above |

**Impact analysis highlights**
- H1 changes a public contract (deleted links: 200 → 410), so it was flagged for sign-off. The redirect
  contract is unchanged.
- H2 is confined to the cache decorator; the repository interface is unchanged.
- H8 adds a schema migration (appended, never edited) and a second writer on SQLite. Bounded batches
  keep the click flusher from waiting.
- H7 touches the constructors of the cache, recorder and flusher through optional parameters, so
  existing tests and call sites still compile unchanged.

**Validation:** every bug fix has a regression test, and each was confirmed to **fail on the old
code** before the fix was applied (for H2, a deterministic interleaving of a read and a delete; for
H3, 12 of 12 new cases failed). The full suite stays green after each commit (167 → 206 tests). The
new repository contract test caught a defect in the AI's own in-memory purge implementation before
it was committed. The load test confirmed the redirect latency target and measured the analytics
throughput ceiling.
