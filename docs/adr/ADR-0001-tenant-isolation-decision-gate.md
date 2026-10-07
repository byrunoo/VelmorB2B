# ADR-0001 — Tenant Isolation: RLS-Primary Hybrid

**Status:** ACCEPTED — RLS-primary hybrid (PostgreSQL RLS authoritative; EF Core fail-fast, stamping and scope protection only)
**Deciders:** Commercial Platform architecture owners — human approval given 2026-10-07
**Proposed:** 2026-10-07 · **Accepted:** 2026-10-07
**Enforced by:** architecture test A014 (Level A implementation invariants + Level B Phase 1 acceptance — §8)
**Evidence:** `tests/Commercial.IntegrationTests/Spikes/TenantIsolation/` (15 tests, Testcontainers PostgreSQL 17)
**Related:** `docs/architecture/frozen-architecture-v1.0.md` §2, §6; Phase 0 mandate §10; test contract A014;
DBA plan `scripts/db/shared-dev-role-split.sql` (not executed).

The rules below are **normative**. Changing them requires a superseding ADR.

---

## 1. Context

Commercial Platform is multi-tenant, multi-company, multi-sales-channel and multi-external-system
(Tenant → N Company, N Sales Channel, N external connection; Company → N connection, N portal; portal ↔
connection not 1:1). Isolation must hold at three levels — Tenant, Company, CustomerAccount — for
interactive users, background jobs and every SQL access path.

## 2. Decision

**RLS-PRIMARY HYBRID.**

### 2.1 Security source of truth

1. PostgreSQL Row-Level Security is the **primary and authoritative** boundary for tenant / company /
   customer-account data visibility.
2. EF Core global query filters are **not** used as a copy of the visibility rule — **no** global visibility
   filter for `TenantId`, `CompanyId` or `CustomerAccountId`. Visibility semantics exist only in PostgreSQL
   policies, so EF/RLS semantic drift cannot occur.

### 2.2 EF Core / Application responsibilities

- `ExecutionScope` fail-fast validation: missing/invalid scope for tenant-scoped work fails **before** SQL is sent.
- Write stamping of `TenantId` / `CompanyId` / `CustomerAccountId` from `ExecutionScope`.
- `SaveChanges` validation (stamped values must equal the scope; foreign scope values are rejected).
- Scope consistency checking and `DbCommand` scope-fingerprint protection (a connection whose applied scope
  differs from the ambient `ExecutionScope` is re-initialized or the command fails).
- EF never re-implements the RLS visibility policy.

### 2.3 Database role model

| Role | Purpose | Attributes / rules |
|---|---|---|
| `commercial_platform_owner` | EF migrations; owns schemas, tables, sequences, policy helper functions; creates RLS policies; provisions the Hangfire schema | NOSUPERUSER, NOBYPASSRLS; never used by API/Worker runtime |
| `commercial_platform_app` | API and Worker runtime | LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE, NOBYPASSRLS; owns **no** business table; DML grants only. RLS is its real security boundary |
| `commercial_platform_maintenance` | controlled DBA/ops work | may have BYPASSRLS; never in the API connection string, never the Worker's runtime credential, never part of normal application configuration |

### 2.4 Principals and scope

| Principal | Required scope for business tables | Notes |
|---|---|---|
| `WORKFORCE` | `TenantId`, **single** `ActiveCompanyId`, `PrincipalId`, permissions | May be *authorized* for several companies (grant model), but each request runs with exactly one active company; switching company is an explicit scope switch. Tenant-wide operations require an explicit permission/grant — never the default. |
| `CUSTOMER` | `TenantId`, `CompanyId`, `CustomerAccountId` (all mandatory, taken from the **active** CustomerPrincipalBinding) | Within a request: never another customer account, company or tenant; never tenant-wide or multi-company visibility. *Clarification 2026-10-07:* the same principal MAY hold several bindings across companies and tenants; each request runs under exactly one active binding (see `docs/architecture/identity-and-customer-membership.md`). |
| `SYSTEM` | `TenantId`, `CompanyId` (+ `CustomerAccountId` when relevant) | Being SYSTEM grants **no** unrestricted access. Hangfire/outbox handlers use outbox metadata → `ExecutionScope` → DB session initializer → RLS. Cross-tenant work never runs as SYSTEM; it uses the maintenance mechanism. |

Missing, invalid or partial scope ⇒ **no access** (no rows; writes rejected).

### 2.5 Infrastructure tables

`commercial.outbox_messages`, `commercial.diagnostic_ping_receipts` and `hangfire.*` are **outside** business RLS
because the dispatcher performs cross-tenant infrastructure orchestration. They must not be reachable from
Application business queries or API endpoints (architecture rules A006/A010/A013). Outbox rows carry scope
metadata (`TenantId` today; `CompanyId` and scope/grant metadata are added with Phase 1 as needed).

### 2.6 Business table baseline

- Tenant-scoped tables: `tenant_id uuid NOT NULL`; company-scoped: `company_id uuid NOT NULL`;
  customer-scoped (where applicable): `customer_account_id uuid NOT NULL`.
- `ALTER TABLE … ENABLE ROW LEVEL SECURITY` **and** `FORCE ROW LEVEL SECURITY` (defense in depth; the runtime role
  is not the owner anyway).
- Policies have `USING` **and** `WITH CHECK`; default is no access for missing/invalid/partial scope.
- Indexes, unique constraints and foreign keys are tenant-aware: `tenant_id` is the leading (or otherwise
  isolation-correct) component; foreign keys are composite and include `tenant_id`, so a Tenant A row can
  never reference a Tenant B row.

### 2.7 Session context

- Production `IDbSessionInitializer` (Phase 1) rewrites **all** context keys on **every** connection open; it
  never trusts state from a previous use of the connection.
- Keys (custom GUC namespace): `commercial.tenant_id`, `commercial.company_id`, `commercial.customer_account_id`,
  `commercial.principal_type` (`WORKFORCE|CUSTOMER|SYSTEM`), `commercial.company_access` (explicit
  tenant-wide grant; never honoured for CUSTOMER). Exact names may be refined by the Phase 1 mandate without
  changing semantics.
- Values come only from a validated `ExecutionScope`; SQL is parameterized; policies treat `NULL` **and** `''`
  as "no scope" (a transaction-local custom GUC reverts to `''`).
- Policy helper functions are `STABLE`, `SECURITY INVOKER` — never `SECURITY DEFINER`.

### 2.8 Connection pooling

Npgsql pooling is allowed; `No Reset On Close` **must be false** (enforced by configuration guard and A014).
Tenant X → Tenant Y → Tenant X on one pooled connection must never leak scope (continuous Phase 1 test).

## 3. Evidence (spike, Testcontainers PostgreSQL 17)

| Finding | Result |
|---|---|
| Cross-tenant read / write (incl. UPDATE/DELETE of invisible rows, INSERT/UPDATE into foreign scope) | denied (0 rows / 42501 `WITH CHECK`) |
| Company isolation; tenant-wide only with explicit grant; CUSTOMER cannot use tenant-wide grant | confirmed |
| Customer X vs Y; own rows readable/writable | confirmed |
| Missing / empty / partial / principal-less scope | fail-closed |
| Raw SQL, `WHERE true`, `IgnoreQueryFilters()` under RLS | contained (2/2 rows of X) |
| `IgnoreQueryFilters()` and raw SQL **without** RLS | 6/6 rows (why EF filters are not a boundary) |
| EF filter vs policy disagreement | stricter silently wins (drift risk) |
| Table owner with `ENABLE` only | bypasses RLS (6/6) |
| Owner with `FORCE` | filtered — but owner can `NO FORCE` / `DISABLE` itself |
| Non-owner NOBYPASSRLS runtime | cannot disable RLS or `SET row_security = off` |
| Npgsql default pool reset | session settings cleared on reuse |
| `No Reset On Close=true` | **scope leaked** to the next user of the connection |
| All-keys rewrite on open via the real Phase 0 seam | X → unscoped → Y/X on the same backend PID, no residue |
| Transaction-local scope | cleared at COMMIT/ROLLBACK; lost between autocommit statements (not primary mechanism) |
| SYSTEM without tenant / with tenant but no company or grant | no rows |
| Policy predicates at 50k rows / 400 tenants | index scan on `(tenant_id, company_id, customer_account_id)` |

## 4. Consequences

- Before the first tenant-scoped table: owner/runtime/maintenance role split on every database (shared dev:
  DBA script `scripts/db/shared-dev-role-split.sql`, executed manually by a DBA — never by automatic runtime
  migration), migrations run as owner, Hangfire schema provisioned by owner.
- Phase 1 delivers: production session initializer, scope fingerprint interceptor, `ExecutionScope`
  (`ActiveCompanyId`, grants, permissions), fail-fast guard, write stamping/validation, tenant-scoped marker,
  RLS migration conventions, outbox scope metadata, the isolation test suite.
- A014 enforces Level A implementation invariants; Level B is the Phase 1 acceptance suite (§8).

## 5. Rejected alternatives

- EF global query filters as the boundary (Option A): bypassable by `IgnoreQueryFilters`, raw SQL, other contexts.
- Duplicated visibility rules in EF and SQL (full hybrid): semantic drift and double maintenance.
- Runtime role = table owner (even with `FORCE`): the owner can switch RLS off.
- Transaction-local scope as the only mechanism: lost between autocommit statements.
- Database/schema per tenant as the primary model: operational cost at many small tenants; company/customer
  rules still need row-level enforcement (may be revisited for exceptional very large tenants by a new ADR).

## 6. Resolved questions

| Question | Decision |
|---|---|
| RLS-primary hybrid? | Accepted |
| Three-role model; who applies it | Accepted; DBA applies an explicit script before Phase 1 tenant tables |
| Infrastructure tables outside business RLS? | Yes (outbox, diagnostic receipts, `hangfire.*`), confined by architecture rules |
| WORKFORCE company access | One `ActiveCompanyId` per request; multi-company authorization is a grant model; explicit switch |
| Tenant-only EF filter? | No — no EF visibility filters at all |

## 7. Remaining (non-blocking) items for the Phase 1 mandate

- Final GUC key names and helper-function schema location.
- Permission catalogue that expresses the tenant-wide grant.
- Volume-based performance validation (large vs many small tenants, keyset pagination, joins, aggregates).

## 8. Enforcement (A014 — Level A invariants + Level B acceptance; updated 2026-10-07)

| Stage | Checks |
|---|---|
| Always | execution-scope seam exists; session-initializer hook wired into DbContext; **no EF global query filters** (visibility lives in RLS); `No Reset On Close` never enabled (config + resolver guard); phase ≥ 1 requires `ACCEPTED` |
| ADR not accepted | RLS policies forbidden in migrations |
| Level A — implementation invariants (any phase; triggered by the implementation, not by a phase number) | scoped entity ⇒ NOT NULL scope columns + `ENABLE` + `FORCE` RLS + USING/WITH CHECK policy in migrations; any isolation implementation (scoped entity, RLS DDL, production initializer type) ⇒ registered `IDbSessionInitializer` is not the no-op and core proofs exist (`cross-tenant`, `cross-company`, `cross-customer`, `fail-closed`, `pool-scope-leak`, `runtime-role-not-owner`) |
| Level B — Phase 1 acceptance | full isolation/security suite and acceptance gate of the Phase 1 mandate, proven by the acceptance run and report — no manual "phase complete" flag |
