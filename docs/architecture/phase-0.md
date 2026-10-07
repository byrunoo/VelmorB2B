# Phase 0 — Foundation (implementation record)

**Active phase source:** `docs/architecture/active-phase.json` (Phase 0 record; Phase 1 is active since 2026-10-07)
**Mandate:** `docs/bootstrap/PHASE0_IMPLEMENTATION_MANDATE.md`
**Status:** implemented; acceptance results in the Phase 0 implementation report.

This document describes the repository as it actually is at the end of Phase 0.

---

## 1. What exists

| Area | Implementation |
|---|---|
| Solution | `Commercial.slnx`, `global.json` (.NET 10 SDK, Microsoft.Testing.Platform runner), `Directory.Build.props` (nullable, warnings-as-errors, analyzers), `Directory.Packages.props` (central versions) |
| Domain | `DomainError`, `Result<T>`, `IDomainEvent`, scope identifiers `TenantId`/`CompanyId`/`CustomerAccountId` (identifiers only; `SalesChannelId` removed in Phase 1 WS2, `PrincipalId`/`CustomerBindingId` added) |
| Application | `ExecutionScope` + `IExecutionScopeAccessor` (AsyncLocal), `IUnitOfWork`, `IOutbox`, `IOutboxMessageHandler`, request pipeline (`ICommand`/`IQuery`/handlers/`IRequestValidator`/`IRequestDispatcher`), error exceptions, Phase 0 **diagnostic** use case (outbox ping) |
| Infrastructure | `CommercialDbContext` (schema `commercial`, snake_case, UTC converter, xmin concurrency helper), `NpgsqlDataSource` registration, `IDbSessionInitializer` hook + interceptor (no-op default), `EfUnitOfWork`, outbox (`EfOutbox`, `OutboxStore`, `OutboxDispatcher`, `OutboxSql`), Redis multiplexer + health check, Hangfire PostgreSQL storage/server + storage health check, Serilog + OpenTelemetry, correlation-ID middleware, health endpoints, dev-only startup migration |
| Api | Minimal API host, `/api/v1` group, OpenAPI (`/openapi/v1.json`, enabled in Development), error envelope (`GlobalExceptionHandler`, status-code pages, 401/403 result handler), auth extension point (default never-authenticating scheme; dev-only header scheme), policies `commercial:authenticated|workforce|customer`, execution-scope middleware, endpoints: `GET /api/v1/system/version`, `GET /api/v1/system/me`, `GET /api/v1/system/access-probe/{workforce,customer}`, diagnostics `POST/GET /api/v1/diagnostics/outbox-pings` (Development only) |
| Worker | Hangfire server, recurring `commercial:outbox-dispatch` job (every 5 s, `*/5 * * * * *`), health endpoints |
| Connectors.Abstractions | `ConnectorCapability` (dotted capability id), `DataAuthority` (LOCAL/EXTERNAL/HYBRID). No connector implementation. |
| Frontend | npm workspaces `frontend/{shared,admin,b2b}`; React 19, TS 6, Vite 8, Tailwind 4, TanStack Query/Table, React Hook Form, Zod, react-router 7, lucide-react |
| Tests | Unit, Architecture (A001–A018 + contract boundary), Contract (OpenAPI snapshot + error envelope), Integration (Testcontainers PostgreSQL 17 + Redis 7) |
| CI | `.github/workflows/ci.yml`, local equivalents `scripts/ci.ps1` / `scripts/ci.sh` |

### Explicitly NOT built (Phase 0 scope)

No Tenant/Company/Customer/Catalog/Pricing/Inventory/Quote/Order/Credit/Commercial Account/Invoice/
Shipment/CRM model or screen, no connector (Kumpara, Logo, Mikro, Nebim, Winner, …), no ERP sync,
no payment, no e-Invoice, no RLS policy, no tenant query filter, no message broker.

## 2. Local development

Prerequisites: .NET SDK 10.0.1xx+, Node.js ≥ 22 (24 used), Docker Desktop.

```bash
docker compose up -d                          # PostgreSQL 17 (127.0.0.1:5432, trust auth) + Redis 7 (127.0.0.1:6379)
dotnet restore
dotnet build
dotnet run --project src/Commercial.Worker    # http://localhost:5090 — applies migrations in Development, runs Hangfire
dotnet run --project src/Commercial.Api       # http://localhost:5080 — /openapi/v1.json, /health/live, /health/ready
cd frontend && npm ci
npm run dev:admin                             # http://localhost:5173  (proxies /api and /health to :5080)
npm run dev:b2b                               # http://localhost:5174
```

Developer screen: `/dev/status` in both apps (version, liveness/readiness, outbox round trip).

Dev identity (Development + `Authentication:Development:Enabled=true` only): send
`X-Dev-Principal-Type: WORKFORCE|CUSTOMER` and optionally `X-Dev-Subject`. Never enabled outside Development.
(Phase 1 WS2 removed the former Phase 0 `X-Dev-Tenant-Id` / company / customer-account / sales-channel headers: dev
auth now yields stage-1 identity only and business scope is resolved server-side — see `execution-scope.md`.)

Configuration/secrets: `appsettings.json` ships empty connection strings. Supply real values through
environment variables (`ConnectionStrings__Commercial`, `ConnectionStrings__Redis`) or user-secrets.
OTLP export activates when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

### 2.1 Database connection resolution (single point)

`Commercial.Infrastructure.Persistence.PostgresConnection.Resolve` is the only place PostgreSQL connection strings
are built (runtime DbContext, Hangfire storage, provisioning, `dotnet ef`). Since Phase 1 workstream 1 it resolves
**separate identities** — see [`database-identities.md`](database-identities.md):

1. base: `ConnectionStrings:Commercial`;
2. common overlay: `Commercial:Postgres:Host|Port|Database`;
3. identity overlay: `Commercial:Postgres:Runtime:Username|Password` (API/Worker) or
   `Commercial:Postgres:Migration:Username|Password` (migrations / Hangfire provisioning). No fallback between them;
   maintenance is never application configuration.

Configuration sources, lowest → highest: `appsettings.json` (key names only, empty) → `appsettings.Development.json`
(local docker defaults, no secrets) → user-secrets (Development: runtime store `commercial-platform-dev`; tooling-only
ops store `commercial-platform-ops`) → environment variables → command line.

### 2.2 Environments

| Environment | PostgreSQL | Redis | How it is selected |
|---|---|---|---|
| **Development – Local** | docker-compose `localhost:5432/commercial`, three roles, trust auth (no password) | docker-compose `localhost:6379` | default `Development` launch profile |
| **Development – Shared** | `192.168.1.250:5432/commercial_platform_dev`, runtime `commercial_platform_app`, migrations `commercial_platform_owner` | local docker Redis | `(shared-db)` launch profiles or `scripts/shared-db.ps1` |
| **Test / CI** | Testcontainers PostgreSQL 17 (throw-away), same three roles | Testcontainers Redis 7 | test fixtures; **never** the shared DB |
| **Production** | environment / secret store only | environment / secret store only | no credentials in the repository |

### 2.3 Shared Development PostgreSQL

| Setting | Value |
|---|---|
| Host | `192.168.1.250` |
| Port | `5432` |
| Database | `commercial_platform_dev` |
| Runtime username | `commercial_platform_app` (API/Worker; DML only after the role split) |
| Migration username | `commercial_platform_owner` (created by the DBA role-split script) |
| Passwords | **not in the repository** — user-secrets or environment variables only |

```bash
dotnet user-secrets set "Commercial:Postgres:Runtime:Password"   "<password>" --id commercial-platform-dev
dotnet user-secrets set "Commercial:Postgres:Migration:Password" "<password>" --id commercial-platform-ops
```

(or `COMMERCIAL__POSTGRES__RUNTIME__PASSWORD` / `COMMERCIAL__POSTGRES__MIGRATION__PASSWORD`). Host/database/usernames
come from the shared profile, so the local profile keeps using docker.

```powershell
pwsh scripts/shared-db.ps1 inspect [runtime|migration|dba] # read-only state, ownership and privileges
pwsh scripts/shared-db.ps1 migrations                      # applied / pending EF migrations (owner)
pwsh scripts/shared-db.ps1 migrate                         # dotnet ef database update (owner; idempotent, never drops)
pwsh scripts/shared-db.ps1 provision                       # EF migrations + Hangfire schema (owner)
pwsh scripts/shared-db.ps1 role-split                      # DBA: owner/runtime/maintenance split (see database-identities.md)
pwsh scripts/shared-db.ps1 worker | api                    # runtime identity, no startup migration
pwsh scripts/shared-db.ps1 cleanup <correlationId>         # delete only diagnostic rows with that correlation ID
```

Rules for the shared database: migrations/provisioning run explicitly as the owner (the shared Worker profile sets
`Database:ApplyMigrationsOnStartup=false`); no `DROP`, no manual edits of `__ef_migrations_history`;
Hangfire uses the same database but its own schema `hangfire`, separate from `commercial`; only
diagnostic data may be created, and it is cleaned up by correlation ID.

Test isolation: integration/contract test hosts blank every `Commercial:Postgres:*` override and an
integration guard test (`SharedDatabaseIsolationTests`) proves a shell carrying shared-DB variables still
resolves the Testcontainers database. CI never has the shared password or network route.

## 3. Testing

```bash
dotnet test                                                # all suites (MTP runner; Docker needed for integration)
dotnet test --project tests/Commercial.ArchitectureTests   # fast guardrails, no network
dotnet test --project tests/Commercial.ContractTests       # no database needed
dotnet test --project tests/Commercial.IntegrationTests    # Testcontainers PostgreSQL + Redis
UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --project tests/Commercial.ContractTests   # intentional API change; review diff
pwsh scripts/ci.ps1                                        # full local CI (bash scripts/ci.sh on Linux/macOS)
```

Integration tests create a fresh, migrated database per test, so outbox tests never share rows.

### 3.1 Git / CI

- Branch: `main`. Changes go through pull requests; never push experiments to `main`.
- Local CI (same gates as GitHub): `pwsh scripts/ci.ps1` (`-SkipIntegration`, `-SkipFrontend` available) or `bash scripts/ci.sh`.
- GitHub workflow: `.github/workflows/ci.yml` — jobs `secret-scan` (gitleaks CLI, full history),
  `backend-fast` (restore → format → build → unit → **architecture** → contract → EF pending-model check),
  `integration` (needs `backend-fast`, so architecture failures stop the run before the long suite),
  `frontend` (install → format → lint → typecheck → admin build → b2b build) and **`ci-gate`**, which fails
  unless every job succeeded.
- Branch protection (to configure once the GitHub repository exists): protect `main`, require pull
  requests, require status check **`ci-gate`** (strict/up-to-date), so any failing job blocks merge.
- Testcontainers independence: integration tests start their own PostgreSQL 17 / Redis 7 containers on
  the runner (Docker is available on `ubuntu-latest`); no external service is needed.
- Shared development database is never used by CI: no `COMMERCIAL__POSTGRES__*` variables or secrets are
  defined in the workflow (a guard step fails the job if any are present), the `(shared-db)` launch profiles
  are only used by `dotnet run --launch-profile`, and test hosts blank all shared-DB overrides
  (`SharedDatabaseIsolationTests`).
- Adding the remote (once the repository exists): `git remote add origin <url>` then `git push -u origin main`.

## 4. Migrations

- Location: `src/Commercial.Infrastructure/Persistence/Migrations` (history table `commercial.__ef_migrations_history`).
- Tooling: local tool manifest `.config/dotnet-tools.json` (`dotnet tool restore`).
- Add: `dotnet ef migrations add <Name> -p src/Commercial.Infrastructure -s src/Commercial.Infrastructure -o Persistence/Migrations`
- Apply locally: Worker applies them in Development (`Database:ApplyMigrationsOnStartup=true`), or `pwsh scripts/migrate.ps1`.
- Production: apply explicitly from the release pipeline (`dotnet ef database update` / migration bundle) with a
  migration-capable role. Hosts never migrate outside Development by default.
- CI checks `dotnet ef migrations has-pending-model-changes`.
- Phase 0 migration `InitialFoundation`: `commercial.outbox_messages`, `commercial.diagnostic_ping_receipts`.
  Hangfire creates/owns schema `hangfire` itself.

## 5. Transactional outbox

Flow: `IUnitOfWork.ExecuteInTransactionAsync` → `IOutbox.Enqueue` (same transaction) → commit →
Hangfire recurring job → `OutboxDispatcher` → `IOutboxMessageHandler`(s).

Ownership (`OutboxSql`): claim in a short transaction with `FOR UPDATE SKIP LOCKED`, stamping a
unique per-claim lease token (`LockedBy = <instance>:<guid>`) and `LockedUntilUtc`; process outside the
transaction; `MarkProcessed`/`MarkFailed` only `WHERE locked_by = @lease`; expired leases (`locked_until_utc <= now`)
are reclaimable; `Attempts` increments at claim time (crash-safe poison handling); exponential backoff
(5 s · 2^(n-1), max 15 min); `DeadLettered` after `MaxAttempts` (10). Delivery is at-least-once; handlers
are idempotent (diagnostic handler: `INSERT … ON CONFLICT DO NOTHING` keyed by message id).

## 6. Tenant-isolation readiness

See ADR-0001. Seams: `ExecutionScope`, centralized `NpgsqlDataSource`/`CommercialDbContext`
registration, `IDbSessionInitializer` invoked on every EF connection open (`NoOpDbSessionInitializer` by default),
integration proof `SessionInitializerTests` (pooled connection, Max Pool Size = 1, same backend PID,
re-initialized per scope). Status: **ACCEPTED — RLS-primary hybrid** (2026-10-07; implementation is Phase 1 scope; shared-DB role split plan: `scripts/db/shared-dev-role-split.sql`, not executed). Evidence: `tests/Commercial.IntegrationTests/Spikes/TenantIsolation` (15 Testcontainers tests).

## 7. Frontend design system

The Commercial admin/B2B visual language intentionally matches the Kumpara (BusinessOS) admin panel so the
product family feels consistent. The reference source was analysed read-only; **no code was copied**.

| Concern | Kumpara panel (observed) | Commercial Platform decision |
|---|---|---|
| Framework | React 19, Vite 8, TypeScript 6 | same (baseline) |
| Components | hand-built Tailwind primitives (no component library) | same approach: own primitives in `@commercial/shared` |
| Styling | Tailwind v4 CSS-first `@theme inline`, semantic CSS variables | same; tokens in `shared/src/styles/theme.css` |
| Icons | lucide-react | lucide-react |
| Data | TanStack Query 5, TanStack Table 8 | same |
| Forms/validation | react-hook-form + zod 4 | same (+ `@hookform/resolvers`) |
| Routing | react-router 7 | react-router 7 |
| HTTP | axios | native `fetch` wrapper (single entry point, Zod-validated, correlation ID) |
| State | zustand (persist) | not needed in Phase 0 (React context + TanStack Query) |
| Dates/charts | native `Intl`; no chart lib | none needed in Phase 0 |
| Toasts / i18n | custom provider; DB-driven i18n | deferred |
| Theme | `data-theme` light/dark, navy `#0b1837` brand, amber `#c2660d` accent | same tokens (light + dark), system preference + toggle |
| Type | Hanken Grotesk / Bricolage Grotesque / JetBrains Mono | same |
| Layout | sidebar 264px / 68px rail, header 64px, content max 1400px | same dimensions in `AppShell` |

Admin uses the brand (navy) identity mark; B2B uses the accent (amber) mark to distinguish surfaces.

Boundaries: `admin` and `b2b` never import each other; shared code lives in `@commercial/shared`
(exports `src/index.ts` and `styles.css`). Browser code calls only the Commercial API through
`apiRequest` (relative URLs; dev proxy). Enforced by ESLint and A016/A017.

## 8. KAPI-1 — Kumpara Public API fit-gap (analysis only, no integration)

Source analysed: `BusinessOS.PublicApi` in the reference archive (gitignored under `docs/bootstrap/reference/`).
Secret-like values exist in that project's `appsettings.json`; they were not copied or recorded.

Observed Public API shape: separate host, `/v1/...` URL versioning, API-key auth (`Authorization: Bearer` or
`X-Api-Key`; each key bound to one tenant + one company), scope-based authorization, per-key rate limiting,
RFC 9457 problem details with stable `code`, `{data, meta}` envelopes, cursor pagination, `updatedAfter` deltas,
a `GET /v1/changes` feed, string decimals for money, mandatory `Idempotency-Key` on writes with replay of
the original response, 409 while in progress and release of the key on failure.

| Capability | Status in Public API | Note |
|---|---|---|
| Customer read | PRESENT | contacts list/detail |
| Product read | PRESENT | products, variants |
| Customer-specific pricing (customer+product+qty+currency+date) | **ABSENT** | only flat product sale/purchase price |
| Inventory | PRESENT | stock levels per warehouse/variant, movements |
| Balance | PRESENT | per currency + base total |
| Ledger | PRESENT | contact ledger entries |
| Aging | PRESENT | buckets per contact |
| Invoice metadata | PRESENT | sales invoices |
| Invoice documents (PDF/XML) | PRESENT (via e-document id) | 409 until available; not keyed by invoice id |
| Payments | PRESENT | read + create + allocate |
| Credit limit | PARTIAL | risk limit field only, not enforced |
| Available credit | **ABSENT** | would need derivation |
| Payment terms | PARTIAL | days only |
| Overdue status | PRESENT | open items / aging |
| Customer blocked status | **ABSENT** | only deleted flag |
| External reference lookup | **ABSENT for orders** | contacts/products by tax no / code / barcode only |
| Idempotency | PRESENT | **no retention/TTL policy documented or implemented** for completed keys |
| Safe retry | PRESENT | replay semantics; 10-minute stale reservation |
| Webhooks | ABSENT | scope exists, no endpoints; polling via `changes` |

These gaps are KAPI-1 work items in the Kumpara repository (roadmap §4); see the Deferred Work list in the
Phase 0 report. No Kumpara contract was invented or encoded in Commercial Platform.

## 9. Known limitations / deferred

- Outbox lease is not renewed during long handlers (lease 2 min, batch ≤ 20) — add heartbeat when real handlers need it.
- No inbox table for future external consumers (diagnostic handler uses its own idempotency key).
- Legacy Sales Ops identifier inventory is provisional (`tests/Commercial.ArchitectureTests/Data/legacy-sales-ops-identifiers.json`).
- Frontend bundles are single-chunk (~570 kB); route-level code splitting arrives with real screens.
- Browser telemetry, toasts and i18n are deferred.
