# Commercial Platform — Phase 1 Identity / Tenant / Security Implementation Mandate

**Status:** APPROVED FOR IMPLEMENTATION — ACTIVE (approved and activated 2026-10-07 by the Phase 1 kickoff;
`docs/architecture/active-phase.json` = phase 1)
**Phase:** 1 — Identity / Tenant / Security
**Architecture source:** `docs/architecture/frozen-architecture-v1.0.md` (highest priority)
**Normative dependency:** ADR-0001 — **ACCEPTED** — RLS-primary hybrid (`docs/adr/ADR-0001-tenant-isolation-decision-gate.md`)
**Governance dependency:** ADR-0002 — **ACCEPTED — TEMPORARY WAIVER** (Phase 0 hosted CI deferred; D1–D7 required before commercial release)
**Kickoff package:** `docs/phases/phase-1/` (`KICKOFF.md`, `phase-scope.phase1.json`, `active-phase.proposed.json`)
**Test contract:** `docs/bootstrap/ARCHITECTURE_TEST_CONTRACT.md` (A001–A018; A014 Level A model §21; A018 phase-1 data §21)

Normative words: **MUST / MUST NOT / SHOULD / MAY**.

---

## 1. Objective

Implement the authoritative identity, tenant, company, principal, authorization-scope and PostgreSQL RLS
security foundation required by every future business module.

Phase 1 leaves the platform with enforceable: tenant isolation, company isolation, the customer-account
isolation foundation, the WORKFORCE / CUSTOMER / SYSTEM principal models, role/permission authorization,
PostgreSQL RLS, secure connection/session context, database role separation, security auditability and
IDOR / horizontal-privilege-escalation protection.

Phase 1 is **not** Customer, Catalog, Pricing, Inventory, Quote or Order implementation (§25).

## 2. Preconditions for the kickoff PR

1. ADR-0001 ACCEPTED — met.
2. ADR-0002 ACCEPTED — TEMPORARY WAIVER — met (hosted CI remains NOT PASS; D1–D7 deferred).
3. This mandate approved — met (activation pending).
4. Kickoff PR content limited to the kickoff exemption: `active-phase.json` → phase 1, this mandate's status line,
   the A018 phase-1 scope data (§21). No functional code.
5. Local CI green on the kickoff commit (verified by dry run: architecture tests pass with phase 1 data).

## 3. Product cardinality (frozen — must be preserved)

- Tenant → N Companies, N B2B Portals / Sales Channels, N ERP / external connections.
- Company → N B2B Portals, N ERP / external connections.
- No `1 Tenant = 1 Company = 1 ERP = 1 B2B` assumption anywhere (schema, scope, claims, configuration).
- Portal ↔ ERP connection is **not** 1:1.
- Phase 1 MUST NOT implement B2B Portal or ERP-connection modules; it MUST only avoid decisions that make these
  cardinalities impossible (e.g. a scope MAY later carry `SalesChannelId`; no "the tenant's ERP" field).

## 4. ADR-0001 is normative

- **PostgreSQL RLS is the security source of truth** for tenant / company / customer-account visibility:
  actual row visibility, `USING` and `WITH CHECK` policies, fail-closed behaviour.
- **EF Core MUST NOT** duplicate visibility semantics — no tenant, company or customer-account global query
  filters (A014).
- EF Core / Application responsibilities: fail-fast scope validation, write stamping, `SaveChanges` scope
  consistency, scope-fingerprint validation, developer-visible errors.

## 5. Database role separation (prerequisite)

| Role | Responsibilities | Rules |
|---|---|---|
| `commercial_platform_owner` | EF migrations; schema/table/sequence/function owner; RLS policies; helper functions; Hangfire schema provisioning | MUST NOT be used by API/Worker runtime |
| `commercial_platform_app` | API + Worker runtime | LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE, NOBYPASSRLS; owns no schema/table; DML only as required |
| `commercial_platform_maintenance` | controlled operations | MAY have BYPASSRLS; MUST NOT be a normal application credential or part of normal application configuration |

The **same three-role architecture** MUST be used in every environment:

| Environment | Provisioning |
|---|---|
| Local docker-compose | development-only init script creates the three roles; development-only local credentials supplied via `.env` (gitignored) / user-secrets — no real or production credentials committed. API/Worker run as `commercial_platform_app`; migrations/schema provisioning as `commercial_platform_owner`; maintenance never used by normal runtime |
| Shared development DB | DBA executes the reviewed `scripts/db/shared-dev-role-split.sql` manually (never application startup or migrations) |
| Testcontainers | fixtures create the three roles per run with generated throw-away credentials; every isolation test runs as the real runtime role |
| Production | provisioned by operations; credentials only in the secret store |

Before the **first real tenant-scoped Phase 1 migration is applied to the shared development DB**:
owner/runtime split implemented in code · migration credential support exists · Hangfire schema provisioning is
owner-driven · `shared-dev-role-split.sql` reviewed and executed by a DBA · post-conditions verified.

## 6. Migration / runtime identity separation (first implementation step)

- Separate credentials: migration = `commercial_platform_owner`; runtime = `commercial_platform_app`;
  maintenance = outside application configuration.
- A distinct migration connection (e.g. `Commercial:Postgres:Migration:Username|Password` /
  `COMMERCIAL__POSTGRES__MIGRATION__*`) resolved by the single `PostgresConnection` resolver rules; secrets external.
- Migrations MUST NOT silently run with runtime credentials (`dotnet ef`, migration bundle, `scripts/shared-db.ps1
  migrate` use the migration identity). Startup migration is refused when the configured identity is the runtime role.
- The runtime role cannot create/alter business tables (test).
- Hangfire: `Hangfire:PrepareSchemaIfNecessary` configurable; `false` for the runtime after provisioning; schema
  provisioned explicitly with the owner identity; Hangfire tables stay outside business RLS; the Worker runs as
  `commercial_platform_app`.

## 7. Phase 1 domain / identity model (allowed)

| Entity | Notes |
|---|---|
| `Tenant` | id, code/name, status (Active/Suspended), created/updated UTC, concurrency (xmin) |
| `Company` | id, `tenant_id`, code/name, status, created/updated UTC, concurrency — no commercial fields |
| `Principal` | **global identity layer** — one per human (never duplicated per company/tenant): id, `principal_type` (WORKFORCE/CUSTOMER), status, administrative display data |
| `PrincipalExternalIdentity` | **global identity layer** — external **issuer + subject** → principal (authoritative authenticated identity; no passwords) |
| `Role`, `Permission`, `RolePermission` | explicit permission catalogue (§15); roles are permission bundles |
| `PrincipalRoleAssignment` | WORKFORCE principal ↔ role within a scope (tenant-wide or company) |
| `CompanyAccessGrant` | WORKFORCE authorization to a company (multi-company authorization; one active company per request) |
| `CustomerPrincipalBinding` | **one customer membership**: `Id, PrincipalId, TenantId, CompanyId, CustomerAccountId, Status, CreatedAtUtc, UpdatedAtUtc`. Principal 1→N bindings across companies **and tenants**; CustomerAccount 1→N bindings. `customer_account_id` is an opaque security reference |
| `CustomerBindingRoleAssignment` | binding ↔ role (a binding may hold several roles, e.g. BUYER + FINANCE); roles are never global principal attributes |
| `CustomerUserInvitation` | pending membership offer: target tenant/company/customer account (from server context), invited email, requested role assignments, inviter, created/expires/accepted UTC, status, **token hash only** |
| `SecurityAuditEvent` | tenant-scoped, RLS-protected, append-only security audit (§16) |

**Customer boundary:** no `CustomerAccount` entity, table or commercial domain in Phase 1; no customer data
(names, addresses, terms, balances…). Customer commercial data is Phase 2.

**Approved membership model (2026-10-07, normative):** `docs/architecture/identity-and-customer-membership.md`.
*Revoked:* "CUSTOMER belongs to one company / is never multi-company". *Replacement:* **A CUSTOMER Principal may have
multiple CustomerPrincipalBindings across multiple Companies and multiple Tenants. A normal request executes under
exactly one active CustomerPrincipalBinding.**

## 8. ExecutionScope v2

Conceptual fields: `TenantId`, `ActiveCompanyId`, `CustomerAccountId?`, `ActiveCustomerBindingId?`, `PrincipalType`,
`PrincipalId`, `Permissions`, explicit `Grants` (incl. tenant-wide grant), `CorrelationId`, `CausationId`,
`ScopeFingerprint`.

| Principal | Required | Rules |
|---|---|---|
| WORKFORCE | TenantId, PrincipalId; ActiveCompanyId for company-scoped business access | may be authorized for many companies (grants) but exactly **one** ActiveCompanyId per request; switching is explicit (§10); tenant-wide access only with an explicit permission/grant |
| CUSTOMER | ActiveCustomerBindingId, and from that binding row: TenantId, CompanyId, CustomerAccountId; PrincipalId | may hold **many** bindings (multiple companies and tenants), but each request runs under **exactly one active binding**; rows of several bindings are never merged; permissions come only from the active binding's roles; never tenant-wide; switching is explicit (§10) |
| SYSTEM | TenantId + CompanyId for business tables; CustomerAccountId where relevant | implies **no** unrestricted access; scope from trusted job/outbox metadata |
| MAINTENANCE | — | separate DB identity and operational path; never an ExecutionScope of a normal request/job |

- Invalid combinations are rejected at construction (fail-fast, developer-visible).
- `ScopeFingerprint` is a deterministic hash of all security-relevant fields.

## 9. Two-stage security context (bootstrap identity resolution)

**Stage 1 — Authenticated identity context.** After token validation the authentication handler creates a minimal,
trusted server-side context: `Issuer`, `Subject`, `PrincipalType` claim where applicable, `CorrelationId`. It comes
**only** from the validated token/authentication handler — never from request body, query or arbitrary headers.

- With this context the system MAY resolve **only** the bindings of `Issuer + Subject`: external identity →
  principal → role assignments, company grants, **all of the principal's own customer bindings** (e.g. to offer an
  account chooser) — and nothing belonging to anyone else.
- The principal MUST NOT be able to enumerate other principals or bindings during bootstrap.
- Enforcement: a narrow bootstrap RLS policy on the identity-directory tables keyed on the issuer/subject written by
  the session initializer in bootstrap mode (or an equivalent reviewed safe DB design). **No `SECURITY DEFINER`
  shortcut.**
- In bootstrap mode the business-scope keys are empty, so every business table returns no rows.

**Stage 2 — Execution scope.** After bindings/grants are resolved and the requested context is authorized, the
`ExecutionScope` (§8) is constructed. **Only after this point** may normal tenant/company/customer business RLS
access occur. The transition (stage 1 → stage 2) MUST be documented in code and covered by tests (§20).

## 10. Active company selection

WORKFORCE principals select the active company with the request header **`X-Active-Company-Id`** — a context
selection request, **not** authorization. Server behaviour:

1. authenticate the principal (stage 1);
2. resolve the principal's authorized company grants;
3. read `X-Active-Company-Id`;
4. verify the requested company belongs to the current tenant;
5. verify the principal is authorized for that company;
6. create the `ExecutionScope` (stage 2);
7. only then execute business/database operations.

Invalid or unauthorized company ⇒ **403** (standard error envelope) and **no** scoped DB access. One active company
per request; switching is explicit (and audited).

CUSTOMER principals select the **active customer binding**, not a company. Recommended (design approved, transport to be
confirmed at implementation): header **`X-Active-Customer-Binding-Id`** carrying the opaque binding id. The server
verifies the binding belongs to the authenticated principal and is Active, then reads Tenant / Company /
CustomerAccount **from the binding row** — the client never supplies those ids as authority. Foreign/disabled/pending
binding ⇒ 403; several bindings and no selection ⇒ explicit "selection required" error (never a merged context); a
single active binding MAY be selected implicitly. `X-Active-Company-Id` is not accepted as authority for CUSTOMER.
Comparison with server-side session selection and token-per-binding:
`docs/architecture/identity-and-customer-membership.md` §5.

SYSTEM jobs never use this header; their scope comes from trusted job/outbox metadata.

## 11. Session initializer — `RlsDbSessionInitializer`

- Implements the existing `IDbSessionInitializer` seam; replaces `NoOpDbSessionInitializer` (A014 Level A).
- On **every** opened connection writes the **complete** context with one parameterized statement
  (`set_config(key, value, false)` per key): mode (bootstrap/scoped), issuer/subject (bootstrap), tenant, company,
  customer account, principal type, principal id, grants (e.g. tenant-wide flag), fingerprint. Missing values are
  written as `''` (explicit reset).
- Never relies on pooled-connection residue; `No Reset On Close` forbidden (resolver + A014).
- GUC namespace `commercial.*`; final key list fixed in the RLS foundation migration and documented.
- Policies and helper functions treat `NULL` and `''` as no scope (fail-closed).

## 12. Scope fingerprint guard

- A `DbCommandInterceptor` compares the ambient `ExecutionScope` fingerprint with the fingerprint written to the
  session at connection initialization, before any command executes.
- Mismatch ⇒ fail closed with an explicit internal security exception (logged + audited); the command is not sent.
- The guard MUST NOT silently rewrite scope on a connection with an active command/transaction; a scope switch
  requires a new unit of work (new open ⇒ initializer).

## 13. RLS foundation and helper functions

- Markers: `Commercial.Domain.Isolation.ITenantScoped`; `ICompanyScoped`, `ICustomerAccountScoped` only where
  justified (entities are not forced into the most specific marker).
- Columns: `tenant_id uuid NOT NULL`; company-scoped + `company_id uuid NOT NULL`; customer-scoped +
  `customer_account_id uuid NOT NULL` as applicable.
- Every marked table: `ENABLE` + `FORCE ROW LEVEL SECURITY`; SELECT/INSERT/UPDATE/DELETE policies with `USING` and
  `WITH CHECK` as appropriate; fail-closed.
- Tenant-aware keys: unique constraints/indexes lead with (or correctly include) `tenant_id`; foreign keys between
  tenant-scoped tables are composite including `tenant_id`.
- `Tenant`: visible where `id = current tenant`; `Company`: tenant + company rules.
- **Global identity layer** (`Principal`, `PrincipalExternalIdentity`): not tenant-owned rows (a principal spans
  tenants). Dedicated policies: bootstrap mode sees only the caller's own issuer+subject identity; scoped mode lets
  administrators see only minimal identity data of principals holding a membership/grant in the **active** scope.
  No `SECURITY DEFINER`; tenant business-data isolation is not weakened.
- **Membership layer** (`CustomerPrincipalBinding`, `CustomerBindingRoleAssignment`, `CustomerUserInvitation`, grants,
  audit): tenant-scoped RLS (plus the bootstrap policy for the caller's own bindings).
- Helper functions: migration-managed, owned by `commercial_platform_owner`, `STABLE` where appropriate,
  `SECURITY INVOKER`, no dynamic SQL; missing/empty values ⇒ no access; no `SECURITY DEFINER` for tenant visibility
  unless a separate reviewed ADR justifies it; tests inspect definitions (`pg_proc`: volatility, `prosecdef = false`).

## 14. Write stamping and validation

- Central `SaveChanges` interceptor: new `ITenantScoped` ⇒ `TenantId` from scope; `ICompanyScoped` ⇒ `CompanyId`
  from scope; caller-supplied values are never trusted (must equal the scope, else error).
- Modified entities: moving a row across tenants is always denied; moving to an unauthorized company is denied;
  `CustomerAccountId` changes by CUSTOMER principals are denied.
- RLS `WITH CHECK` remains the final DB enforcement.

## 15. Authentication and authorization

**Authentication (provider-independent).**
- OIDC / OAuth2 compatible; **no commercial identity provider is selected or hard-coded** in Phase 1.
- Validated external identity minimum: `Issuer` + `Subject`. Standard claims (name, email, …) are informational and
  never authorization truth by themselves. Tenant/company/customer values in tokens are not authority.
- The platform MUST NOT store user passwords as the primary production authentication strategy.
- An authentication integration boundary wraps validated identity claims; no vendor-specific core types
  (e.g. `EntraUser`, `Auth0User`, `KeycloakUser`) in Domain/Application.
- Development header auth stays strictly Development-only (test proves it is absent elsewhere).
- Integration tests use a test issuer / test OIDC setup to prove: token validation → external identity → principal
  resolution → scope resolution → authorization.
- Production IdP selection is not a Phase 1 blocker.

**Authorization.**
- Explicit `Permission` catalogue; Phase 1 categories only: tenant administration, company administration,
  identity administration, security administration (roles, permissions, grants, audit read), **customer user
  administration** (e.g. `customer.users.read|invite|manage`, `customer.roles.assign`).
- No Product/Order/Invoice/commercial permissions.
- Code checks permissions, never role names. Initial customer roles OWNER / BUYER / VIEWER / FINANCE are permission
  bundles; their business permissions arrive with later phases.
- WORKFORCE multi-company access = authorization grants + single active company (§10).
- CUSTOMER roles are **binding-scoped** (`CustomerBindingRoleAssignment`); `Principal → GlobalRole` is forbidden; no
  role/permission leaks between bindings. CUSTOMER permissions cannot widen scope; principal type cannot be changed by
  role/permission.

## 15a. Customer user management and invitations (approved 2026-10-07)

Details: `docs/architecture/identity-and-customer-membership.md` §6–§8, §11–§12.

- Customer-side management uses the **active** binding only: e.g. OWNER of A1/CA-100 may invite/manage users of
  A1/CA-100, never of another binding where the same principal is VIEWER. Target Tenant/Company/CustomerAccount always
  come from server-side context, never from input. WORKFORCE users with security permissions use the same
  binding/invitation foundation (one customer-user model).
- **New person:** `CustomerUserInvitation` with cryptographically random, single-use, time-limited, revocable,
  replay-protected token, **hash only** stored; email states company/account and role; acceptance starts IdP
  registration (OIDC) → validated Issuer + Subject → Principal + ExternalIdentity created/linked → binding activated →
  roles assigned → invitation accepted. No production password storage.
- **Existing person:** no new Principal; new binding + roles; information email ("You have been granted access to … as
  Viewer"). Existence is established only via a proven identity link, never by email match alone.
- **Role change:** SecurityAuditEvent + optional notification ("updated from Viewer to Buyer"); no tokens/internals.
- Requested roles are validated: no arbitrary role assignment, no escalation beyond the inviter's permissions.
- Email is not the global identity key (Issuer + Subject is); explicit, proven account linking only.
- Email delivery through a provider-independent notification boundary (e.g. `INotificationSender`) dispatched via the
  outbox; no provider chosen or referenced in Domain/Application.
- **RLS vs authorization:** RLS decides which rows of the active Tenant/Company/CustomerAccount are visible;
  permission-based authorization decides which actions the principal may perform in the active binding (e.g. VIEWER vs
  OWNER see the same rows; only OWNER may invite).
- Every Phase 1 endpoint declares its permission (policy-based authorization).

## 16. Security audit

`SecurityAuditEvent` is **tenant-scoped security data protected by RLS**.

- Fields (minimum): `tenant_id` (required), `company_id` (where relevant), actor `principal_id` (where available),
  `principal_type`, event/action, target/resource identifiers (where safe), UTC timestamp, `correlation_id`,
  security-outcome metadata.
- Runtime `commercial_platform_app`: **INSERT only**; no UPDATE/DELETE (grants + policies). Append-only from the
  application perspective.
- Authorized security/admin readers SELECT only within their permitted tenant/company scope.
- Owner/maintenance operational access is separate.
- No secrets, tokens or credential material in audit payloads.
- No automatic audit deletion in Phase 1. **Retention is configurable / policy-defined and deferred** as an
  operational/compliance policy.
- Events: authentication outcomes, scope/company switches, role/permission/grant changes, denied escalations,
  fingerprint mismatches, **invitation created / revoked / accepted, binding added / disabled, binding role added /
  removed, active customer binding switch (when security relevant), failed unauthorized invitation, failed role
  escalation**. Never raw invitation tokens.

## 17. IDOR / privilege protection (must be proven)

Cross-tenant, cross-company and cross-customer-account access; IDOR (by id); horizontal privilege escalation; role
escalation; scope tampering; TenantId / CompanyId / CustomerAccountId tampering (body, query, header, token hint);
principal-type escalation; runtime-role RLS bypass.

## 18. Outbox scope evolution

- Evaluate and add metadata as justified: `TenantId` (exists), `CompanyId`, `CustomerAccountId` (when relevant),
  originating principal type / system context, grant metadata only if a handler needs it.
- Dispatcher: outbox row → **new** SYSTEM `ExecutionScope` from metadata → handler → DB initializer → RLS. One
  isolated scope per message; no AsyncLocal leakage between messages/jobs (test).
- The outbox table stays outside business RLS (ADR-0001 §2.5) and inaccessible to Application/API (A006/A010/A013).
- WS2 contract (implemented): `OutboxScopeMetadata` — no metadata ⇒ SYSTEM infrastructure scope; tenant + company ⇒
  SYSTEM business scope; partial ⇒ fail closed (dead-letter). Until the company/account/binding columns are added,
  business-scoped enqueue is refused.

## 19. Hangfire

Infrastructure-level, outside business RLS; schema provisioned by the owner identity; runtime
`PrepareSchemaIfNecessary=false` after provisioning; Worker connects as `commercial_platform_app`.

## 20. Security test suite (real PostgreSQL via Testcontainers — no mocked isolation)

Each test is tagged `[Trait("ADR-0001", "<id>")]`. The **full list is the Phase 1 acceptance suite (Level B)**; the
core subset marked ★ is additionally enforced by A014 Level A as soon as isolation implementation exists.

| # | Test | Trait id |
|---|---|---|
| 1 | Tenant A cannot read Tenant B | `cross-tenant` ★ |
| 2 | Tenant A cannot insert/update/delete Tenant B | `cross-tenant-write` |
| 3 | Company A1 cannot read Company A2 | `cross-company` ★ |
| 4 | Customer X cannot read Customer Y | `cross-customer` ★ |
| 5 | Customer cannot gain tenant-wide scope | `customer-no-tenant-wide` |
| 6 | Missing scope ⇒ no rows / denied writes | `fail-closed` ★ |
| 7 | Partial scope fails closed | `partial-scope` |
| 8 | Invalid scope fails closed | `invalid-scope` |
| 9 | Raw SQL cannot bypass RLS | `raw-sql` |
| 10 | `IgnoreQueryFilters` cannot bypass RLS | `ignore-query-filters` |
| 11 | IDOR attempt fails (API, by id) | `idor` |
| 12 | Pooled X → Y → X has no leakage | `pool-scope-leak` ★ |
| 13 | Open-connection scope mismatch rejected | `fingerprint-mismatch` |
| 14 | Commit clears context safely | `commit-clears` |
| 15 | Rollback clears context safely | `rollback-clears` |
| 16 | SYSTEM without tenant/company cannot access business rows | `system-unscoped` |
| 17 | SYSTEM with explicit scope sees only that scope | `system-scoped` |
| 18 | Runtime role owns no tenant business tables | `runtime-role-not-owner` ★ |
| 19 | Runtime role has NOBYPASSRLS | `runtime-role-nobypassrls` |
| 20 | Each marked table has ENABLE + FORCE RLS | `rls-enabled-forced` |
| 21 | Required policies exist in `pg_policies` | `policies-present` |
| 22 | Tenant-aware FK prevents cross-tenant reference | `tenant-aware-fk` |
| 23 | Write stamping cannot move an entity cross-tenant | `stamping-no-move` |
| 24 | Role escalation denied | `role-escalation` |
| 25 | Horizontal privilege escalation denied | `horizontal-escalation` |

Additional acceptance tests: `bootstrap-own-bindings-only` (stage 1 cannot enumerate others; business tables empty),
`stage-transition` (business RLS only after stage 2), `active-company-unauthorized` (403, no DB access),
`customer-company-header-not-authority`, `audit-append-only` (app cannot UPDATE/DELETE), `audit-tenant-scoped`,
`oidc-test-issuer-flow`, `dev-auth-absent-outside-development`, `migration-identity-not-runtime`,
`hangfire-runtime-no-schema-create`, `outbox-scope-propagation`, `no-asynclocal-leak-between-jobs`.

Customer membership / invitation tests (approved 2026-10-07): `multi-binding-single-active` (a principal with
bindings in several companies and tenants sees only the active binding's rows), `foreign-binding-rejected` (another
principal's or a disabled/pending binding id ⇒ 403, no DB access), `binding-selection-required` (several bindings, no
selection ⇒ error, never merged), `binding-role-no-leak` (OWNER in binding 1 ≠ OWNER in binding 2),
`invite-scope-from-active-binding` (inviter cannot target another company/tenant/account), `invite-role-escalation`,
`invite-token-hash-only`, `invite-token-single-use`, `invite-expired`, `invite-revoked`, `invite-existing-principal-adds-binding`
(no duplicate principal), `email-not-identity-key` (matching email does not link identities), `role-change-audited`.

## 21. Architecture rules

- A001–A013, A015–A017: unchanged.
- **A014 (implemented 2026-10-07, Level A — implementation invariants, any phase):** triggered by the implementation
  itself, never by a phase number or a manual completion flag:
  - scoped entity exists ⇒ NOT NULL scope columns per markers + `ENABLE` + `FORCE` RLS + USING/WITH CHECK policy in
    migrations;
  - isolation implementation exists (scoped entity, RLS DDL, or a production `IDbSessionInitializer` type) ⇒ the
    registered initializer is not the no-op and the ★ core proofs exist;
  - always: no EF visibility query filters, `No Reset On Close` never enabled, RLS DDL only with ADR-0001 ACCEPTED,
    phase ≥ 1 requires ADR-0001 ACCEPTED.
- **Level B — Phase 1 acceptance:** the complete §20 suite and the §24 gate, proven by the Phase 1 acceptance run and
  the implementation report. **No permanent manual "phase complete" flag** is introduced.
- **A018:** remains phase-aware; Phase 1 allows only the Identity / Tenant / Security scope; Phase 2+ modules and
  Portal/SalesChannel/Integration modules stay forbidden. Data: `docs/phases/phase-1/phase-scope.phase1.json`,
  merged by the kickoff PR (kickoff exemption).

## 22. Contract tests

OpenAPI snapshot updated deliberately for Phase 1 endpoints; error envelope unchanged and extended to the new
401/403/409 cases (incl. unauthorized active company). Still **no** external provider contract tests.

## 23. Frontend (identity/security foundation only)

- Admin: current tenant/company context; active-company selector for authorized WORKFORCE (sends
  `X-Active-Company-Id`; server validates); identity/security administration foundation (principals, roles,
  permissions, grants, audit view) as required by Phase 1 endpoints; developer status page kept.
- B2B: authenticated shell, context display and the **account (binding) chooser / switcher** for principals with several
  bindings (sends the active binding selection; no merged views); customer user/role administration foundation for
  principals whose active binding grants it.
- Visual language: existing shared, Kumpara-aligned design tokens/components; A016/A017 unchanged.
- MUST NOT build Customer, Catalog, Product, Pricing, Quote, Order or Invoice screens.

## 24. Acceptance gate (no partial PASS)

**Build:** backend · admin · b2b — PASS.
**Tests:** unit · architecture · contract · integration · full isolation/security suite (§20) — PASS.
**Database:** owner/runtime split verified (local, shared dev, Testcontainers) · runtime owns no business tables ·
runtime NOBYPASSRLS · migrations run as owner · API/Worker run as app · Hangfire schema pre-provisioned.
**Isolation:** Cross Tenant · Cross Company · Cross Customer · IDOR · Horizontal Privilege Escalation · Role
Escalation · Scope Tampering · Raw SQL RLS · Pool Leakage — all PASS.
**Runtime:** authentication/context resolution (two-stage) · active-company selection · session initializer ·
fingerprint guard · audit append-only · outbox scope propagation — all work.
**CI:** local CI green; hosted CI governed by ADR-0002 (D1–D7 reported as deferred, never claimed PASS).

Any failing or missing item ⇒ **FAIL / INCOMPLETE**.

## 25. Explicitly forbidden in Phase 1

Customer commercial module (incl. `CustomerAccount` entity); Catalog/Product; Pricing; Inventory business logic; Quote;
SalesOrder; Credit/Risk commercial rules; Invoice; Shipment; CRM; Leads; Opportunity; Payment gateway; Kumpara, Logo,
Mikro, Nebim, Winner connectors; external ERP synchronization; e-Invoice/e-Archive; native mobile features; B2B Portal
/ Sales Channel module; ERP/external-connection module; vendor-specific identity types in core;
RabbitMQ/microservices/Kubernetes.

## 26. Implementation sequence

1. Kickoff PR (§2).
2. Migration/runtime identity separation; Hangfire provisioning switch (§6); three-role provisioning for local
   docker-compose and Testcontainers (§5).
3. DBA executes the reviewed role-split script on the shared dev DB; post-conditions verified.
4. ExecutionScope v2 + validation (§8).
5. RLS foundation migration: helper functions, GUC keys, conventions (§13).
6. `RlsDbSessionInitializer` (bootstrap + scoped modes), fingerprint interceptor, fail-fast guard, write stamping (§11, §12, §14).
7. Tenant, Company, identity directory, grants, customer binding, audit tables with RLS (§7, §9, §16).
8. OIDC authentication boundary, two-stage resolution, active-company selection, authorization (§9, §10, §15).
9. Outbox scope evolution (§18).
10. Phase 1 administration API + contract snapshot (§22).
11. Frontend (§23).
12. Full security/acceptance suite (§20).
13. Shared dev DB validation (migrate as owner, run as app, round trips).
14. Phase 1 implementation report (§27).

## 27. Completion report — required format

# PHASE 1 IMPLEMENTATION REPORT

1. Active Phase
2. Implemented
3. Database Role Separation
4. Tenant / Company Model
5. Identity / Principal Model
6. ExecutionScope
7. PostgreSQL RLS
8. Session Initializer / Fingerprint
9. Authorization / Permissions
10. Outbox Scope Propagation
11. Architecture Rules
12. Security / Isolation Tests
13. Contract Tests
14. Runtime Validation
15. Frontend
16. Migrations
17. Shared Development DB Validation
18. Deviations
19. Deferred Work
20. Final Gate: PASS / FAIL

## 28. Decisions recorded (2026-10-07)

| Topic | Decision |
|---|---|
| Identity provider | Not selected; OIDC/OAuth2 provider-independent boundary; issuer + subject minimum |
| Bootstrap | Two-stage context; bootstrap reads only own issuer+subject bindings; no SECURITY DEFINER |
| Active company | `X-Active-Company-Id` header = selection, never authorization |
| Local DB roles | Three-role model in local docker-compose too |
| Audit | Tenant-scoped, RLS-protected, append-only for runtime; retention policy-defined (deferred) |
| Customer binding | Minimal security seam only; no CustomerAccount commercial entity. **Updated 2026-10-07:** principal 1→N bindings across companies and tenants; exactly one active binding per request; binding-scoped roles; invitation model — see §15a and `docs/architecture/identity-and-customer-membership.md` |
| Active customer binding | Recommended `X-Active-Customer-Binding-Id` (opaque binding id, server resolves scope); transport confirmed at implementation |
| Global identity | Principal + ExternalIdentity are a global identity layer (no duplicate principals); memberships are tenant-scoped |
| A014 model | Level A implementation invariants (any phase) + Level B acceptance suite; no manual completion flag |

## 29. Implementation notes

| Workstream | State | Notes |
|---|---|---|
| 1 — Database identity separation + three-role foundation (§5, §6, §19) | **complete** (2026-10-07): Testcontainers, local docker and shared dev (DBA role split executed and verified; runtime/owner probes, provisioning, API/Worker, outbox round trip) | runtime/migration identities in `PostgresConnection`; `--provision-database`; Hangfire `PrepareSchemaIfNecessary=false`; `scripts/db/local/*.sql`; reviewed `scripts/db/shared-dev-role-split.sql` (dry-run tested); see `docs/architecture/database-identities.md` |
| 2 — ExecutionScope v2 + active context selection (§7–§10, §15, §18 contract) | **complete** (2026-10-07), no schema change | immutable kind-based `ExecutionScope` (WORKFORCE company / tenant-wide-with-grant, CUSTOMER binding, SYSTEM infrastructure / business), `ExecutionScopeValidator`, SHA-256 `ScopeFingerprint` (correlation excluded), `AuthenticatedIdentityContext` (stage 1), `ContextHeaders` + `ActiveContextRequest`, `IIdentityDirectory` + `ExecutionScopeResolver` (fail-closed `UnprovisionedIdentityDirectory` default), `Permission`/`PermissionSet`, six active-context error codes, scope/permission policies, `OutboxScopeMetadata` (business-scoped enqueue refused until outbox scope columns exist; partial metadata dead-lettered), `DbSessionContext` one-way mapping (not executed); dev auth reduced to stage-1 identity. See `docs/architecture/execution-scope.md`. |

> **ONE PRINCIPAL MAY HAVE MANY MEMBERSHIPS. ONE NORMAL REQUEST HAS ONE ACTIVE BUSINESS SCOPE.**
