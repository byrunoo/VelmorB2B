# Commercial Platform

Multi-tenant, multi-company, multi-portal (B2B sales channel), multi-ERP **B2B Commercial Platform**.
Modular monolith on .NET 10 + PostgreSQL + Redis + Hangfire + transactional outbox, with React/Vite
admin and B2B frontends. No ERP is mandatory; external systems (Kumpara, Logo, Mikro, Nebim, Winner, …)
will be integrated only through connector boundaries in later phases.

**Active phase:** Phase 1 — Identity / Tenant / Security (`docs/architecture/active-phase.json`; mandate `docs/phases/phase-1/PHASE1_IMPLEMENTATION_MANDATE.md`). Phase 0 closed with local gates PASS; GitHub-hosted CI deferred by ADR-0002 (not passed).

## Start here

| Document | Purpose |
|---|---|
| [`docs/architecture/frozen-architecture-v1.0.md`](docs/architecture/frozen-architecture-v1.0.md) | Invariants (highest priority) |
| [`CLAUDE.md`](CLAUDE.md) | Working rules for agents and humans |
| [`docs/architecture/active-phase.json`](docs/architecture/active-phase.json) | Active phase |
| [`docs/architecture/phase-0.md`](docs/architecture/phase-0.md) | What Phase 0 built; local dev, testing, migrations, design system, KAPI-1 fit-gap |
| [`docs/architecture/dependency-rules.md`](docs/architecture/dependency-rules.md) | Executable architecture rules A001–A018 |
| [`docs/adr/`](docs/adr/README.md) | Decisions (ADR-0001 tenant isolation gate) |
| [`docs/bootstrap/`](docs/bootstrap) | Original bootstrap package (mandate, test contract, roadmap) |

## Repository layout

```text
src/       Commercial.Domain · Application · Infrastructure · Api · Worker · Connectors.Abstractions
tests/     Commercial.UnitTests · ArchitectureTests · ContractTests · IntegrationTests
frontend/  shared (@commercial/shared) · admin · b2b   (npm workspaces)
docs/      architecture · adr · bootstrap
scripts/   ci.ps1 · ci.sh · migrate.ps1
```

## Quick start

Requires .NET SDK 10.0.1xx+, Node.js ≥ 22, Docker.

```bash
docker compose up -d
dotnet restore
dotnet build
dotnet test
```

Run the hosts (Development):

```bash
dotnet run --project src/Commercial.Worker   # :5090 — migrations (dev), Hangfire, outbox dispatch
dotnet run --project src/Commercial.Api      # :5080 — /api/v1, /openapi/v1.json, /health/live, /health/ready
```

Frontend (from `frontend/`):

```bash
npm ci
npm run dev:admin        # http://localhost:5173
npm run dev:b2b          # http://localhost:5174
npm run lint
npm run typecheck
npm run build
npm run format:check
```

### Shared Development PostgreSQL

Host `192.168.1.250`, port `5432`, database `commercial_platform_dev`.

### Database identities (ADR-0001)

| Credential | Role | Used by | Where the password lives |
|---|---|---|---|
| Runtime | `commercial_platform_app` | API + Worker | `Commercial:Postgres:Runtime:Password` — user-secrets `commercial-platform-dev` / env |
| Migration | `commercial_platform_owner` | `dotnet ef`, `Commercial.Worker --provision-database`, `scripts/shared-db.ps1 migrate/provision` | `Commercial:Postgres:Migration:Password` — user-secrets `commercial-platform-ops` / env |
| Maintenance | `commercial_platform_maintenance` | operations only — never application configuration | `commercial-platform-ops` only |

Passwords are **never** stored in the repository. Local docker uses the same three roles with trust auth on
127.0.0.1 (no passwords). Setup, provisioning and the shared-DB role split:
[`docs/architecture/database-identities.md`](docs/architecture/database-identities.md);
environments: [`docs/architecture/phase-0.md` §2](docs/architecture/phase-0.md).
Tests and CI always use Testcontainers (same three roles), never the shared database.

Full local CI: `pwsh scripts/ci.ps1` (or `bash scripts/ci.sh`). Details: [`docs/architecture/phase-0.md`](docs/architecture/phase-0.md).
