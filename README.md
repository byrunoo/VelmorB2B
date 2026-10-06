# Commercial Platform

Multi-tenant, multi-company, multi-portal (B2B sales channel), multi-ERP **B2B Commercial Platform**.
Modular monolith on .NET 10 + PostgreSQL + Redis + Hangfire + transactional outbox, with React/Vite
admin and B2B frontends. No ERP is mandatory; external systems (Kumpara, Logo, Mikro, Nebim, Winner, …)
will be integrated only through connector boundaries in later phases.

**Active phase:** Phase 0 — Foundation (`docs/architecture/active-phase.json`).

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

Host `192.168.1.250`, port `5432`, database `commercial_platform_dev`, user `commercial_platform_app`.
The password is **never** stored in the repository — set it per machine:

```bash
dotnet user-secrets set "Commercial:Postgres:Password" "<password>" --id commercial-platform-dev
```

(or environment variable `COMMERCIAL__POSTGRES__PASSWORD`), then use `pwsh scripts/shared-db.ps1 inspect|migrate|worker|api`.
Local Docker vs Shared vs Testcontainers: see [`docs/architecture/phase-0.md` §2.2–2.3](docs/architecture/phase-0.md).
Tests and CI always use Testcontainers, never the shared database.

Full local CI: `pwsh scripts/ci.ps1` (or `bash scripts/ci.sh`). Details: [`docs/architecture/phase-0.md`](docs/architecture/phase-0.md).
