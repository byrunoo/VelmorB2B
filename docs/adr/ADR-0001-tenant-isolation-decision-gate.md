# ADR-0001 — Tenant Isolation Decision Gate

**Status:** REQUIRED BEFORE PHASE 1 — NOT YET DECIDED
**Deciders:** Commercial Platform architecture owners (human review required)
**Gate:** Phase 1 kickoff PR is blocked until this ADR is `ACCEPTED` (enforced by architecture test A014).
**Related:** `docs/architecture/frozen-architecture-v1.0.md` §6, `docs/bootstrap/PHASE0_IMPLEMENTATION_MANDATE.md` §10, `docs/bootstrap/ARCHITECTURE_TEST_CONTRACT.md` A014.

> This ADR records the decision space and the seams that Phase 0 built. It intentionally does **not**
> choose. Phase 0 code must not implement RLS policies or global tenant query filters.

## 1. Context

Commercial Platform is multi-tenant, multi-company, multi-sales-channel (B2B portal) and
multi-external-system. Frozen cardinality:

- a **Tenant** owns many **Companies**, many **Sales Channels / B2B portals** and many
  **external-system connections**;
- a **Company** may have many external-system connections and run many portals;
- **Portal ↔ connection is not 1:1**; capability sources are selected by capability + authority + policy.

All persisted business data from Phase 1 onward will be tenant-scoped, and much of it company- and
customer-account-scoped. The isolation mechanism shapes connection handling, DbContext
initialization, migrations and test strategy, so it must be chosen before Phase 1 creates the first
tenant-scoped table.

## 2. Options under consideration

### Option A — EF Core global query filters + explicit execution scope

- Every tenant-scoped entity gets `HasQueryFilter(e => e.TenantId == scope.TenantId)` (EF 10 named filters
  allow company/customer filters to be toggled independently).
- Isolation lives in the application process; raw SQL, Dapper, reporting connections and
  `IgnoreQueryFilters()` bypass it.
- No database-side session state; connection pooling is unaffected.

### Option B — PostgreSQL Row-Level Security + per-connection/transaction context

- Tables get `ENABLE ROW LEVEL SECURITY` + policies reading `current_setting('app.tenant_id')`.
- The session initializer sets context on every connection/transaction. With pooled connections the
  setting must be **transaction-scoped** (`set_config(..., true)` inside the transaction) or reliably
  reset on return to the pool (session-level values leak between pooled usages).
- Raw SQL and reporting are also protected; requires a non-owner application role (owners bypass RLS
  unless `FORCE ROW LEVEL SECURITY`), migrations run as a separate role, and testing must cover policy SQL.

### Option C — Justified hybrid

- e.g. RLS as the hard tenant boundary + EF filters for company/customer-account narrowing and ergonomics.

## 3. Evaluation criteria (to be scored at decision time)

1. Defence in depth against IDOR / cross-tenant reads (Phase 1 gate: cross tenant/company/customer PASS).
2. Behaviour with Npgsql connection pooling and Hangfire/outbox background execution (System principal).
3. Raw SQL paths (outbox claim, reporting) — protected or explicitly exempt?
4. Migration/role model and operational complexity.
5. Testability (Testcontainers) and performance (plan stability with RLS predicates).
6. Cross-company scenarios inside one tenant (workforce users spanning companies).

## 4. Phase 0 seams already in place (decision-neutral)

| Seam | Location | Phase 0 behaviour |
|---|---|---|
| `ExecutionScope` (TenantId, CompanyId, CustomerAccountId, SalesChannelId, PrincipalType, PrincipalId, CorrelationId, CausationId) | `src/Commercial.Application/Abstractions/Scope/` | Populated from validated claims (API) or outbox message (Worker); **not enforced** |
| `IExecutionScopeAccessor` (AsyncLocal) | same | Works for HTTP, Hangfire jobs, outbox dispatch |
| Centralized DbContext/connection registration | `src/Commercial.Infrastructure/Persistence/PersistenceRegistration.cs` | Single `NpgsqlDataSource`, single `CommercialDbContext` registration |
| `IDbSessionInitializer` + `DbSessionInitializerInterceptor` | `src/Commercial.Infrastructure/Persistence/Session/` | Invoked on every EF connection open (incl. pooled physical connections); default `NoOpDbSessionInitializer` |
| Integration proof of the hook | `tests/Commercial.IntegrationTests/Persistence/SessionInitializerTests.cs` | Proves invocation through the real DbContext + pooled connection path and that SQL issued by the hook is visible to the context's queries |
| Gate enforcement | `tests/Commercial.ArchitectureTests/TenantIsolationReadinessRules.cs` | Fails if RLS/query filters appear while undecided, or if phase ≥ 1 while undecided |

Note: outbox `TenantId` is nullable until the Phase 1 tenant contract is finalized.

## 5. Decision

_Not yet decided._ To be completed by human review before the Phase 1 kickoff PR. When accepted:

1. change **Status** to `ACCEPTED — <option>` (A014 requires the status line to start with `ACCEPTED` for phase ≥ 1),
2. record rejected alternatives and the impact analysis,
3. update `frozen-architecture-v1.0.md` §6 if an invariant is added,
4. replace `NoOpDbSessionInitializer` / add query filters in the Phase 1 implementation.

## 6. Consequences

Until accepted, no tenant-scoped business table may be created (also blocked by A018 Phase 0 scope).
