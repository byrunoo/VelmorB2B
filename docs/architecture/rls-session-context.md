# PostgreSQL Session Context and Fingerprint Guard

**Status:** IMPLEMENTED — Phase 1 Workstream 3 (2026-10-07). **No production RLS schema** (tables, helper functions,
policies) exists yet: that is WS4. Depends on ADR-0001 §2.7–§2.8 and [`execution-scope.md`](execution-scope.md).

```
ExecutionScope (immutable, validated)
  → DbSessionContext.FromScope            one-way mapping, all 9 keys, '' = not applicable
  → RlsDbSessionInitializer               WRITER   — every connection open
  → PostgreSQL session (custom GUCs)      read back: commercial.scope_fingerprint
  → DbSessionScopeGuard                   VERIFIER — before every command
```

**Initializer = writer. Fingerprint guard = verifier.** The guard never writes or corrects; the writer never verifies
commands.

## 1. Keys

| Key | Value |
|---|---|
| `commercial.scope_kind` | `ExecutionScopeKind` name |
| `commercial.principal_type` | `WORKFORCE` / `CUSTOMER` / `SYSTEM` / `ANONYMOUS` |
| `commercial.principal_id` | GUID or '' |
| `commercial.tenant_id` | GUID or '' (business scopes only) |
| `commercial.company_id` | GUID or '' (business scopes only; '' for tenant-wide WORKFORCE) |
| `commercial.customer_account_id` | GUID or '' (CUSTOMER / SYSTEM with customer metadata) |
| `commercial.customer_binding_id` | GUID or '' |
| `commercial.tenant_wide_company_access` | `true` only for `WorkforceTenant` with the explicit grant; otherwise `false` |
| `commercial.scope_fingerprint` | `ExecutionScope.Fingerprint` (always set) |

### Fail-closed empty / missing semantics (contract for WS4 policies)

`NULL`, `''` and a missing setting (`current_setting(key, true)` → NULL) all mean **scope unavailable ⇒ no business
rows**. There is no wildcard: a missing company never means "all companies", a missing tenant never means "all
tenants", SYSTEM never means unrestricted. Tenant-wide access exists only as `tenant_wide_company_access = 'true'`,
which the writer sets only for WORKFORCE with `ScopeGrants.TenantWideCompanyAccess`; CUSTOMER and SYSTEM always
write `false`. WS4 helper functions must be `STABLE SECURITY INVOKER` and use `nullif(current_setting(k, true), '')`.

## 2. Writer — `RlsDbSessionInitializer`

- **Call:** one parameterized statement `SELECT set_config(@k0, @v0, false), …, set_config(@k8, @v8, false)`; key
  names and values are parameters (no concatenation, no dynamic `SET`).
- **Why `set_config(…, false)` (session level):** the ADR spike showed transaction-local settings (`true` /
  `SET LOCAL`) disappear between autocommit statements, so EF queries outside an explicit transaction would run without
  scope. The write happens right after open, outside any transaction (autocommit), so it is committed immediately and a
  later ROLLBACK cannot revert it.
- **All keys, every open:** values are never "only changed keys"; not applicable = `''`. A pooled physical connection
  therefore never keeps the previous user's values — independent of, and in addition to, Npgsql's pool reset
  (`DISCARD ALL` on reuse), which must stay enabled.
- **Fail closed before SQL:** no scope, a scope that did not pass the validator (e.g. built without the factories),
  missing fingerprint, incomplete mapping, or `No Reset On Close` on the connection ⇒ `DbSessionScopeException`
  `DB_SESSION_INITIALIZATION_FAILED`.
- **Fail closed after SQL:** every value returned by `set_config` must equal the intended one.
- **Logging:** Debug — scope kind, principal type, fingerprint ShortId, correlation id. Never tokens, secrets or
  permissions.

## 3. Lifecycle

`DbSessionInitializerInterceptor` (EF `DbConnectionInterceptor`) runs on **every** `ConnectionOpened` (sync + async)
of a `CommercialDbContext` connection:

1. forget any previous record for the connection object;
2. call the writer with the CURRENT ambient scope;
3. read `commercial.scope_fingerprint` back from PostgreSQL and record it (server value, not the writer's claim);
4. on any failure: close the connection, keep no record, rethrow.

`ConnectionClosed` drops the record. Consequences:

| Path | Behaviour |
|---|---|
| EF query / SaveChanges (EF opens and closes per operation) | each open re-initializes under the current scope |
| `Database.OpenConnection[Async]` (manual, sync or async) | initialized |
| `BeginTransaction` | opens (initialized, autocommit) then `BEGIN` |
| Execution strategy retry / reopen / broken connection | every reopen re-initializes; a dead pooled connector fails initialization (fail closed) and is discarded by Npgsql |
| `DbContext` construction | nothing happens until a connection opens |
| `GetDbConnection().Open()` directly (bypasses EF) | **not initialized ⇒ every command rejected** (`DB_SESSION_NOT_INITIALIZED`) |
| Raw SQL through EF (`ExecuteSql*`, `SqlQuery*`) | intercepted like LINQ |
| Raw ADO `connection.CreateCommand()` | only in allow-listed files (architecture rule); the outbox store verifies each command explicitly |

Cost: one `set_config` round trip plus one read-back per physical open (pool reset is folded into the first by
Npgsql). Accepted for correctness; an optimization (fold the read-back into the write statement) is possible later
without changing the contract.

## 4. Verifier — `DbSessionScopeGuard` / `DbSessionScopeGuardInterceptor`

EF `DbCommandInterceptor` on Reader / NonQuery / Scalar (sync + async). Before execution:

1. SQL that changes session state is rejected — any `set_config(`, `SET [SESSION|LOCAL] commercial.*`,
   `RESET commercial.*|ALL|ROLE`, `SET ROLE`, `SET SESSION AUTHORIZATION`, `DISCARD …` ⇒
   `DB_SESSION_KEY_WRITE_FORBIDDEN` (only the writer sets keys; it uses its own raw command, which is not intercepted);
2. the connection must be open and have a recorded server fingerprint ⇒ else `DB_SESSION_NOT_INITIALIZED`;
3. recorded fingerprint must equal the ambient `ExecutionScope.Fingerprint` ⇒ else `DB_SESSION_SCOPE_MISMATCH`.

The guard never switches the session in place (no `set_config` inside an open connection or transaction). Recovery is
explicit: close and reopen under the current scope, or use a fresh DbContext. Two scopes with the same security
context (same fingerprint, different correlation id) are compatible by design.

`DbSessionScopeException` is internal: the API's global handler answers a generic 500 `INTERNAL_ERROR` (no session
internals leak); the code and both fingerprint ShortIds are logged (Warning) for future security audit.

## 5. Infrastructure vs business SQL

The guard does **not** classify SQL and has **no bypass**: every command on a `CommercialDbContext` is verified.
Infrastructure work runs under explicit non-business scopes, whose context carries empty business keys:

| Path | Scope / connection | Why safe |
|---|---|---|
| Outbox claim / mark (Hangfire job, no ambient) | `Unscoped` (Anonymous) | infrastructure table outside business RLS; raw commands guard-verified |
| Outbox message processing | fresh `SystemInfrastructure` / `SystemBusiness` from metadata (WS2) | DbContexts created inside the message scope |
| HTTP requests | scope from `ExecutionScopeMiddleware` | DbContexts are created per request inside it |
| Health check (`AddDbContextCheck` → `CanConnect`) | request scope = Anonymous | no fake tenant scope, no grant |
| Diagnostic ping (Phase 0) | request / message scope | infrastructure tables |
| Migrations / provisioning / design-time | separate owner-identity context **without** these interceptors (`ConfigureMigrationContext`) | owner tooling, never runtime; no session context needed |
| Hangfire storage | Hangfire's own connections (runtime role, `hangfire` schema) | never touches business tables; with no keys set, future business RLS denies |

## 6. Pooling

Pooling stays enabled; `No Reset On Close` is rejected by the configuration resolver (`PostgresConnection.Resolve`),
by the writer at runtime, and by A014 in source. Proven: tenant A → pool → tenant B on the same backend PID keeps no A
value; customer binding X → Y; business → non-business clears business keys; commit / rollback then reuse.

## 7. Tests

- `tests/Commercial.IntegrationTests/Persistence/RlsSessionContextTests.cs` — the 25 WS3 cases (keys per scope kind,
  pool reuse, fail closed, guard, transactions, lifecycle) against Testcontainers PostgreSQL as
  `commercial_platform_app`.
- `RlsSessionPolicyProofTests.cs` — a **test-only** table, helper function and policy (created per throw-away database
  by the owner identity, seeded by the bootstrap superuser) prove that the real writer's context drives RLS:
  cross-tenant, cross-company, cross-customer (+ WITH CHECK), fail-closed (incl. a session with no keys at all),
  pool-scope-leak, runtime-role-not-owner. These satisfy A014 Level A core proofs.
- Architecture: `DbSessionContextRules` + A014.
