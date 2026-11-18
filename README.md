# URL Shortener: AI-Assisted Engineering Prototype

A production-style URL shortener in **C# / .NET 10 (ASP.NET Core Minimal APIs)** with short-link
creation (random or custom alias), redirects, link expiry, privacy-preserving click analytics, abuse
protection, and unit + integration test suites.

It was built as three sequential scenarios on one codebase (**greenfield → brownfield → ambiguous**),
each a separate commit, to show requirement analysis, task decomposition, AI-assisted execution and
validation. A review-driven hardening iteration followed (one commit per finding; see
[SCENARIOS.md](docs/SCENARIOS.md#iteration-4-review-driven-hardening-brownfield)).

| Start here | |
|---|---|
| [docs/ENGINEERING_SUMMARY.md](docs/ENGINEERING_SUMMARY.md) | Final summary: plan, rationale, artifacts, risks, assumptions, limitations |
| [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md) | Requirement interpretation, ambiguities, task decomposition |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Components, control flow, key decisions |
| [docs/SCENARIOS.md](docs/SCENARIOS.md) | Greenfield / brownfield / ambiguous: decomposition, execution, validation |
| [docs/TESTING.md](docs/TESTING.md) | Testing approach and quality gates |
| [docs/RISKS.md](docs/RISKS.md) | Failure modes, security review, trade-offs, limitations |
| [docs/AI_USAGE_LOG.md](docs/AI_USAGE_LOG.md) | AI traceability: generated / edited / rejected, sign-off checklist |

### AI-assisted development

As the assignment requires, this project was built by **Matthew Lee** with **Claude (Anthropic)** as an
AI pair programmer. I framed each task (intent, constraints, acceptance criteria, technical context),
reviewed and ran every output on my own machine, decided what was accepted, edited or rejected, and
approved each commit. Claude assisted with implementation, debugging, test generation, code review
and documentation, and each AI-assisted commit carries a `Co-Authored-By: Claude` trailer.

The full record of what was generated, edited, rejected or found defective (with the reason for
each), the quality gates every output passed, and the high-impact changes that required my sign-off
is in [docs/AI_USAGE_LOG.md](docs/AI_USAGE_LOG.md).

---

## Quick start

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`dotnet --version` ≥ 10.0.100)
- VS Code with the **C# Dev Kit** extension (for running and debugging tests)
- Optional: the **REST Client** extension (`humao.rest-client`) to use `UrlShortener.http`

### Build, test, run

```bash
dotnet restore
dotnet build            # warnings are errors
dotnet test             # 170 unit + 36 integration
dotnet run --project src/UrlShortener.Api
```

The API listens on **http://localhost:5260** and creates `urlshortener.db` (SQLite) in the API
project's folder on first run. Schema migrations apply automatically. The OpenAPI 3.1 description
is at **http://localhost:5260/openapi/v1.json** (committed copy: [docs/openapi.v1.json](docs/openapi.v1.json)).

### Try it

Open `UrlShortener.http` in VS Code and click **Send Request** on each block, or use curl:

```bash
# Create
curl -s -X POST http://localhost:5260/api/links \
  -H "Content-Type: application/json" \
  -d '{"url":"https://learn.microsoft.com/dotnet/","customAlias":"dotnet"}'

# Follow (shows the 302)
curl -i http://localhost:5260/dotnet

# Stats (admin key from appsettings.Development.json)
curl -s http://localhost:5260/api/links/dotnet/stats -H "X-Api-Key: dev-only-admin-key-change-me"
```

---

## API

All errors are RFC 9457 `application/problem+json`. Domain errors carry a stable `errorCode`.

| Method | Route | Auth | Success | Errors |
|---|---|---|---|---|
| `POST` | `/api/links` body `{ url, customAlias?, expiresAt? }` | none (rate-limited) | `201` + `Location` | `400` validation, `409 alias_taken`, `413`, `429`, `503` |
| `GET` | `/api/links/{code}` | none (shares the redirect rate limit) | `200` link metadata | `404`, `410 link_deleted` (target withheld), `429` |
| `GET` | `/api/links/{code}/stats` | `X-Api-Key` (admin rate limit) | `200` totals, uniques, by day, top referrers, by device | `401`, `403`, `404`, `429` |
| `DELETE` | `/api/links/{code}` | `X-Api-Key` (admin rate limit) | `204` (soft delete; code never reissued) | `401`, `403`, `404`, `429` |
| `GET` | `/{code}` | none (rate-limited) | `302` to target | `404`, `410 link_deleted` / `link_expired`, `429` |
| `GET` | `/health` | none | `200 Healthy` | |
| `GET` | `/openapi/v1.json` | none | `200` OpenAPI 3.1 document | |

**Validation error codes:** `url_required`, `url_invalid`, `url_too_long`, `url_scheme_not_allowed`,
`url_credentials_not_allowed`, `url_host_not_allowed`, `url_self_reference`, `url_host_blocked`,
`alias_length`, `alias_invalid_characters`, `alias_reserved`, `expiry_in_past`, `expiry_too_far`.

---

## Configuration

Set in `appsettings*.json`, environment variables (`Section__Key`), or user-secrets.

| Key | Default | Notes |
|---|---|---|
| `Shortener:BaseUrl` | `http://localhost:5260` | Public base for generated short URLs (never taken from the Host header) |
| `Shortener:CodeLength` | `7` | 4–32 Base62 characters |
| `Shortener:MaxExpiryDays` | `365` | Upper bound for `expiresAt` |
| `Shortener:BlockedHosts` | `[]` | Domains (and their subdomains) that may not be shortened |
| `Storage:Provider` | `Sqlite` | `Sqlite` or `InMemory` |
| `Storage:ConnectionString` | `Data Source=urlshortener.db` | |
| `Storage:CacheTtlSeconds` | `60` | `0` disables the redirect cache |
| `Analytics:VisitorHashKey` | _(required)_ | **Secret.** Keys the visitor hash |
| `Analytics:BufferCapacity` / `BatchSize` / `FlushIntervalMilliseconds` | `10000` / `500` / `1000` | Click pipeline tuning |
| `Analytics:RetentionDays` / `RetentionSweepIntervalMinutes` | `395` / `360` | Clicks older than this are purged; `0` keeps them forever |
| `RateLimiting:CreatePermitsPerMinute` / `RedirectPermitsPerMinute` / `AdminPermitsPerMinute` | `20` / `300` / `30` | Per client IP. The redirect budget also covers metadata lookups |
| `ForwardedHeaders:Enabled` / `KnownProxies` / `KnownNetworks` | `false` / `[]` / `[]` | Enable behind a load balancer **and list its addresses**; startup fails if enabled with none |
| `Security:AdminApiKey` | _(none)_ | **Secret.** If unset, delete/stats return 403 (fail closed). Use ≥ 32 random bytes |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | _(unset)_ | Standard OpenTelemetry variable; when set, metrics are exported over OTLP |

Development placeholders for both secrets are in `appsettings.Development.json`. For any other
environment:

```bash
cd src/UrlShortener.Api
dotnet user-secrets set "Security:AdminApiKey" "<random>"
dotnet user-secrets set "Analytics:VisitorHashKey" "<random>"
```

The app refuses to start if required settings are missing or invalid.

### Observability

Metrics are published on the `UrlShortener` meter (cache hits/misses, clicks dropped, persisted,
failed and purged, buffer depth), next to ASP.NET Core's built-in `http.server.request.duration`.
Watch them locally with `dotnet-counters monitor -n UrlShortener.Api --counters UrlShortener`, or set
`OTEL_EXPORTER_OTLP_ENDPOINT` to export them to a collector.

---

## Project layout

```
src/
  UrlShortener.Core/            Domain, rules, use cases, ports. No dependencies.
  UrlShortener.Infrastructure/  SQLite + in-memory adapters, cache, analytics pipeline, migrations
  UrlShortener.Api/             Minimal API endpoints, auth filter, rate limiting, error mapping
tests/
  UrlShortener.UnitTests/         Rules, services, contract suites (in-memory + SQLite)
  UrlShortener.IntegrationTests/  Full HTTP pipeline via WebApplicationFactory, OpenAPI contract snapshot
tools/
  UrlShortener.LoadTest/        Redirect load generator (see docs/TESTING.md)
docs/                           Requirements, architecture, scenarios, testing, risks, AI log, openapi.v1.json
UrlShortener.http               Ready-made requests for VS Code REST Client
.github/workflows/ci.yml        Build, test, vulnerable-package scan
```
