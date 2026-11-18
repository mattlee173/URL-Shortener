# AI-Assisted Engineering Log

**Principle:** AI assists within tasks; the engineer owns execution, correctness and sign-off.
This log is the traceability record: how tasks were framed for the AI, what it generated, what was
changed or rejected (and why), which quality gates ran, and which changes need explicit human
approval.

- **AI assistant:** Claude (Anthropic), used as a pair programmer in an agentic coding session
- **Engineer of record:** Matthew Lee. Reviews, runs, and approves all outputs.

---

## 1. How tasks were framed

Each unit of work was given to the AI with the same four parts, not as an open-ended "build X":

| Part | Example (Scenario 2, T13: non-blocking click recording) |
|---|---|
| **Intent** | Record every successful redirect for analytics. |
| **Constraints** | Must not add latency or a failure mode to `GET /{code}`; no new infrastructure; no raw IPs stored; `ShortLinkService` must not change. |
| **Acceptance criteria** | Redirect does no DB write; buffer is bounded; overflow drops and is counted; graceful shutdown drains; storage failure doesn't stop the consumer; all covered by tests. |
| **Technical context** | .NET 10, existing `IShortLinkRepository` + cache decorator, SQLite single-writer, `BackgroundService` hosting. |

Iterative refinement pattern used throughout: **generate → build (warnings as errors) → run tests →
run the app and probe edge cases by hand → fix → add a regression test → commit.**

---

## 2. Traceability: generated / edited / rejected

### Scenario 1: Greenfield

Status key: **Generated, accepted**: AI output kept as is after review. **Edited**: AI output changed
after a gate or review found a problem. **Considered → rejected**: an alternative weighed during
design and not used. **Defect found → fixed**: a bug in generated code caught by verification.

| Item | Status | Rationale |
|---|---|---|
| Layered solution (Core / Infrastructure / Api), quality-gate `Directory.Build.props` | Generated, accepted | Keeps Core free of dependencies; gates apply to every project. |
| `Error` type name | **Edited** → `DomainError` | Analyzer CA1716: `Error` clashes with a VB keyword. |
| Static `Success`/`Failure` factories on `Result<T>` | **Edited** → implicit conversions only | CA1000 (static members on generic type); implicit conversions read better at call sites. |
| EF Core for persistence | **Considered → rejected** | Too heavy for 2–3 tables; explicit SQL makes the atomic insert reviewable. |
| Check-then-insert for alias uniqueness | **Considered → rejected** | Race condition. Replaced with `INSERT … ON CONFLICT DO NOTHING` plus a concurrency test. |
| 301 permanent redirects | **Considered → rejected** | Browser caching would defeat delete/expiry/analytics. |
| Sequential / hash-based codes | **Considered → rejected** | Enumerable or guessable; hash dedupe leaks cross-user information. |
| Eager options read inside `AddInfrastructure` | **Edited** → lazy `IOptions` | Eager reads ignore `WebApplicationFactory` config overrides (they're applied after `Program` registers services). |
| Malformed-JSON handling | **Defect found → fixed** | Manual probe returned 500 in Development. Added `BadRequestExceptionHandler` (400) and a regression test. |
| `createdAt` precision | **Defect found → fixed** | Create response (ticks) ≠ read-back (ms). Truncated in service; regression test. |
| `/relative/path` error code | **Defect found → fixed** | Linux `Uri` treats it as `file://`, so the error code differed by OS. Explicit rooted-path check. |

### Scenario 2: Brownfield (analytics)

| Item | Status | Rationale |
|---|---|---|
| Impact analysis table (see SCENARIOS.md) | Generated, reviewed | Done before code; drove "don't touch `ShortLinkService`". |
| Synchronous click insert in redirect | **Considered → rejected** | Adds a write + failure mode to the hot path. |
| Bounded channel + `BackgroundService` batch writer | Generated, accepted | No new infra, back-pressure via drop-and-count. |
| `FullMode.DropWrite` | **Considered → rejected**, used `Wait` + `TryWrite` | `DropWrite` reports success while discarding, so drops couldn't be counted. |
| Store raw IP / UA / full referrer | **Considered → rejected** | Data minimisation. HMAC visitor hash, referrer host, device class instead. |
| `ClientKind` serialised as integer | **Edited** → string enum | API readability; `[JsonConverter]` lives in Core with no new dependency. |
| Test-only `CA1861` suppression | Accepted (tests only) | Inline expected arrays are clearer in assertions; still enforced in production code. |

### Scenario 3: Ambiguous (expiry + abuse)

| Item | Status | Rationale |
|---|---|---|
| Clarifying questions + assumptions table | Generated, **requires engineer confirmation** | These are product decisions (see SCENARIOS.md). |
| Default expiry for all links | **Considered → rejected** | Silent behaviour change for existing users. |
| `ALTER TABLE` inside the old `CREATE IF NOT EXISTS` initializer | **Considered → rejected** | Not re-runnable. Replaced with a versioned migration runner (`PRAGMA user_version`). |
| DNS resolution to detect private targets | **Considered → rejected** | Makes the shortener do outbound lookups (SSRF surface) and is open to DNS-rebinding TOCTOU. Documented as residual risk. |
| Unicode host handling | **Defect found → fixed** | Exploratory test: IDN link created fine, then every redirect 500'd (Kestrel rejects non-ASCII headers). Store punycode; regression tests. |
| Rate-limit policy reading options at registration | **Considered → rejected**, per-request `IOptionsMonitor` | Allows test overrides and config reload. |

### Post-delivery: first build on the engineer's machine

| Item | Status | Rationale |
|---|---|---|
| `Microsoft.Data.Sqlite` 10.0.0 | **Defect found → fixed** (bumped to 10.0.15) | Engineer's first real `dotnet restore` failed with NU1903: the package pulls in `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which bundles a SQLite version affected by CVE-2025-6965 / GHSA-2m69-gcr7-jv3q (high). 10.0.15 resolves 2.1.13, outside the affected range. The AI sandbox couldn't reach NuGet's vulnerability feed, so this gate only fired on real infrastructure. It shows why the engineer's run is the authoritative one. Fixed by upgrading rather than suppressing the warning. |
| Click drain on shutdown (`ClickFlushService`) | **Defect found → fixed** | First run against real xUnit on Windows: `StopAsync_DrainsBufferedEvents_BeforeExiting` saved 0 of 25 events. Root cause: since .NET 10, `BackgroundService` starts `ExecuteAsync` via `Task.Run` with the stopping token, so a stop that arrives before the task is scheduled cancels it before it runs, and the drain loop never executes. Reproduced in 44 of 300 stress iterations. Fix: `StopAsync` drains anything still buffered once the consumer is no longer running (single-reader invariant kept). Two regression tests added: a deterministic "never started" case and a 100-iteration start/stop race. 0 of 300 failures after the fix. The original test was correct; it caught a real data-loss bug that only shows up under some scheduling timings. |

### Post-review hardening (Claude Code session, 2026-11-17)

A fresh AI session reviewed the delivered code against the docs before changing anything. The
engineer approved the prioritised list of findings below; each item is one commit.

| Item | Status | Rationale |
|---|---|---|
| Public metadata endpoint `GET /api/links/{code}` | **Defect found → fixed** | Review finding: it was unauthenticated **and** unthrottled, so it answered "does this code exist?" without the redirect's rate limit (enumeration bypass, contradicting RISKS §2), and it returned the target of **deleted** links (a phishing link taken down stayed discoverable). Fix: the endpoint shares the redirect's per-IP budget, and deleted links return 410 `link_deleted` without the target. Expired links still return metadata (expiry is not a takedown). Putting the endpoint behind the admin key was **considered → rejected**: a public "where does this go?" preview is a legitimate anti-phishing feature. Regression tests at unit and integration level. |
| Cache repopulated with a stale row after delete (`CachedShortLinkRepository`) | **Defect found → fixed** | Review finding: a read that fetched the row just before a delete committed could cache it *after* the delete evicted the key, so the deleted link kept redirecting for the full TTL (60 s) on a single instance. Fix: a delete counter; a read that overlaps a delete removes what it just cached (the check runs after `Set` so the check-then-set window is closed too). Per-key versioning and caching a tombstone were **considered → rejected**: more state for a rare event, where the cost of the global counter is one uncached read. Deterministic regression test that interleaves the read and the delete; confirmed it fails without the fix. |
| Private IPv4 addresses hidden inside IPv6 literals (`TargetUrlValidator`) | **Defect found → fixed** | Review finding: only IPv4-mapped (`::ffff:a.b.c.d`) addresses were unwrapped, so IPv4-compatible (`::127.0.0.1`), NAT64 (`64:ff9b::a9fe:a9fe` = the metadata IP) and 6to4 (`2002:7f00:1::`) literals passed, as did Teredo and the documentation and benchmarking ranges. Fix: unwrap each embedded-IPv4 form and check the inner address; block local-use NAT64 and Teredo outright, since their destination can't be known. 12 new reject cases (all failed before the fix) and 3 allow cases (public addresses wrapped the same ways). The residual risk (DNS names that resolve to private IPs) is unchanged and still documented. |
| Forwarded-headers support (`ForwardedHeadersSetup`, RISKS §5.1) | Generated, accepted | Off by default. When enabled it trusts **only** the configured `KnownProxies`/`KnownNetworks`, and startup **fails** if both lists are empty, because ASP.NET Core treats empty lists as "trust any source", which would let clients spoof their IP and pick their own rate-limit bucket. Only `X-Forwarded-For` is honoured; Host/Proto were **considered → rejected** because short URLs come from `Shortener:BaseUrl`, so trusting them adds attack surface for no benefit. Integration tests: a trusted proxy gives each forwarded client its own bucket; an untrusted source can't dodge the limit; disabled ignores the header; a misconfiguration fails startup. |
| Admin brute-force throttling + hashed key comparison (RISKS §5.6) | **Defect found → fixed** | Delete and stats had no rate limit, so the admin key could be guessed at full speed. New `admin` per-IP policy (30/min, configurable), enforced by middleware **before** the key filter runs, so wrong guesses count too. Separately, `FixedTimeEquals` returns early on a length mismatch, leaking the key's length despite the "constant-time" comment, so both sides are now SHA-256 digests before comparing. Account lockout was **considered → rejected**: one shared key means a lockout would let an attacker lock the operator out (denial of service). Tests: throttled before the key check; near-miss keys (prefix, suffix, case) rejected. Timing itself isn't unit-testable; the fix is by construction. |
| OpenAPI 3.1 document (`/openapi/v1.json`, RISKS §5.3) | Generated, **edited** | `Microsoft.AspNetCore.OpenApi` 10.0.15 (matches the runtime; vulnerability scan clean). Every endpoint declares all the status codes it can return; admin operations carry an `ApiKey` security requirement through one `RequireAdminKey()` extension, which now applies the rate limit, filter and metadata together so none can be missed. A **committed snapshot** (`docs/openapi.v1.json`) is compared with the live document by an integration test, so contract changes appear as reviewed diffs. **Edited:** the first generated document marked `customAlias` as required, because positional record parameters without a default are emitted as required, but the API has always treated it as optional; fixed by giving it a `= null` default. Swagger UI was **considered → rejected**: an extra package, and the JSON plus `UrlShortener.http` already cover review and manual testing. |
| Metrics (`ShortenerMetrics`, `TelemetrySetup`, RISKS §5.5) | Generated, accepted | Instruments use the BCL `Meter` API in Infrastructure, so no package lands there: cache hit/miss, clicks dropped / persisted / failed / purged, buffer depth. Redirect latency is **not** duplicated, because ASP.NET Core already emits `http.server.request.duration` by route and status. Export goes through OpenTelemetry 1.21.0 (OTLP) **only** when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; otherwise the SDK isn't registered, and `dotnet-counters` still shows everything. A Prometheus `/metrics` endpoint was **considered → rejected**: that exporter is still prerelease, and the endpoint would need its own auth. **Deliberate exception to the lazy-options rule:** the exporter switch is read at registration (the Aspire pattern), because it is a deployment switch, not behaviour tests override. Unit tests drive the instruments through a `MeterListener` scoped to a private meter. |
| Click retention job (`ClickRetentionService`, migration 4, RISKS §5.4) | Generated, **edited** | Deletes clicks older than `RetentionDays` (395 = 13 months, so year-on-year comparisons still work; 0 disables), sweeping at startup and every 6 h. Deletes run in batches of 5 000 so the click flusher, SQLite's only other writer, never waits long. **Migration 4** adds an index on `occurred_at_ms`: the existing `(code, time)` index can't serve a time-only filter, so without it every sweep would scan the whole table; a test checks the query plan uses it after upgrading a v3 database. Startup validation rejects `RetentionDays` shorter than `StatsWindowDays`. Deleting by `id` range was **considered → rejected**: batched clicks can land slightly out of time order, so ids aren't strictly time-ordered. **Edited:** the new repository contract test caught an off-by-N bug in the AI-written in-memory implementation (the batch counter kept counting past `maxCount`) before it was committed; SQLite was correct. This shows why contract suites run against every store. |
| Load test harness (`tools/UrlShortener.LoadTest`) and baseline | Generated, accepted | k6 wasn't installed, and NBomber needs a commercial licence for business use, so both were **considered → rejected** in favour of a ~150-line dependency-free console tool (closed-loop workers, exact percentiles, non-zero exit on a breached budget). Run against a live Release build: **~216 k redirects/s, p99 0.76 ms, 0 failures**, meeting the 10 ms target. The run also measured the analytics ceiling: about **32 k clicks/s** reach SQLite, and the excess is dropped and counted as designed. That is a capacity limit to document, not a defect (TESTING.md). Also smoke-tested the app with `OTEL_EXPORTER_OTLP_ENDPOINT` set and no collector: it starts, redirects and serves OpenAPI normally. |
| Documentation sync after the hardening iteration | Generated, reviewed | Test counts (206), the "verified on real packages" status, README configuration and API tables, architecture diagram and decisions, the new SCENARIOS "Iteration 4" section, ENGINEERING_SUMMARY, and the remaining RISKS §5 items. The engineer-of-record name and the §5 sign-off ticks are deliberately **left for the engineer**: sign-off has to be the human's act, not the AI's. |
| OpenAPI snapshot test could not find the repo (`OpenApiDocumentTests`) | **Defect found → fixed** | Found while re-verifying after the authorship rewrite: building with `--artifacts-path` (output outside the repo) made the test throw "Could not locate the repository root", because it walked up from the build output folder. It locates the repo from its own source path (`[CallerFilePath]`) instead, so it works however the build output is laid out. This was a defect in the AI-written test, not in the product. |
| Commit authorship (pre-publication) | Edited, at the engineer's direction | The AI session had committed under the identity "Claude (AI pair)", so the history didn't show the engineer of record. Before the first push, the engineer had all 18 commits rewritten (author and committer = Matthew Lee) with every file unchanged, verified by identical tree hashes, and each commit's `Co-Authored-By: Claude` trailer kept as the per-commit AI attribution. The README now states how AI was used. Leaving the AI identity as author was **considered → rejected**: it misrepresents who owns and approved the work, which the assignment stresses. Commit IDs quoted in SCENARIOS.md were updated. |
| Repository structure tidy-up | Generated, accepted (engineer-requested) | The engineer flagged loose files during review. Startup wiring moved to `Api/Configuration/`; error handling (`ResultMapping`, `BadRequestExceptionHandler`) to `Api/Errors/`; `SecurityOptions` and `IStorageInitializer` split out of the files that hid them; `StorageOptions` moved under `Persistence/`. The REST Client file moved next to the API it exercises (`src/UrlShortener.Api/UrlShortener.Api.http`, the `dotnet new webapi` convention), and `CLAUDE.md` moved to `.claude/` (still read by Claude Code). Namespaces follow folders. Moves used `git mv` so history follows the files. Behaviour is unchanged: all tests pass and the OpenAPI snapshot is identical. Collapsing the root config files or the `docs/` set was **considered → rejected**: tools require the root files where they are, and each doc maps to a graded deliverable. |
| SQLite files kept in `data/` | Generated, accepted (engineer-requested) | The database and its WAL companion files (`.db`, `-wal`, `-shm`) were written into the API project folder. The default connection string is now `Data Source=data/urlshortener.db`, and `SqliteConnectionFactory.EnsureDatabaseDirectoryExists()` (called by the schema initializer) creates the folder, because SQLite creates files but not folders. In-memory and `file:` URIs are left alone. Covered by a unit test and checked with a real `dotnet run`. The files stay git-ignored. |
| `ChannelClickRecorder.DroppedCount` removed | Edited | Leftover from before the metrics work: an in-process counter recording the same thing as the `urlshortener.clicks.dropped` metric, read by only one test. Removed so there's a single source of truth; that test now checks the drop behaviour, and the existing metrics test checks the count. |

---

## 3. Quality gates applied to every AI output

1. **Compile with warnings as errors**: nullable analysis, .NET analyzers at `latest-recommended`,
   code style. Several AI suggestions were rejected at this gate (CA1716, CA1000, CA1861, CS8602,
   CS8629).
2. **Automated tests**: 171 unit and 36 integration test cases at the time of writing, plus an
   OpenAPI contract snapshot.
3. **Manual exploratory verification** of behaviour tests can't easily show: real Kestrel limits
   (413), `Retry-After` on 429, legacy-database upgrade, SIGINT drain of buffered clicks, IDN
   redirect, startup with the OTLP exporter enabled.
4. **Performance**: the redirect load test (`tools/UrlShortener.LoadTest`) with a p99 budget; see
   TESTING.md for the baseline.
5. **Security review** of every input path (see [RISKS.md](RISKS.md) §2).
6. **CI** (`.github/workflows/ci.yml`): build, test, vulnerable-package scan on every push/PR.

### Verification environment note

The AI's sandbox could not reach nuget.org. During generation, the build and test runs used
temporary local stand-ins for `Microsoft.Data.Sqlite`, xUnit and `WebApplicationFactory`, with the
same API surface, backed by the native SQLite library and a real Kestrel host. They are **not** part
of this repository. **The engineer must run `dotnet test` against the real packages** (see README)
before treating the suite as verified. That run is the authoritative quality gate.

**Status (2026-11-17): verified.** On the engineer's Windows 11 machine with the real NuGet packages
(.NET SDK 10, ASP.NET Core runtime 10.0.15): `dotnet build` (Debug and a forced Release rebuild) gives
0 warnings, `dotnet test` passes all tests, and `dotnet list package --vulnerable --include-transitive`
is clean. The first such run found the two post-delivery defects above. Every later change in this
log was built and fully tested on this machine before it was committed.

---

## 4. Secure AI usage

- No secrets, credentials or proprietary code were given to the AI. All keys in the repo are clearly
  marked dev placeholders.
- AI output is treated as untrusted until it passes the gates above; no generated dependency was
  added without justification (the solution's runtime packages are Microsoft.Data.Sqlite, Microsoft.AspNetCore.OpenApi and the two
  OpenTelemetry packages, each justified in the table above).
- Dependencies are pinned to exact versions; CI flags known-vulnerable packages.

---

## 5. Human sign-off required (high-impact changes)

These changes affect security, data durability or public contracts, and need explicit engineer
approval before merging or deploying:

| Change | Why high-impact | Sign-off |
|---|---|---|
| Schema migrations 1–3 (`SqliteSchemaInitializer`) | Irreversible on production data | ☐ |
| Schema migration 4 (`ix_click_events_time`) | Builds an index on a possibly large table at startup (one-off write lock) | ☐ |
| Click retention (395 days default; purges data permanently) | Irreversible deletion of analytics data; defines "total clicks" as "within retention" | ☐ |
| Destination safety rules (`TargetUrlValidator`), incl. 2026-11-17 embedded-IPv4 / special-purpose additions | Security boundary; false positives block legitimate users | ☐ |
| Admin API-key filter (fail-closed behaviour, SHA-256 + constant-time compare, admin rate limit) | AuthN for destructive operations | ☐ |
| Rate-limit values (20 create / 300 redirect+metadata / 30 admin per IP per min) | Can lock out real users behind NAT | ☐ |
| Analytics data model (what personal data is and isn't stored) | Privacy / compliance | ☐ |
| Scenario 3 product assumptions (no default expiry, 410 semantics, 365-day max) | Product decisions made without a product owner | ☐ |
| Metadata endpoint: deleted links → 410, shares redirect rate limit | Public API contract and security rule change | ☐ |
| Forwarded headers (trusted-proxy list, fail-closed validation) | Decides which client IP rate limits and analytics see; a wrong proxy list allows IP spoofing | ☐ |
