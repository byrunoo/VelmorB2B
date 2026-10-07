# Commercial Platform — Frozen Architecture v1.0

**Status:** FROZEN
**Normative priority:** 1 (highest). Then: accepted ADRs → active phase mandate → Architecture Test Contract → Roadmap.
**Consolidated from (bootstrap package, `docs/bootstrap/`):** `PHASE0_IMPLEMENTATION_MANDATE.md` (rev 3),
`ARCHITECTURE_TEST_CONTRACT.md` (rev 3), `COMMERCIAL_PLATFORM_ROADMAP_v1.1.md`, `CLAUDE_CODE_START_HERE.md`,
plus the product owner's approved cardinality decisions (2026-10-06).

This document did not exist in the bootstrap package; it was assembled from the invariants those
documents already state. It introduces **no new or relaxed invariant**. Where wording differs between
sources, the stricter reading is kept. Changing anything here requires the process in §12.

---

## 1. Product boundary

1. Commercial Platform is a **new, independent product** (multi-tenant B2B commercial platform).
2. **Legacy Sales Ops** is not a source for code, schema or domain design. Its commercial core
   (Customer / Quote / Order / Payment) is not grown further; future Lead/Opportunity/Field Visit/
   Route/Target/Commission capabilities move onto Commercial Platform as bounded contexts.
3. **Kumpara / BusinessOS** is an **external system**. Commercial Platform never connects to the Kumpara
   database, never reads/writes Kumpara tables, never references BusinessOS internal assemblies,
   never shares a DbContext, never calls Kumpara internal services/repositories. The only allowed path:

   ```text
   Commercial Platform → Commercial.Connectors.Kumpara → BusinessOS.PublicApi /v1 → Kumpara
   ```

   BusinessOS source code is reference material only (Public API fit-gap, UI technology). It is not a
   source of truth for the Commercial domain model, schema or business logic.
4. **No ERP is mandatory.** The platform must run end-to-end without any external system
   (ERP-free operation; ERP-free finance is not legal accounting; e-Invoice/e-Archive generation is out of V1 scope).
5. **Rental (Kiralama)** is out of V1 scope (stays in KiralamaTakip).

## 2. Tenancy and cardinality (approved)

```text
Tenant
├─ Company (1..n)
│   ├─ External-system connection (0..n)   e.g. Logo, Kumpara, Mikro, Nebim, Winner …
│   └─ Sales channel / B2B portal (0..n)   e.g. "Dealers", "Corporate customers", "Export"
├─ Sales channel / B2B portal (0..n)       (tenant-level channels are allowed)
└─ External-system connection (0..n)
```

Invariants:

- **Not** `1 Tenant = 1 ERP = 1 B2B`.
- **Portal ↔ external connection is not 1:1.** One connection may serve many portals; one portal may
  source different capabilities from different providers (e.g. product/inventory from one, balance/
  invoice from another, order write to a third, pricing local).
- Long-term **source selection is capability + authority + policy based** (`LOCAL` / `EXTERNAL` / `HYBRID`
  authority). No model may hard-wire a single provider per tenant, company or portal.
- No tenant-specific (e.g. pilot customer) behaviour is hard-coded; tenant configuration and
  capability/policy mechanisms are used.
- Server-side security context carries `TenantId`, `CompanyId`, `CustomerAccountId`, `PrincipalType`
  (`WORKFORCE` | `CUSTOMER`) and permissions; a B2B principal can never access another customer
  account's data (Phase 1 gate).
- *Clarification (human decision 2026-10-07, no invariant relaxed):* one human is one global Principal that MAY hold
  several customer memberships (CustomerPrincipalBindings) across companies **and tenants**; every request runs under
  exactly one active membership, and the isolation rule above applies to that active membership. Roles are scoped to
  the membership. Details: `docs/architecture/identity-and-customer-membership.md`.

Phase 0 implements only the identifiers and the execution-scope carrier; the models are Phase 1+.

## 3. Architecture style

- **Modular monolith.** No microservices, service mesh, Kubernetes-specific assumptions, distributed
  saga frameworks.
- **V1 async baseline:** `PostgreSQL + Transactional Outbox + Hangfire`. RabbitMQ/Kafka are not part of
  V1; they may be added only on measured need (multiple independent consumers, high fan-out,
  cross-runtime delivery, throughput beyond outbox/Hangfire capacity, service decomposition) via ADR.

## 4. Technology baseline

| Area | Frozen choice |
|---|---|
| Runtime | .NET 10 LTS, ASP.NET Core |
| Persistence | EF Core, PostgreSQL (primary relational store) |
| Cache | Redis |
| Background jobs | Hangfire (PostgreSQL storage) |
| Async integration | Transactional Outbox |
| HTTP contract | REST + OpenAPI |
| Logging / telemetry | Serilog, OpenTelemetry |
| Tests | xUnit, Testcontainers, NetArchTest (or equivalent) |
| Frontend | React, TypeScript, Vite, TanStack Query, TanStack Table, React Hook Form, Zod |

## 5. Assemblies and dependency direction

```text
Commercial.Api ─────┐                    Commercial.Worker ──┐
   │ (composition)  │                       │ (composition)   │
   ▼                ▼                       ▼                 ▼
Commercial.Application ◄──── Commercial.Infrastructure ────►(EF, Npgsql, Redis, Hangfire, Serilog, OTel)
   │                                        │
   ▼                                        ▼
Commercial.Domain ◄──────────────────────────┘

Commercial.Connectors.Abstractions      (provider-independent primitives; no Commercial.* deps)
Commercial.Connectors.<Provider>        (Phase 6+; depends on Abstractions + own transport only)
```

| Assembly | May contain | Must not |
|---|---|---|
| Domain | domain-safe primitives, domain errors, domain-event abstraction, strongly typed IDs | EF Core, ASP.NET Core, Hangfire, Redis, Npgsql, HTTP, logging impl, cloud SDKs, provider code, Connectors.* |
| Application | use-case orchestration, ports, validation pipeline, current-scope abstractions, transaction abstraction | DbContext impl, provider branching, endpoints, Infrastructure/Api/Worker/connector implementation refs |
| Infrastructure | EF DbContext, PostgreSQL, Redis, Hangfire, outbox persistence/dispatch, DB-session hook, Serilog/OTel wiring | commercial business rules / use cases |
| Api | HTTP host, OpenAPI, authn/authz extension points, error handling, correlation, health/version, versioning | business logic; direct DbContext/Npgsql/Redis/Hangfire storage use in endpoints |
| Worker | Hangfire server, outbox dispatch execution, job hosting | provider jobs (Phase 0); direct mutation of business aggregates via DbContext |
| Connectors.Abstractions | provider-independent capability/result/error/identity primitives | any provider-specific type/namespace; large speculative SDK |
| Connectors.&lt;Provider&gt; | provider adaptation over public APIs | references to other connectors; references to provider internals (e.g. BusinessOS.*) |

Production projects form an acyclic graph and never reference test projects/packages.
Details and executable mapping: `docs/architecture/dependency-rules.md`.

## 6. Tenant isolation

- The production isolation mechanism (EF global query filters vs PostgreSQL RLS vs justified hybrid)
  is decided by **ADR-0001 before Phase 1 kickoff**. Phase 0 must not decide it implicitly.
- Required seams (exist from Phase 0): centralized execution scope; centralized DbContext/connection
  registration; DB-session initializer hook invoked when a connection becomes usable; no-op default;
  integration proof through the real pooled connection path.
- **Decided by ADR-0001 (ACCEPTED 2026-10-07): RLS-primary hybrid.** PostgreSQL RLS is the authoritative
  tenant/company/customer visibility boundary (ENABLE + FORCE, USING + WITH CHECK, fail-closed); EF Core does
  fail-fast scope validation, write stamping and scope-fingerprint protection but never re-implements visibility
  (no EF global query filters); runtime role owns no tables (owner / app / maintenance roles); `No Reset On Close`
  is forbidden. Infrastructure tables (outbox, diagnostic receipts, `hangfire.*`) stay outside business RLS.

## 7. Provider independence

- Commercial Domain and Application contain **no provider-specific logic** and never branch on
  provider identity (`if (provider == "...")` is forbidden). Provider behaviour lives behind the
  connector/adaptation boundary; core works with provider-independent capabilities and contracts.
- Provider leakage is detected with **exact provider tokens**, never broad substrings. Plain `Logo`,
  plain `SAP` (and generic words such as `Winner`) are never banned; `CompanyLogo`, `LogoUrl`,
  `BrandLogo`, `SaplingConfiguration` are legitimate. Token list: Architecture Test Contract §4 plus the
  exact roots of connectors as they are introduced (`NebimV3`, `WinnerErp`, …).

## 8. Transactional outbox

- Outbox rows are written in the same PostgreSQL transaction as the state change.
- Minimum fields: `Id, OccurredAtUtc, Type, Payload, CorrelationId, CausationId?, TenantId?, Attempts,
  NextAttemptAtUtc, ProcessedAtUtc, LastError, Status, LockedBy?, LockedUntilUtc?`.
- **Ownership:** short claim transaction with `FOR UPDATE SKIP LOCKED` + persisted lease
  (`LockedBy`, `LockedUntilUtc`); processing outside the claim transaction; success/failure only by the
  current lease owner; expired leases are reclaimable. "Hangfire runs it once" is not sufficient.
- Delivery is at-least-once; **handlers must be idempotent**.

## 9. HTTP contract and errors

- REST, URL-versioned (`/api/v1`), described by OpenAPI; contract changes require an explicit snapshot update.
- Stable error envelope (`type, title, status, code, detail, correlationId, errors` — plus `instance`,
  `traceId`), `application/problem+json`; no client-visible stack traces or raw internal exception
  messages; validation (400) vs conflict (409) and 401 vs 403 are distinct.
- Browsers talk **only** to the Commercial API — never directly to Kumpara/Logo/any ERP.

## 10. Observability and security foundation

- All hosts: structured logging, correlation ID, trace ID, OpenTelemetry tracing, metrics, liveness vs readiness.
  Readiness covers PostgreSQL, Redis and Hangfire storage.
- Authentication-scheme extension points; `WORKFORCE` / `CUSTOMER` principal-type claim contract;
  authorization-policy foundation; secrets never committed; development identity unmistakably dev-only.

## 11. Delivery governance

- Exactly one active main phase: `docs/architecture/active-phase.json`. Business capability outside the
  active phase is not built. Phase changes happen only in a dedicated, human-approved **kickoff PR** that
  proves the previous gate, activates the next mandate, changes `active-phase.json` and advances A018
  phase-scope data — and nothing else.
- Architecture tests (A001–A018) are executable merge blockers. Phase 0 enforcement is never relaxed in
  later phases; only phase-aware scope data advances.
- KAPI-1 (Kumpara Public API fit-gap & readiness) runs in parallel from Phase 0 in the Kumpara repository;
  it does not widen Commercial Platform scope. KAPI-1 READY is required for Phase 6A acceptance.

## 12. Changing this document

Weakening an invariant requires: architectural reason, rejected alternatives, impact analysis and an
ADR (and/or a new Frozen Architecture version). Normal phase activation (§11) is the only exception.
