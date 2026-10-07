# Execution Scope v2 and Active Context Selection

**Status:** IMPLEMENTED — Phase 1 Workstream 2 (2026-10-07). No database schema, no RLS policy, no identity tables yet.
**Depends on:** ADR-0001 (RLS-primary hybrid), `identity-and-customer-membership.md` (approved membership model).

> ## ONE PRINCIPAL MAY HAVE MANY MEMBERSHIPS. ONE NORMAL REQUEST HAS ONE ACTIVE BUSINESS SCOPE.
>
> A principal can be WORKFORCE in several companies/tenants, or CUSTOMER through several bindings across companies
> and tenants. A request never merges them: it executes under exactly one active company (WORKFORCE) or exactly one
> active customer binding (CUSTOMER), or under no business scope at all.

## 1. Two-stage model

| Stage | Type | Built from | Grants business access? |
|---|---|---|---|
| 1 — authenticated identity | `AuthenticatedIdentityContext` (Issuer, Subject, PrincipalType, CorrelationId) | validated authentication claims `iss`, `sub`, `principal_type` only | **no** |
| 2 — execution scope | `ExecutionScope` (immutable) | `IExecutionScopeResolver` over `IIdentityDirectory` (server-side memberships) + selection headers | only when `HasBusinessScope` |

- Claims never carry tenant / company / customer-account / binding values (`CommercialClaimTypes` has none; A-rule).
- `PrincipalId` is never inferred from the token: it comes from the directory lookup by **Issuer + Subject**
  (email is not an identity key). An unknown principal resolves to an identity-only scope with `PrincipalId = null`.
- `TenantId` is never inferred from `PrincipalId`: it comes from the selected company / binding row.
- HTTP callers can never be SYSTEM; `AuthenticatedIdentityContext` rejects SYSTEM and Anonymous.

## 2. ExecutionScope v2

`src/Commercial.Application/Abstractions/Scope/ExecutionScope.cs` — sealed class, private constructor, no setters,
no `with`; every instance passes `ExecutionScopeValidator` at construction (fail closed with
`InvalidExecutionScopeException`, code `EXECUTION_SCOPE_INVALID`) and gets a `ScopeFingerprint`.

Fields: `Kind`, `PrincipalType`, `PrincipalId?`, `TenantId?`, `ActiveCompanyId?`, `CustomerAccountId?`,
`ActiveCustomerBindingId?`, `Permissions` (`PermissionSet`), `Grants` (`ScopeGrants`), `CorrelationId`, `CausationId?`,
`Fingerprint`. Strongly typed ids: `TenantId`, `CompanyId`, `CustomerAccountId`, `PrincipalId`, `CustomerBindingId`
(`src/Commercial.Domain/Identity/ScopeIds.cs`).

| Kind | Factory | Required | Forbidden | Business scope |
|---|---|---|---|---|
| Anonymous | `Anonymous` | correlation | everything else | no |
| AuthenticatedIdentity | `ForIdentity` | WORKFORCE/CUSTOMER type | tenant, company, account, binding, permissions, grants | no |
| WorkforceCompany | `WorkforceCompany` | principal, tenant, company | account, binding, grants | yes |
| WorkforceTenant | `WorkforceTenant` | principal, tenant, **explicit `TenantWideCompanyAccess` grant** | company, account, binding | yes |
| CustomerBinding | `CustomerBinding` | principal, tenant, company, account, binding (all from ONE binding row) | grants | yes |
| SystemInfrastructure | `SystemInfrastructure` | correlation | principal, business ids, permissions | no |
| SystemBusiness | `SystemBusiness` | tenant, company (binding ⇒ account) | principal, permissions, grants | yes |

Rules:
- No generic ADMIN principal type; maintenance (BYPASSRLS tooling identity) is never a scope principal.
- "Company missing" never means "all companies": tenant-level WORKFORCE scope exists only with the explicit grant.
- CUSTOMER can never carry a grant (no factory parameter; validator; architecture rule).
- CUSTOMER permissions come **only** from the active binding: `CustomerBinding(principal, ResolvedCustomerBinding, …)`
  takes the binding record including its permissions; there is no separate permission parameter, so OWNER in binding
  A never leaks into binding B (no union).
- Changing company or binding = a new scope (new fingerprint); the old one is never mutated.

## 3. Permissions

`Permission` is a validated dotted code (`customer.users.manage`), `PermissionSet` an immutable ordinal set,
`scope.HasPermission(p)` the only check. Roles (OWNER, BUYER, VIEWER, FINANCE, …) are permission bundles resolved
before the scope is built; code never compares role names. Phase 1 catalogue (`SecurityPermissions`): tenant / company /
identity / security administration, security audit read, customer user read / invite / manage, customer role assign.
No Product / Order / Invoice permissions.

## 4. Scope fingerprint

`ScopeFingerprint` = `"sfp1-" + hex(SHA-256(canonical JSON))`. Canonical input, fixed order:
`v, kind, principalType, principalId, tenantId, companyId, customerAccountId, customerBindingId, grants,
permissions[] (ordinal sorted)`. Structured JSON avoids concatenation ambiguity.

- **Excluded (decision):** CorrelationId / CausationId — they do not change authorization; two requests with the same
  security context share a fingerprint.
- Never included: tokens, passwords, secrets, e-mail or other non-authorization PII.
- `ShortId` (12 hex) is safe for logs; the outbox dispatcher adds it to its log scope.

## 5. HTTP active-context contract

| Header | Meaning | Who |
|---|---|---|
| `X-Active-Company-Id` | **selection hint** for the company to act in — never authorization | WORKFORCE; CUSTOMER only as a consistency check against the active binding |
| `X-Active-Customer-Binding-Id` | **selection hint**: the caller's own binding (opaque GUID) | CUSTOMER |

Constants: `ContextHeaders` (only place the literals may appear in `src`). There is deliberately **no**
`X-Tenant-Id`, `X-Company-Id`, `X-Customer-Account-Id` or `X-Dev-Tenant-Id` header — an architecture rule fails if one
appears in `src` or `frontend`. Both headers are documented as optional parameters on every authorized operation in
the OpenAPI document.

Parsing (`ActiveContextRequest.Parse`) is syntax only and separate from authorization: canonical GUID ("D" format),
single value, not the empty GUID. Malformed binding header ⇒ 400 `ACTIVE_CUSTOMER_BINDING_INVALID`; malformed company
header ⇒ 400 `BAD_REQUEST`.

Pipeline (`src/Commercial.Api/Program.cs`): `UseAuthentication` → `ExecutionScopeMiddleware` (stage 1 → parse →
`IExecutionScopeResolver` → `accessor.Begin(scope)`) → `UseAuthorization`. A rejected selection never reaches the
endpoint. A **pending** selection (company / binding not chosen) leaves an identity-only scope: identity endpoints
(`/me`) work, endpoints that need a business scope fail with the pending code.

### Resolution rules

**WORKFORCE**
- Company header ⇒ company must exist, be active, and the principal must hold a membership in it **or** an explicit
  tenant-wide grant in that company's tenant ⇒ `WorkforceCompany` with permissions from those memberships only.
  Otherwise 403 `ACTIVE_COMPANY_FORBIDDEN` (unknown and foreign companies answer identically).
- No header ⇒ tenant-level `WorkforceTenant` scope only if the principal has an explicit tenant-wide grant and all its
  memberships are in exactly one tenant; otherwise identity-only. Either way company-scoped endpoints answer 400
  `ACTIVE_COMPANY_REQUIRED`.
- Binding header ⇒ 403 `ACTIVE_CUSTOMER_BINDING_FORBIDDEN`.

**CUSTOMER**
- Only the caller's own bindings are ever loaded (`GetCustomerBindingsAsync(principalId)`); only `Active` ones count.
- Binding header ⇒ must be one of the caller's active bindings; foreign, nonexistent, invited, suspended or revoked
  ⇒ identical 403 `ACTIVE_CUSTOMER_BINDING_FORBIDDEN` (same code, same message, no ids echoed).
- No header: 0 active ⇒ unavailable (403 `ACTIVE_CUSTOMER_BINDING_FORBIDDEN` on customer endpoints); 1 ⇒ implicit
  selection; >1 ⇒ 400 `ACTIVE_CUSTOMER_BINDING_REQUIRED` on customer endpoints (never "pick the first", never merge).
- Company header present and ≠ binding company ⇒ 403 `ACTIVE_COMPANY_FORBIDDEN`.

**Both**
- Unknown principal + any selection header ⇒ 403 (non-disclosing).
- Directory principal type ≠ token principal type, or inactive principal ⇒ 403 `EXECUTION_SCOPE_INVALID`.
- Authenticated but missing issuer / subject / type ⇒ 403 `EXECUTION_SCOPE_INVALID`.

### Error codes (standard error envelope)

| Code | Status | When |
|---|---|---|
| `ACTIVE_COMPANY_REQUIRED` | 400 | company-scoped endpoint without an active company |
| `ACTIVE_COMPANY_FORBIDDEN` | 403 | company not available to the caller / inconsistent with the binding |
| `ACTIVE_CUSTOMER_BINDING_REQUIRED` | 400 | several active bindings, none selected |
| `ACTIVE_CUSTOMER_BINDING_FORBIDDEN` | 403 | binding not the caller's / not active; no active binding at all |
| `ACTIVE_CUSTOMER_BINDING_INVALID` | 400 | malformed binding header |
| `EXECUTION_SCOPE_INVALID` | 403 | no valid scope can be established for the authenticated caller |

Authorization policies (`CommercialPolicies`): `Authenticated`, `Workforce` (identity type only), `Customer` (CUSTOMER
+ one resolved binding), `ActiveCompany` (exactly one active company), `CustomerUsersManage` (binding scope +
`customer.users.manage` of the ACTIVE binding — probe for binding-scoped permissions). Probe endpoints:
`/api/v1/system/access-probe/{workforce,customer,active-company,customer-users-manage}`; `/api/v1/system/me` returns the
resolved scope plus `pendingSelection`.

## 6. Identity directory port

`IIdentityDirectory` (Application, provider independent): `FindPrincipalAsync(issuer, subject)`,
`FindCompanyAsync(companyId)`, `GetWorkforceMembershipsAsync(principalId)`, `GetCustomerBindingsAsync(principalId)`.

- Production default (Infrastructure): `UnprovisionedIdentityDirectory` — resolves nobody, grants nothing (fail
  closed) until the identity tables exist. Registered with `TryAdd` so the DB-backed implementation replaces it.
- Test fake: `tests/Commercial.UnitTests/Security/InMemoryIdentityDirectory.cs` (linked into the contract tests).
  An architecture rule forbids fake / in-memory / dev directories in production assemblies.
- No dev-only resolver exists. Development authentication (`X-Dev-Principal-Type`, `X-Dev-Subject`, issuer
  `urn:commercial:development`, Development + explicit opt-in only) produces **stage-1 identity only**; the former
  `X-Dev-Tenant-Id` / `X-Dev-Company-Id` / `X-Dev-Customer-Account-Id` / `X-Dev-Sales-Channel-Id` headers were removed.

## 7. AsyncLocal safety

`AmbientExecutionScopeAccessor` (AsyncLocal) — `Begin` returns a disposable that restores the previous value. Proven
by unit tests: nested restore, restore on exception, 50 parallel flows with different business scopes (including
thread-pool hops) never observe each other's scope, ambient returns to `Unscoped`.

## 8. Background work (outbox / jobs)

`OutboxScopeMetadata` (Infrastructure): consumers build a **fresh** scope from persisted metadata only — never the
producer's ambient scope, never an unrestricted SYSTEM fallback.

| Persisted metadata | Consumer scope |
|---|---|
| none | `SystemInfrastructure` (no business access) |
| tenant + company (+ account / binding) | `SystemBusiness` |
| partial (e.g. tenant without company) | invalid ⇒ message dead-lettered immediately with `EXECUTION_SCOPE_INVALID`, handler never runs |

The outbox table today has only `tenant_id` (no migration in WS2). Therefore `EfOutbox` **refuses** to enqueue from a
business scope (`InvalidExecutionScopeException`) rather than persisting partial scope; the company / account /
binding columns are added together with the first business-scoped producer (later Phase 1 workstream, with RLS).
Hangfire jobs follow the same contract when they start carrying business work.

## 9. Database session context (Phase 1 WS3 — implemented; no production RLS schema yet)

Full design: [`rls-session-context.md`](rls-session-context.md).

`ExecutionScope` → `DbSessionContext.FromScope` → `RlsDbSessionInitializer` (writer, every connection open,
session-level `set_config(key, value, false)`, all keys, '' = not applicable) → PostgreSQL session →
`DbSessionScopeGuard` (verifier, before every command: ambient fingerprint = server-confirmed session fingerprint,
else `DB_SESSION_SCOPE_MISMATCH`).

Keys: `commercial.scope_kind, commercial.principal_type, commercial.principal_id, commercial.tenant_id,
commercial.company_id, commercial.customer_account_id, commercial.customer_binding_id,
commercial.tenant_wide_company_access, commercial.scope_fingerprint`. Non-business scopes write empty business keys.
Direction is scope → database only. Production RLS tables / helper functions / policies are WS4.

## 10. Guardrails (architecture tests, `ExecutionScopeRules` + A014)

- ExecutionScope immutable, sealed, no public constructor, no setters / init, no `with` clone.
- PrincipalType = Anonymous / Workforce / Customer / System only (no admin / maintenance / bypass).
- No business-scope claims; `AuthenticatedIdentityContext` = Issuer, Subject, PrincipalType, CorrelationId.
- CUSTOMER factory accepts neither grants nor a separate permission set; only `WorkforceTenant` takes grants.
- No `X-Tenant-Id` / `X-Company-Id` / `X-Customer-Account-Id` / `X-Dev-*-Id` scope headers in `src` or `frontend`.
- Selection header literals only in `ContextHeaders`.
- Customer-binding types only in Identity / Security / Scope namespaces (the commercial CustomerAccount stays forbidden
  by A018).
- No fake / in-memory / dev identity directory in production assemblies.
- A014 scope key list updated to v2; no EF query filters (unchanged).
