# Commercial Platform — Phase 0 Implementation Mandate

**Status:** APPROVED FOR IMPLEMENTATION  
**Package revision:** 3  
**Phase:** 0 — Foundation  
**Architecture Source:** `docs/architecture/frozen-architecture-v1.0.md`  
**Active Phase Source:** `docs/architecture/active-phase.json`  
**Target:** New repository / new codebase  
**Primary Goal:** Build the executable architectural foundation and guardrails without implementing Phase 1+ commercial features.

---

## 1. Phase 0 objective

Phase 0 is not a business-feature phase.

At the end of Phase 0 the repository must be:

- buildable,
- testable,
- runnable locally,
- CI-verifiable,
- protected by executable architecture boundaries,
- equipped with error/observability/runtime foundations,
- ready for a controlled Phase 1 kickoff.

Real Customer, Product, Quote and Order workflows are not required and must not be implemented.

---

## 2. Mandatory documents and source order

The repository must contain and Claude Code must read:

1. `docs/architecture/frozen-architecture-v1.0.md`
2. root `CLAUDE.md`
3. `docs/architecture/active-phase.json`
4. this mandate
5. `ARCHITECTURE_TEST_CONTRACT.md`

The Frozen Architecture is the highest-priority source.

---

## 3. Locked product decisions

### 3.1 Product boundary

Commercial Platform is a new independent product.

The Internal Sales Operations UI in this repository is not the previous Sales Ops application.

Legacy Sales Ops is not a source for code, schema or domain design.

### 3.2 Kumpara boundary

Kumpara is an external system.

Commercial Platform may not:

- connect directly to the Kumpara database,
- read/write Kumpara tables,
- reference BusinessOS internal assemblies/entities,
- share a DbContext,
- bypass the public API.

Future integration uses `BusinessOS.PublicApi /v1`.

No Kumpara connector business implementation is built in Phase 0.

### 3.3 ERP independence

No ERP/provider is mandatory for the product to run.

### 3.4 Architecture style

Modular monolith.

Do not introduce:

- microservices,
- RabbitMQ,
- Kafka,
- service mesh,
- Kubernetes-specific assumptions,
- distributed saga frameworks.

---

## 4. Frozen technology baseline

### Backend

- .NET 10 LTS
- ASP.NET Core
- EF Core
- PostgreSQL
- Redis
- Hangfire
- Transactional Outbox
- REST + OpenAPI
- Serilog
- OpenTelemetry
- xUnit
- Testcontainers

`NetArchTest.Rules` or an equivalent .NET architecture-test library may be used.

### Frontend

- React
- TypeScript
- Vite
- TanStack Query
- TanStack Table
- React Hook Form
- Zod

### Async baseline

`PostgreSQL + Transactional Outbox + Hangfire`

RabbitMQ is explicitly not part of Phase 0.

---

## 5. Target repository shape

```text
commercial-platform/
├─ CLAUDE.md
├─ README.md
├─ PHASE0_IMPLEMENTATION_MANDATE.md
├─ ARCHITECTURE_TEST_CONTRACT.md
├─ Directory.Build.props
├─ Directory.Packages.props
├─ global.json
├─ docker-compose.yml
├─ .editorconfig
├─ .gitignore
│
├─ docs/
│  ├─ architecture/
│  │  ├─ frozen-architecture-v1.0.md
│  │  ├─ active-phase.json
│  │  ├─ phase-0.md
│  │  └─ dependency-rules.md
│  └─ adr/
│     ├─ README.md
│     └─ ADR-0001-tenant-isolation-decision-gate.md
│
├─ src/
│  ├─ Commercial.Domain/
│  ├─ Commercial.Application/
│  ├─ Commercial.Infrastructure/
│  ├─ Commercial.Api/
│  ├─ Commercial.Worker/
│  └─ Commercial.Connectors.Abstractions/
│
├─ frontend/
│  ├─ admin/
│  ├─ b2b/
│  └─ shared/
│
├─ tests/
│  ├─ Commercial.UnitTests/
│  ├─ Commercial.IntegrationTests/
│  ├─ Commercial.ArchitectureTests/
│  └─ Commercial.ContractTests/
│
└─ scripts/
```

`Commercial.Connectors.Kumpara` is not required in Phase 0.

---

## 6. Assembly responsibilities

### Commercial.Domain

Allowed:

- domain-safe primitives,
- domain error primitives,
- domain-event abstraction if needed,
- strongly typed ID foundation if justified.

Forbidden:

- EF Core,
- ASP.NET Core,
- Hangfire,
- Redis,
- HTTP infrastructure,
- provider-specific code,
- logging implementations,
- cloud SDKs.

### Commercial.Application

Allowed:

- use-case orchestration foundation,
- ports/interfaces,
- validation pipeline foundation,
- current-user/current-scope abstractions,
- transaction abstractions.

Forbidden:

- DbContext implementation,
- provider-specific branching,
- HTTP endpoint implementations,
- Infrastructure implementation references.

### Commercial.Infrastructure

Expected:

- EF Core DbContext,
- PostgreSQL registration/configuration,
- Redis implementation,
- Hangfire persistence/registration,
- transactional outbox persistence and dispatcher,
- DB-session initialization hook,
- Serilog/OpenTelemetry wiring.

### Commercial.Api

Expected:

- HTTP host,
- OpenAPI,
- authn/authz extension points,
- global error handling,
- correlation ID,
- health/version endpoints,
- versioning foundation.

No business logic in controllers/endpoints.

### Commercial.Worker

Expected:

- Hangfire server,
- outbox dispatcher execution,
- future job-host foundation.

No provider jobs in Phase 0.

### Commercial.Connectors.Abstractions

May contain only provider-independent connector primitives/capability descriptors.

Do not create a large speculative SDK in Phase 0.

---

## 7. Provider-token rules

Architecture tests must not ban generic branding words.

Forbidden provider tokens must use exact/known tokens or namespace/type conventions such as:

```text
Kumpara
BusinessOS
LogoTiger
LogoGo
LogoIsbasi
LogoErp
MikroErp
Netsis
Parasut
Nebim
SapBusinessOne
```

Do not blacklist plain `Logo` or plain `SAP` by substring.

Legitimate names such as `CompanyLogo`, `LogoUrl` or `Sapling` must not fail architecture tests.

---

## 8. Frontend foundation

`frontend/admin` and `frontend/b2b` are separate Vite apps.

Phase 0 deliverables:

- shared TypeScript configuration,
- lint/formatting,
- explicit shared workspace/package boundary,
- API-client foundation,
- TanStack Query provider,
- error boundary,
- environment configuration,
- authenticated-shell placeholder / dev shell,
- simple health/version developer page.

Do not implement Customer, Catalog, Quote, Order, Invoice or Account screens.

---

## 9. Persistence foundation

PostgreSQL is the primary relational database.

Required:

- centralized `DbContext` registration,
- migrations path,
- consistent naming convention,
- UTC timestamp policy,
- concurrency foundation,
- transaction abstraction,
- DB health check.

Do not create Phase 1+ commercial business tables.

Foundation tables are allowed where required for runtime infrastructure, e.g. outbox/inbox and Hangfire persistence.

---

## 10. Tenant isolation decision gate

Phase 0 must **not** accidentally decide the production tenant persistence-isolation model.

Before Phase 1 kickoff, `docs/adr/ADR-0001-tenant-isolation-decision-gate.md` must be completed and approved.

Candidate strategies include:

- EF Core global query filters + explicit execution scope, or
- PostgreSQL RLS + per-connection/session context.

### Required Phase 0 seams

Phase 0 must provide:

1. `CurrentExecutionScope`/equivalent abstraction capable of carrying future `TenantId`, `CompanyId`, `CustomerAccountId` and principal context.
2. Centralized DbContext/connection registration.
3. A database session initialization hook invoked when a database connection becomes usable.
4. Default no-op implementation in Phase 0.
5. Test seam proving the hook can execute with pooled/Testcontainers connections.

Do not implement real RLS policies or global tenant query filters in Phase 0 unless a separately approved ADR changes the timing.

---

## 11. Transactional Outbox foundation

Phase 0 must prove the following flow without any external ERP call:

```text
Application transaction
        ↓
PostgreSQL transaction
        ↓
Outbox row
        ↓
Commit
        ↓
Hangfire-triggered dispatcher
        ↓
Test/no-op idempotent handler
```

### Minimum outbox fields

- `Id`
- `OccurredAtUtc`
- `Type`
- `Payload`
- `CorrelationId`
- `CausationId` nullable
- `TenantId` nullable until Phase 1 contract is finalized
- `Attempts`
- `NextAttemptAtUtc`
- `ProcessedAtUtc`
- `LastError`
- `Status`
- `LockedBy` nullable
- `LockedUntilUtc` nullable

### Multi-worker ownership requirement

Two Worker instances must not process the same live outbox row concurrently.

Baseline claim mechanism:

1. start a short DB transaction,
2. select claimable rows using PostgreSQL `FOR UPDATE SKIP LOCKED`,
3. set `LockedBy` and `LockedUntilUtc`,
4. commit claim transaction,
5. process outside the claim transaction,
6. mark success/failure only if the current worker still owns the lease,
7. allow expired leases to be reclaimed.

Equivalent proven semantics are acceptable, but "Hangfire should only run it once" is not sufficient.

### Handler idempotency

The Phase 0 test/no-op handler must be written as an idempotent handler and integration tests must demonstrate duplicate delivery does not create duplicate side effects in its test sink.

---

## 12. Error contract

Commercial API must expose a stable error envelope.

Minimum:

```json
{
  "type": "...",
  "title": "...",
  "status": 400,
  "code": "VALIDATION_ERROR",
  "detail": "...",
  "correlationId": "...",
  "errors": {}
}
```

Requirements:

- no client-visible stack traces,
- correlation ID in response,
- validation vs conflict distinction,
- 401 vs 403 distinction,
- no production leakage of raw internal exception messages.

---

## 13. Observability foundation

All hosts must support:

- structured logging,
- correlation ID,
- trace ID,
- OpenTelemetry tracing,
- metrics foundation,
- liveness/readiness separation.

Minimum health dependencies:

- PostgreSQL,
- Redis,
- Hangfire storage.

---

## 14. Security foundation

Phase 0 is not the full identity phase.

Prepare:

- authentication-scheme extension points,
- `WORKFORCE` / `CUSTOMER` principal-type claim contract,
- current execution-scope abstraction,
- authorization-policy registration foundation,
- secrets/configuration separation,
- no-secrets-in-repository policy.

Development identity, if used, must be unmistakably dev-only.

---

## 15. Contract tests — exact Phase 0 scope

`tests/Commercial.ContractTests` must exist and run, but **must not invent a fake Kumpara, Logo or ERP contract**.

Phase 0 contract-test contents are limited to Commercial Platform's own HTTP contract:

1. **OpenAPI snapshot test**
   - generate the Commercial API OpenAPI document,
   - compare against a reviewed snapshot or compatibility baseline,
   - require explicit snapshot update for intentional contract changes.

2. **Error-envelope schema test**
   - verify validation/conflict/unhandled-error responses conform to the standard error shape,
   - verify `code`, `status`, `correlationId` and error details semantics.

3. Optional lightweight health/version endpoint contract test.

External connector contract tests start in their integration phase, not Phase 0.

---

## 16. Local development environment

`docker-compose.yml` must start at least:

- PostgreSQL,
- Redis.

Hangfire may run inside Worker.

Target flow:

```bash
docker compose up -d
dotnet restore
dotnet build
dotnet test
```

Frontend commands must be documented at repo root.

---

## 17. CI pipeline

Minimum stages:

1. restore
2. format/lint validation
3. backend build
4. unit tests
5. architecture tests
6. contract tests
7. integration tests
8. frontend install
9. frontend lint
10. frontend typecheck
11. frontend build

Architecture tests should fast-fail before longer integration suites.

Integration tests must use Testcontainers and not depend on manually installed developer services.

---

## 18. Phase-aware scope guard

Phase scope is driven by:

`docs/architecture/active-phase.json`

The architecture test suite must read this file.

When `phase == 0`, A018 forbids Phase 1+ domain implementations.

When a human-approved kickoff PR changes the active phase, A018 must apply the allowed/forbidden scope for the newly activated phase rather than permanently banning Phase 0-forbidden modules.

### Phase transition exemption

The dedicated kickoff PR may update:

- `active-phase.json`,
- the new phase mandate,
- phase-specific A018 scope data/tests,

without requiring a new ADR **solely because the active-phase scope changed**.

This exemption does not allow weakening Frozen Architecture invariants.

---

## 19. Phase 0 — explicitly forbidden implementation

When active phase is 0, do not implement:

- Customer business module/workflows,
- Product/Catalog business module,
- Pricing engine,
- Inventory business logic,
- Quote,
- SalesOrder,
- Credit/Risk,
- Commercial Account Ledger business logic,
- InvoiceReference business logic,
- Shipment,
- Rental,
- Leads/CRM,
- Opportunities,
- Field Visits,
- Route planning,
- Targets,
- Commissions,
- Kumpara connector implementation,
- Logo/Mikro/Netsis connector implementation,
- external ERP synchronization,
- e-Invoice/e-Archive,
- payment gateway,
- RabbitMQ,
- microservices,
- Kubernetes platform,
- native mobile app.

If a foundation concern appears to require one of these, create only the minimum abstraction allowed by this mandate and report the deferred work.

---

## 20. Documentation deliverables

Repository must contain:

- root `README.md`
- root `CLAUDE.md`
- `docs/architecture/frozen-architecture-v1.0.md`
- `docs/architecture/active-phase.json`
- `docs/architecture/phase-0.md`
- `docs/architecture/dependency-rules.md`
- `docs/adr/README.md`
- `docs/adr/ADR-0001-tenant-isolation-decision-gate.md`
- local-development instructions
- testing instructions
- migration instructions

Docs must reflect the real repository state.

---

## 21. Phase 0 acceptance gate

Phase 0 is DONE only when all required gates pass.

### Build

- [ ] `dotnet build` succeeds under the repository warning policy
- [ ] Admin frontend build succeeds
- [ ] B2B frontend build succeeds

### Tests

- [ ] Unit test project runs
- [ ] Architecture test project runs
- [ ] Contract test project runs with **only the Phase 0 contract scope defined above**
- [ ] Integration tests run through Testcontainers
- [ ] At least one architecture rule has been deliberately broken once and proven to fail CI/tests, then restored

### Runtime

- [ ] PostgreSQL starts locally
- [ ] Redis starts locally
- [ ] API starts
- [ ] Worker starts
- [ ] Hangfire persistence works
- [ ] Outbox can be created and dispatched
- [ ] Multi-worker claim test proves one active owner per row
- [ ] Expired lease recovery works
- [ ] Idempotent test handler tolerates duplicate delivery

### API/Observability

- [ ] OpenAPI document generated
- [ ] OpenAPI contract snapshot test passes
- [ ] Standard error-envelope contract tests pass
- [ ] Correlation ID works
- [ ] OpenTelemetry wiring works
- [ ] Liveness/readiness work

### Architecture

- [ ] No Kumpara internal dependency
- [ ] No Legacy Sales Ops dependency
- [ ] No provider leakage in core
- [ ] Exact provider-token rules do not flag `CompanyLogo`/`LogoUrl`
- [ ] API cannot directly use DbContext for business mutations
- [ ] Frontend cross-app import rules enforced
- [ ] A018 reads `active-phase.json`

### Tenant-isolation readiness

- [ ] Current execution-scope abstraction exists
- [ ] DB-session initializer hook exists and is no-op by default
- [ ] Integration test proves session initializer invocation through the real DbContext/pooled connection path
- [ ] `ADR-0001` remains visibly `REQUIRED BEFORE PHASE 1` unless already decided by human review

### CI

- [ ] Architecture-test failure blocks merge
- [ ] Contract tests run in CI
- [ ] Integration tests run in CI
- [ ] Frontend lint/typecheck/build run in CI

If one required item fails, report Phase 0 as **FAIL / INCOMPLETE**, not PASS.

---

## 22. Completion report — single required format

The completion report format is normative here. `CLAUDE_CODE_START_HERE.md` must mirror this list and may not define a different report shape.

Return a `PHASE 0 IMPLEMENTATION REPORT` containing exactly these headings, in this order:

1. Active Phase
2. Implemented
3. Repository Structure
4. Architecture Rules Enforced
5. Deliberate Failure Rule Proven
6. Tests Run / Results
7. Outbox Multi-worker Test Result
8. Contract Tests Result
9. Tenant Isolation Readiness / ADR-0001 Status
10. Runtime Validation
11. CI Validation
12. New Dependencies
13. Migrations
14. Deviations
15. Deferred Work
16. Final Gate: PASS / FAIL

If any required gate is missing or failing, the final gate must be **FAIL / INCOMPLETE**. Partial completion may not be reported as PASS.

---

## 23. Definition of Phase 0 success

Phase 0 is successful when the repository has a boring, executable, provider-independent foundation with enforceable boundaries and no premature commercial feature implementation.
