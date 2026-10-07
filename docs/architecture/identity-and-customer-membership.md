# Identity, Customer Membership and Invitations (Phase 1 design — approved 2026-10-07)

**Status:** APPROVED DESIGN (human decision 2026-10-07) — not yet implemented.
**Normative for:** Phase 1 mandate §7–§10, §15–§16, §20 and §30. Depends on ADR-0001 (RLS-primary hybrid; unchanged).

> **Revoked statement:** "A CUSTOMER principal belongs to one company / is never multi-company."
> **Replacement:** *A CUSTOMER Principal may have multiple CustomerPrincipalBindings across multiple Companies and
> multiple Tenants. A normal request executes under exactly one active CustomerPrincipalBinding.*

---

## 1. Two layers

```text
GLOBAL IDENTITY LAYER (not tenant-owned; tightly restricted visibility)
  Principal                 one per human (WORKFORCE or CUSTOMER), never duplicated per company/tenant
  PrincipalExternalIdentity Issuer + Subject → Principal (authoritative authenticated identity)

TENANT / CUSTOMER MEMBERSHIP LAYER (tenant-scoped, RLS-protected)
  CustomerPrincipalBinding        Principal ↔ (TenantId, CompanyId, CustomerAccountId)   — one membership
  CustomerBindingRoleAssignment   CustomerPrincipalBinding ↔ Role                         — roles per membership
  CustomerUserInvitation          pending membership offer (hash-only token)
  CompanyAccessGrant / PrincipalRoleAssignment   WORKFORCE authorization (unchanged)
  SecurityAuditEvent              tenant-scoped audit
```

Example — one Principal (`user@example.com`), four memberships, four independent role sets:

```text
Tenant A ─ Company A1 ─ CustomerAccount A1-100 → binding #1 → OWNER
         └ Company A2 ─ CustomerAccount A2-450 → binding #2 → VIEWER
Tenant B ─ Company B1 ─ CustomerAccount B1-20  → binding #3 → BUYER
         └ Company B3 ─ CustomerAccount B3-875 → binding #4 → BUYER + FINANCE
```

Cardinalities: Principal 1 → N bindings; CustomerAccount (opaque security reference in Phase 1) 1 → N bindings;
binding 1 → N role assignments. No 1:1 assumption anywhere.

### Global identity visibility (no weakening of tenant isolation)

`Principal` / `PrincipalExternalIdentity` are not tenant business rows, so they cannot simply carry a `tenant_id`
policy. Their visibility is restricted by dedicated RLS policies (no `SECURITY DEFINER` shortcut):

- **bootstrap mode** (stage 1, §4): a caller sees only the identity rows of its own validated Issuer + Subject and
  its own bindings — nothing else, no enumeration;
- **scoped mode** (stage 2): administrators see only the minimal identity data of principals that hold a membership /
  grant in the **active** tenant (and company / customer account as the active scope allows);
- no business table becomes visible through the identity layer.

## 2. CustomerPrincipalBinding

| Field | Notes |
|---|---|
| `Id` | opaque id; also the client-facing active-context handle (§5) |
| `PrincipalId` | global principal |
| `TenantId`, `CompanyId` | NOT NULL; RLS scope |
| `CustomerAccountId` | NOT NULL; **opaque security reference only** in Phase 1 (no CustomerAccount entity/table) |
| `Status` | Pending / Active / Disabled |
| `CreatedAtUtc`, `UpdatedAtUtc` | UTC; concurrency token |

Uniqueness: at most one non-disabled binding per (Principal, Tenant, Company, CustomerAccount).

## 3. Binding-scoped roles (permission-based)

- Roles are **not** global principal attributes. `Principal → GlobalRole` is forbidden.
- `CustomerBindingRoleAssignment (CustomerPrincipalBindingId, RoleId)`; a binding may hold several roles.
- Roles are bundles of permissions; application code checks **permissions**, never role names.
- Initial customer roles (seed concepts): `OWNER`, `BUYER`, `VIEWER`, `FINANCE`. Phase 1 permissions are
  identity/security only, e.g. `customer.users.read`, `customer.users.invite`, `customer.users.manage`,
  `customer.roles.assign`. BUYER/VIEWER/FINANCE business permissions arrive with their phases.
- No permission from one binding can apply to another binding (the request has exactly one active binding).
- Assignable roles are bounded: an inviter may only grant roles that are allowed for its binding and do not exceed
  its own permissions (no role escalation).

## 4. Two-stage context with many bindings

1. **Stage 1 — authenticated identity:** Issuer, Subject, PrincipalType claim, CorrelationId from the validated token.
   The bootstrap policy lets the caller read only its own Principal, ExternalIdentity and bindings (+ role
   assignments) — e.g. to show "choose an account".
2. **Stage 2 — execution scope:** the requested binding is verified to belong to the Principal and be Active; Tenant,
   Company and CustomerAccount are read **from the binding row**; permissions are computed from that binding's roles.
   `ExecutionScope` = `TenantId, CompanyId, CustomerAccountId, ActiveCustomerBindingId, PrincipalType=CUSTOMER,
   PrincipalId, Permissions, CorrelationId, ScopeFingerprint`. Only now may business RLS access happen.

Rows from several bindings are **never** merged into one ordinary request.

## 5. Active customer binding selection — recommendation

| Option | Description | Pros | Cons |
|---|---|---|---|
| **A. Header `X-Active-Customer-Binding-Id` (recommended)** | client sends the opaque binding id on each request; server validates it belongs to the authenticated Principal and is Active, then resolves scope from the DB | stateless; works for API clients, multiple browser tabs on different accounts, SPA + mobile; symmetrical with WORKFORCE `X-Active-Company-Id`; easy to test; no server session store | header must be sent on every request (shared API client handles it) |
| B. Server-side session selection | server stores "current binding" per session/token | client sends nothing extra | stateful; tabs interfere (switching in one tab changes another); needs session storage + invalidation; harder for API clients |
| C. Re-issue token per binding | IdP / token exchange issues a binding-specific token | scope in token | couples to IdP features; token refresh complexity; violates "token is not authority for tenant scope" spirit |

**Recommendation: Option A.** Rules:
- The client never sends TenantId / CompanyId / CustomerAccountId as authority — only the binding id.
- Unknown, foreign, disabled or pending binding ⇒ 403 (standard envelope), no scoped DB access.
- CUSTOMER requests ignore/forbid `X-Active-Company-Id` (if present and inconsistent ⇒ 403).
- No header and exactly one active binding ⇒ that binding MAY be selected implicitly (convenience); several bindings
  and no header ⇒ 409/400 "binding selection required" — never a merged context.
- Switching is explicit (the client changes the header); security-relevant switches are audited.

## 6. Customer-side user management

Authorization always uses the **active** binding:

```text
Principal P: A1/CA-100 → OWNER   ;   A2/CA-200 → VIEWER
Active binding A1/CA-100  → may invite/manage users of A1/CA-100
Active binding A2/CA-200  → may NOT invite/manage anyone
P can never manage users of A2/CA-200 while acting in A1/CA-100 (and vice versa)
```

Target scope of any invitation / role change is taken from the active binding server-side — never from request
input. WORKFORCE users with security permissions use the **same** binding/invitation foundation for the companies they
are authorized for (no separate customer-user model).

## 7. Invitation flows

### New person (no Principal resolves)

```text
inviter (active binding or workforce scope, permission customer.users.invite)
 → CustomerUserInvitation {TargetTenantId, TargetCompanyId, TargetCustomerAccountId from server context,
                           InvitedEmail, RequestedRoleAssignments (validated, no escalation),
                           InvitedByPrincipalId, CreatedAtUtc, ExpiresAtUtc, Status=Pending, TokenHash}
 → email: secure link "You have been invited to <Company / account> as <Role>."
 → link → validate (hash match, Pending, not expired, not revoked) → start IdP registration (OIDC)
 → IdP returns validated Issuer + Subject
 → Principal + ExternalIdentity created (or explicitly linked, §8) → binding Active → roles assigned
 → invitation Accepted (single use)
```

Token: cryptographically random (≥ 256 bits), only its **hash** stored (e.g. SHA-256), single use, time limited,
revocable, replay protected (status transition is atomic). The platform never stores production passwords —
credentials are created at the Identity Provider.

### Existing person (Principal already resolves)

No new Principal. Create a new binding for the server-determined Tenant/Company/CustomerAccount, assign the
requested binding roles and send an **information** email: "You have been granted access to <Company / account>
with the <Role> role." The person signs in with the existing identity.

How "existing" is determined: only through an established identity link (Issuer + Subject) — or, when only an email
is known, by sending an invitation the person must accept while signed in (which proves control of that identity).
Email alone never auto-merges identities.

### Role change

Changing binding roles (e.g. VIEWER → BUYER) creates a SecurityAuditEvent and an optional security notification:
"Your access for <Company> has been updated from Viewer to Buyer." No tokens or security internals in emails.

### Revocation

Revoking an invitation (status Revoked, token unusable) is audited; an optional notification may be sent.

## 8. Identity and email rules

- Authoritative identity = **Issuer + Subject**. Email is for invitations, communication and candidate matching only.
- Normalized email is never the immutable authentication key.
- Account linking is explicit and proven by the identity flow; unrelated external identities are never merged because
  their emails match.

## 9. RLS vs authorization

| Question | Answered by |
|---|---|
| Which rows are visible in the active Tenant / Company / CustomerAccount? | PostgreSQL RLS (ADR-0001) |
| Which actions may this Principal perform within the active binding? | permission-based authorization (binding roles) |

Example: VIEWER and OWNER of the same binding see the same rows (same RLS scope); only OWNER may invite users or
assign roles. Action authorization is never encoded only in RLS. RLS still enforces that writes stay inside the active
scope (WITH CHECK).

## 10. Security audit (minimum)

invitation created · invitation revoked · invitation accepted · binding added · binding disabled · role added ·
role removed · active membership switch (when security relevant) · failed unauthorized invitation · failed role
escalation. Never raw tokens, secrets or credentials in audit payloads.

## 11. Email delivery boundary

Provider-independent abstraction in Application (e.g. `INotificationSender` / `IEmailSender` with message intents:
`CustomerInvitation`, `CustomerMembershipGranted`, `CustomerRoleChanged`, `CustomerInvitationRevoked`); delivery via
the transactional outbox (idempotent handler). No provider (SendGrid, SES, …) is chosen; no provider type in
Domain/Application.

## 12. Attack surface to be proven (Phase 1 tests)

IDOR on binding / invitation ids · scope tampering (tenant/company/account in body/query/header) · invitation token
replay · expired / revoked invitation · role escalation and arbitrary role assignment · cross-tenant, cross-company and
cross-customer invitations · binding of another principal used as active binding · role leakage between bindings ·
merged multi-binding context.

## 13. Implementation guidance (A018-compatible)

- Namespaces: `…Identity`, `…Membership`, `…Security`, `…Tenancy`, `…Isolation`. The Phase 1 A018 data still forbids
  the `Customers` module segment and the `CustomerAccount` type (commercial entity, Phase 2) — the membership types
  (`CustomerPrincipalBinding`, `CustomerBindingRoleAssignment`, `CustomerUserInvitation`) are allowed (asserted by
  `A018_Phase1_security_concepts_are_allowed_by_phase1_scope`).
- Frontend: the `accounts` path segment remains forbidden in Phase 1; use e.g. `membership/` or `context/` for the B2B
  binding chooser and `security/` / `users/` for user administration screens.
- A014 Level A applies as soon as the first membership table exists: NOT NULL scope columns, ENABLE + FORCE RLS,
  USING / WITH CHECK policies, production session initializer and the core isolation proofs in the same change.
