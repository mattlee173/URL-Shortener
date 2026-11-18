# AI-Assisted Engineering Log

**Principle:** AI assists within tasks; the engineer owns execution, correctness and sign-off.
This log is the traceability record: how tasks were framed for the AI, what it generated, what was
changed or rejected (and why), which quality gates ran, and which changes need explicit human
approval.

- **AI assistant:** Claude (Anthropic), used as a pair programmer in an agentic coding session
- **Engineer of record:** _<your name>_. Reviews, runs, and approves all outputs.

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

---

## 3. Quality gates applied to every AI output

1. **Compile with warnings as errors**: nullable analysis, .NET analyzers at `latest-recommended`,
   code style. Several AI suggestions were rejected at this gate (CA1716, CA1000, CA1861, CS8602,
   CS8629).
2. **Automated tests**: 141 unit and 24 integration test cases at the time of writing.
3. **Manual exploratory verification** of behaviour tests can't easily show: real Kestrel limits
   (413), `Retry-After` on 429, legacy-database upgrade, SIGINT drain of buffered clicks, IDN
   redirect.
4. **Security review** of every input path (see [RISKS.md](RISKS.md) §2).
5. **CI** (`.github/workflows/ci.yml`): build, test, vulnerable-package scan on every push/PR.

### Verification environment note

The AI's sandbox could not reach nuget.org. During generation, the build and test runs used
temporary local stand-ins for `Microsoft.Data.Sqlite`, xUnit and `WebApplicationFactory`, with the
same API surface, backed by the native SQLite library and a real Kestrel host. They are **not** part
of this repository. **The engineer must run `dotnet test` against the real packages** (see README)
before treating the suite as verified. That run is the authoritative quality gate.

---

## 4. Secure AI usage

- No secrets, credentials or proprietary code were given to the AI. All keys in the repo are clearly
  marked dev placeholders.
- AI output is treated as untrusted until it passes the gates above; no generated dependency was
  added without justification (the solution has **one** third-party runtime package).
- Dependencies are pinned to exact versions; CI flags known-vulnerable packages.

---

## 5. Human sign-off required (high-impact changes)

These changes affect security, data durability or public contracts, and need explicit engineer
approval before merging or deploying:

| Change | Why high-impact | Sign-off |
|---|---|---|
| Schema migrations 1–3 (`SqliteSchemaInitializer`) | Irreversible on production data | ☐ |
| Destination safety rules (`TargetUrlValidator`) | Security boundary; false positives block legitimate users | ☐ |
| Admin API-key filter (fail-closed behaviour) | AuthN for destructive operations | ☐ |
| Rate-limit values (20 create / 300 redirect per IP per min) | Can lock out real users behind NAT | ☐ |
| Analytics data model (what personal data is and isn't stored) | Privacy / compliance | ☐ |
| Scenario 3 product assumptions (no default expiry, 410 semantics, 365-day max) | Product decisions made without a product owner | ☐ |
