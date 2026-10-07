# Database Identities and Setup (ADR-0001 three-role model)

Phase 1 workstream 1. Applies to local docker, shared development, Testcontainers and production alike.

## 1. Identities

| Identity | Role | Used by | Can | Cannot |
|---|---|---|---|---|
| **Runtime** | `commercial_platform_app` | API, Worker (normal runtime) | CONNECT, USAGE on `commercial`/`hangfire`, SELECT/INSERT/UPDATE/DELETE (grants + owner default privileges), read migration history | create/alter/drop anything, own anything, write migration history, bypass RLS (NOBYPASSRLS), act as owner/maintenance |
| **Migration** | `commercial_platform_owner` | `dotnet ef`, `Commercial.Worker --provision-database`, local Development startup provisioning, Testcontainers fixture | own the database, both schemas and all objects; run EF migrations; install/upgrade the Hangfire schema; create RLS policies and helper functions (later) | be used by the API/Worker runtime |
| **Maintenance** | `commercial_platform_maintenance` | controlled operations only (BYPASSRLS) | DML on `commercial` for support | appear in any application configuration (enforced by architecture test `DatabaseIdentityRules`) |
| DBA (bootstrap) | superuser | role creation / ownership transfer scripts only | everything | be used by tooling other than `role-split` / `set-role-passwords` |

## 2. Configuration (single resolver: `PostgresConnection.Resolve`)

```text
ConnectionStrings:Commercial                    base (host/port/database/runtime username)
Commercial:Postgres:Host|Port|Database          common overlay (all identities)
Commercial:Postgres:Runtime:Username|Password   runtime identity        (env COMMERCIAL__POSTGRES__RUNTIME__*)
Commercial:Postgres:Migration:Username|Password migration identity     (env COMMERCIAL__POSTGRES__MIGRATION__*)
```

Rules enforced by the resolver (unit-tested):

- runtime never resolves `commercial_platform_owner`, `commercial_platform_maintenance` or the configured migration
  username, and never reads migration settings;
- migration requires an explicit `Migration:Username` (no fallback), never inherits the runtime password, and refuses
  the runtime / maintenance role;
- maintenance has no configuration path at all; `No Reset On Close` is rejected.

### Secret stores (Development; never committed)

| Store (user-secrets id) | Loaded by | Keys |
|---|---|---|
| `commercial-platform-dev` | API + Worker runtime (Development) | `Commercial:Postgres:Runtime:Password` |
| `commercial-platform-ops` | tooling only: `dotnet ef`, `--provision-database`, `scripts/shared-db.ps1` | `Commercial:Postgres:Migration:Password`, `Commercial:Postgres:Maintenance:Password`, `Commercial:Postgres:Dba:Username`, `Commercial:Postgres:Dba:Password` |

The runtime process never loads the ops store, so it never holds migration, maintenance or DBA credentials.
Production: the same keys come from the secret store / environment; runtime deployments receive only `Runtime:*`.

## 3. Provisioning (owner identity, explicit)

- EF migrations + Hangfire schema: `dotnet run --project src/Commercial.Worker -- --provision-database`
  (or `pwsh scripts/shared-db.ps1 provision`). `dotnet ef database update` also runs as the owner.
- After migrating, provisioning revokes INSERT/UPDATE/DELETE/TRUNCATE on `commercial.__ef_migrations_history` from the
  runtime role (EF seeding hook), so the runtime cannot forge or hide migration history.
- Hangfire runtime uses `Hangfire:PrepareSchemaIfNecessary=false` (default; asserted by `DatabaseIdentityRules`).
- Local Development only: `Database:ApplyMigrationsOnStartup=true` in the Worker provisions at startup **with the
  migration identity**; it fails clearly when no migration identity is configured. Shared/production keep it off.

## 4. Local docker

- `docker-compose.yml` mounts `scripts/db/local/` into `/docker-entrypoint-initdb.d`: `01-roles.sql` (roles) and
  `02-database.sql` (ownership, grants, default privileges, history hardening). Trust auth on 127.0.0.1 — no password
  exists in the repository. `POSTGRES_USER` is only the local bootstrap superuser.
- Existing volume (created before the split) — run once (idempotent, no data change):

  ```powershell
  docker compose up -d
  docker compose exec -T postgres psql -U commercial -d commercial -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/01-roles.sql
  docker compose exec -T postgres psql -U commercial -d commercial -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/02-database.sql
  ```

  (Use PowerShell or `MSYS_NO_PATHCONV=1` in Git Bash so the container path is not rewritten.)
- Runtime: `Username=commercial_platform_app` (appsettings.Development); Worker startup provisioning:
  `Commercial:Postgres:Migration:Username=commercial_platform_owner`.

## 5. Testcontainers

`ContainersFixture` runs the same `01-roles.sql` (once, generated throw-away passwords) and `02-database.sql` (per test
database), provisions with the owner identity and hands tests the **runtime** connection by default
(`CreateDatabaseAsync()` also exposes owner / maintenance / admin connections). Proven by
`DatabaseIdentityTests` (real privilege attempts + catalog queries) and `SharedDevRoleSplitScriptTests` (dry run of the
shared DBA script on a replica of the shared pre-split state).

## 6. Shared development database (`192.168.1.250 / commercial_platform_dev`)

1. Stop API/Worker. `pwsh scripts/shared-db.ps1 inspect` (read-only preflight).
2. DBA credentials (superuser) in the ops store; optionally owner/maintenance passwords (ops store).
3. `pwsh scripts/shared-db.ps1 role-split` — runs `scripts/db/shared-dev-role-split.sql` in one transaction with
   preconditions (expected ownership, no unexpected schemas/objects/policies/sessions/databases) and postconditions;
   then sets owner/maintenance passwords as client-side SCRAM verifiers if configured
   (`set-role-passwords` can do that later).
4. `pwsh scripts/shared-db.ps1 inspect dba` — verify; `migrations` (owner) — "up to date".
5. Runtime: `pwsh scripts/shared-db.ps1 worker` / `api` (runtime identity, no startup migration).

Hangfire objects, sequences, indexes and TOAST tables follow their tables; the script transfers tables, views,
sequences, routines and standalone types explicitly (no `REASSIGN OWNED`, which would also move unrelated shared objects).

## 7. Runtime session context (Phase 1 WS3)

The runtime identity is also the only identity that receives a PostgreSQL session context: `RlsDbSessionInitializer`
writes all `commercial.*` keys on every connection open of the runtime `CommercialDbContext`, and the fingerprint
guard verifies them before every command (see `rls-session-context.md`). The owner (migration / provisioning /
design-time) uses a separate context without these interceptors; maintenance has no application path. Pooling stays
enabled and `No Reset On Close` is rejected by the resolver and again by the session writer at runtime.
