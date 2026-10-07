# CLAUDE.md — Commercial Platform

Instructions for coding agents (and humans) working in this repository.

## 1. Read first, in this order

1. `docs/architecture/frozen-architecture-v1.0.md` — highest-priority source; invariants.
2. this file.
3. `docs/architecture/active-phase.json` — the ONLY source of the active phase.
4. the mandate it references (Phase 1: `docs/phases/phase-1/PHASE1_IMPLEMENTATION_MANDATE.md`).
5. `docs/bootstrap/ARCHITECTURE_TEST_CONTRACT.md` and `docs/architecture/dependency-rules.md`.
6. `docs/adr/ADR-0001-tenant-isolation-decision-gate.md` (ACCEPTED) and
   `docs/adr/ADR-0002-phase0-hosted-ci-temporary-waiver.md` (ACCEPTED — temporary waiver).

The original bootstrap package (mandate, test contract, roadmap, start-here, sample rules) is kept
verbatim under `docs/bootstrap/`.

## 2. Active phase

**Phase 1 — Identity / Tenant / Security** (active since 2026-10-07). Only the identity/tenant/security
foundation of the Phase 1 mandate: Tenant, Company, principals, roles/permissions/grants, minimal customer
security binding, security audit, ExecutionScope v2, PostgreSQL RLS (ADR-0001), DB role separation.
Do NOT implement Customer commercial data / CustomerAccount entity, Catalog/Product, Pricing, Inventory, Quote,
Order, Credit, Commercial Account, Invoice, Shipment, CRM/Lead/Opportunity, payment gateway, e-Invoice, B2B
Portal / Sales Channel or ERP-connection modules, any connector (Kumpara, Logo, Mikro, Nebim, Winner, …), ERP
sync, mobile app. A018 enforces this from `tests/Commercial.ArchitectureTests/PhaseScope/phase-scope.json`.

Phase 0 is closed with local gates PASS; GitHub-hosted CI is **NOT PASS** and deferred by ADR-0002 (D1–D7 required
before commercial release) — never report it as passed.

Phase changes happen only in a dedicated human-approved kickoff PR (active-phase.json + mandate + A018
scope data, nothing else).

## 3. Hard boundaries

- Kumpara/BusinessOS is external: never its DB, tables, internal assemblies or DbContext. Future access is
  only `Commercial.Connectors.Kumpara → BusinessOS.PublicApi /v1`. The BusinessOS zip under
  `docs/bootstrap/reference/` is gitignored reference material — never copy its code or any secret from it.
- Legacy Sales Ops is not a code/schema/domain source.
- No provider-specific logic or provider-name branching in Domain/Application. Use exact provider tokens
  (never ban plain `Logo`/`SAP`).
- No ERP is mandatory. Portal ↔ connection is not 1:1 (capability + authority + policy).
- Modular monolith; async = PostgreSQL outbox + Hangfire. No RabbitMQ/Kafka/microservices/K8s assumptions.
- Tenant isolation is decided (ADR-0001 ACCEPTED, RLS-primary hybrid): never add EF global query filters for visibility,
  never enable `No Reset On Close`, never make the runtime role a table owner. Implementation is Phase 1 scope.
- Browser apps call only the Commercial API; admin and b2b never import each other (shared code → `frontend/shared`).
- Never commit secrets; dev Postgres uses trust auth on 127.0.0.1 so no password exists in the repo.
- Database identities (ADR-0001): runtime = `commercial_platform_app` (DML only), migrations/provisioning =
  `commercial_platform_owner`, maintenance never in app config. Never run migrations with runtime credentials and
  never let API/Worker connect as owner/maintenance. See `docs/architecture/database-identities.md`.

## 4. Never weaken guardrails to make a build pass

Architecture tests are merge blockers. Do not add ignore lists, loosen tokens or skip tests. If a rule
seems wrong, raise it (ADR process) instead of editing it. Never report a test as passing that you did
not run; never report partial work as done.

## 5. Commands

```bash
docker compose up -d                       # PostgreSQL + Redis (local dev)
dotnet restore && dotnet build             # warnings are errors
dotnet test --project tests/Commercial.ArchitectureTests   # fast guardrails
dotnet test                                 # all suites (integration needs Docker for Testcontainers)
dotnet run --project src/Commercial.Worker  # Development: provisions as owner at startup, then runs as app (Hangfire + outbox)
dotnet run --project src/Commercial.Worker -- --provision-database   # explicit owner-identity provisioning (EF + Hangfire schema)
dotnet run --project src/Commercial.Api     # http://localhost:5080  (OpenAPI: /openapi/v1.json)
cd frontend && npm ci && npm run lint && npm run typecheck && npm run build
```

Full CI locally: `pwsh scripts/ci.ps1` (or `bash scripts/ci.sh`).

## 6. Conventions

- C#: file-scoped namespaces, nullable enabled, `TreatWarningsAsErrors`, central package versions in
  `Directory.Packages.props` (add versions there, never in csproj).
- Persistence: single `CommercialDbContext`, schema `commercial`, snake_case, `DateTimeOffset`/UTC only,
  migrations in `src/Commercial.Infrastructure/Persistence/Migrations`.
- Use cases: `ICommand`/`IQuery` + handlers + `IRequestValidator` in Application, dispatched through
  `IRequestDispatcher`; endpoints only translate HTTP.
- Errors: throw `ValidationFailedException` / `ConflictException` / `NotFoundException` /
  `ForbiddenAccessException`; never return raw exception messages.
- Async side effects: `IOutbox.Enqueue` inside `IUnitOfWork.ExecuteInTransactionAsync`; handlers implement
  `IOutboxMessageHandler` and MUST be idempotent.
- OpenAPI changes: update `tests/Commercial.ContractTests/Snapshots/openapi-v1.json` deliberately
  (`UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --project tests/Commercial.ContractTests`) and review the diff.
