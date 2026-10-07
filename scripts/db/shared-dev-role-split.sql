-- =====================================================================================================
-- ADR-0001 — owner / runtime / maintenance role split for the SHARED DEVELOPMENT database
--
--   MANUAL DBA SCRIPT. Never executed by the application, by migrations or by CI.
--   Reviewed 2026-10-07 against the real commercial_platform_dev state (Phase 1 workstream 1).
--
-- Target state (ADR-0001 §2.3):
--   commercial_platform_owner        owns the database, schemas `commercial` + `hangfire` and every object in them;
--                                    runs EF migrations and Hangfire schema provisioning. Never used by API/Worker.
--   commercial_platform_app          runtime (API + Worker): LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE,
--                                    NOBYPASSRLS, owns NOTHING, CONNECT + USAGE + DML only.
--   commercial_platform_maintenance  controlled DBA/ops: BYPASSRLS; never in application configuration.
--
-- Run as a SUPERUSER (needed for ALTER ... OWNER across roles and BYPASSRLS). Single transaction: any failed
-- precondition/postcondition rolls everything back. No data is read, modified or dropped.
--
-- Passwords: roles are created WITHOUT a password (no login possible until one is set). Set passwords afterwards
-- WITHOUT sending plain text to the server, e.g. psql `\password commercial_platform_owner` (client-side SCRAM),
-- or `pwsh scripts/shared-db.ps1 set-role-passwords` (sends SCRAM verifiers computed locally from the ops
-- user-secrets store). Never store passwords in the repository.
--
-- Run:  pwsh scripts/shared-db.ps1 role-split         (DBA credentials from the ops user-secrets store)
--   or  psql "host=192.168.1.250 port=5432 dbname=commercial_platform_dev user=<superuser>" -f scripts/db/shared-dev-role-split.sql
-- =====================================================================================================

\set ON_ERROR_STOP on

BEGIN;

-- ---------------------------------------------------------------- 0. preconditions (fail = nothing changes)
DO $$
DECLARE
    unexpected text;
BEGIN
    IF current_database() <> 'commercial_platform_dev' THEN
        RAISE EXCEPTION 'Wrong database: %', current_database();
    END IF;
    IF NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
        RAISE EXCEPTION 'Run as a superuser (current_user=%)', current_user;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'commercial_platform_app') THEN
        RAISE EXCEPTION 'commercial_platform_app does not exist';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname IN ('commercial_platform_owner', 'commercial_platform_maintenance')) THEN
        RAISE EXCEPTION 'Target roles already exist — review manually instead of re-running';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_stat_activity WHERE usename = 'commercial_platform_app' AND pid <> pg_backend_pid()) THEN
        RAISE EXCEPTION 'commercial_platform_app has open sessions — stop API/Worker first';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_policies) THEN
        RAISE EXCEPTION 'Unexpected existing RLS policies — review manually';
    END IF;

    -- Expected current owner of the database and both schemas.
    IF pg_get_userbyid((SELECT datdba FROM pg_database WHERE datname = current_database())) <> 'commercial_platform_app' THEN
        RAISE EXCEPTION 'Unexpected database owner';
    END IF;
    SELECT string_agg(nspname || '=' || pg_get_userbyid(nspowner), ', ') INTO unexpected
    FROM pg_namespace
    WHERE nspname IN ('commercial', 'hangfire') AND pg_get_userbyid(nspowner) <> 'commercial_platform_app';
    IF unexpected IS NOT NULL THEN
        RAISE EXCEPTION 'Unexpected schema ownership: %', unexpected;
    END IF;

    -- No unexpected non-system schemas.
    SELECT string_agg(nspname, ', ') INTO unexpected
    FROM pg_namespace
    WHERE nspname NOT IN ('public', 'commercial', 'hangfire', 'information_schema') AND nspname NOT LIKE 'pg\_%';
    IF unexpected IS NOT NULL THEN
        RAISE EXCEPTION 'Unexpected schemas: %', unexpected;
    END IF;

    -- Every object in commercial/hangfire must currently belong to the runtime role (nothing foreign to move).
    SELECT string_agg(n.nspname || '.' || c.relname || '=' || pg_get_userbyid(c.relowner), ', ') INTO unexpected
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname IN ('commercial', 'hangfire') AND pg_get_userbyid(c.relowner) <> 'commercial_platform_app';
    IF unexpected IS NOT NULL THEN
        RAISE EXCEPTION 'Unexpected relation ownership: %', unexpected;
    END IF;

    -- Nothing outside commercial/hangfire in this database may belong to the runtime role
    -- (system schemas excluded: pg_toast relations follow their parent table's owner automatically).
    SELECT string_agg(n.nspname || '.' || c.relname, ', ') INTO unexpected
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname NOT IN ('commercial', 'hangfire') AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
      AND pg_get_userbyid(c.relowner) = 'commercial_platform_app';
    IF unexpected IS NOT NULL THEN
        RAISE EXCEPTION 'Runtime role owns relations outside commercial/hangfire: %', unexpected;
    END IF;

    -- The runtime role must not own other databases (this script only handles this one).
    SELECT string_agg(datname, ', ') INTO unexpected
    FROM pg_database
    WHERE datname <> current_database() AND pg_get_userbyid(datdba) = 'commercial_platform_app';
    IF unexpected IS NOT NULL THEN
        RAISE EXCEPTION 'Runtime role owns other databases (handle separately): %', unexpected;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_default_acl WHERE pg_get_userbyid(defaclrole) = 'commercial_platform_app') THEN
        RAISE EXCEPTION 'Runtime role has default privileges defined — review manually';
    END IF;
END $$;

-- ---------------------------------------------------------------- 1. roles (no passwords: login impossible until set)
CREATE ROLE commercial_platform_owner       LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
CREATE ROLE commercial_platform_maintenance LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE BYPASSRLS   NOREPLICATION;
ALTER ROLE  commercial_platform_app         LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;

-- ---------------------------------------------------------------- 2. explicit ownership transfer (this database only)
ALTER DATABASE commercial_platform_dev OWNER TO commercial_platform_owner;
ALTER SCHEMA commercial OWNER TO commercial_platform_owner;
ALTER SCHEMA hangfire   OWNER TO commercial_platform_owner;

DO $$
DECLARE
    r record;
BEGIN
    FOR r IN -- tables (owned sequences and row types follow their table)
        SELECT c.oid::regclass AS obj FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('commercial', 'hangfire') AND c.relkind IN ('r', 'p')
          AND pg_get_userbyid(c.relowner) = 'commercial_platform_app'
    LOOP
        EXECUTE format('ALTER TABLE %s OWNER TO commercial_platform_owner', r.obj);
    END LOOP;

    FOR r IN -- views / materialized views / foreign tables, if any
        SELECT c.oid::regclass AS obj, c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('commercial', 'hangfire') AND c.relkind IN ('v', 'm', 'f')
          AND pg_get_userbyid(c.relowner) = 'commercial_platform_app'
    LOOP
        EXECUTE format('ALTER %s %s OWNER TO commercial_platform_owner',
            CASE r.relkind WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'MATERIALIZED VIEW' ELSE 'FOREIGN TABLE' END, r.obj);
    END LOOP;

    FOR r IN -- remaining (standalone) sequences
        SELECT c.oid::regclass AS obj FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('commercial', 'hangfire') AND c.relkind = 'S'
          AND pg_get_userbyid(c.relowner) = 'commercial_platform_app'
    LOOP
        EXECUTE format('ALTER SEQUENCE %s OWNER TO commercial_platform_owner', r.obj);
    END LOOP;

    FOR r IN -- functions / procedures
        SELECT p.oid::regprocedure AS obj FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname IN ('commercial', 'hangfire') AND pg_get_userbyid(p.proowner) = 'commercial_platform_app'
    LOOP
        EXECUTE format('ALTER ROUTINE %s OWNER TO commercial_platform_owner', r.obj);
    END LOOP;

    FOR r IN -- standalone types (enums/domains/composites not tied to a table; skip array types)
        SELECT t.oid::regtype AS obj FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
        WHERE n.nspname IN ('commercial', 'hangfire') AND t.typrelid = 0 AND t.typelem = 0
          AND pg_get_userbyid(t.typowner) = 'commercial_platform_app'
    LOOP
        EXECUTE format('ALTER TYPE %s OWNER TO commercial_platform_owner', r.obj);
    END LOOP;
END $$;

-- ---------------------------------------------------------------- 3. database / schema privileges
REVOKE ALL ON DATABASE commercial_platform_dev FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE commercial_platform_dev TO commercial_platform_app, commercial_platform_maintenance;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA commercial, hangfire TO commercial_platform_app, commercial_platform_maintenance;
REVOKE CREATE ON SCHEMA commercial, hangfire, public FROM commercial_platform_app;

-- ---------------------------------------------------------------- 4. runtime (DML only) — existing objects
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA commercial, hangfire TO commercial_platform_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA commercial, hangfire TO commercial_platform_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA commercial TO commercial_platform_app;
-- EF migration history is owner metadata: runtime may read it, never write it.
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON commercial.__ef_migrations_history FROM commercial_platform_app;

-- ---------------------------------------------------------------- 5. runtime — future objects created by the owner
ALTER DEFAULT PRIVILEGES FOR ROLE commercial_platform_owner IN SCHEMA commercial
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO commercial_platform_app;
ALTER DEFAULT PRIVILEGES FOR ROLE commercial_platform_owner IN SCHEMA commercial
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO commercial_platform_app;
ALTER DEFAULT PRIVILEGES FOR ROLE commercial_platform_owner IN SCHEMA commercial
    GRANT EXECUTE ON FUNCTIONS TO commercial_platform_app;
ALTER DEFAULT PRIVILEGES FOR ROLE commercial_platform_owner IN SCHEMA hangfire
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO commercial_platform_app;
ALTER DEFAULT PRIVILEGES FOR ROLE commercial_platform_owner IN SCHEMA hangfire
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO commercial_platform_app;

-- ---------------------------------------------------------------- 6. maintenance (explicit, audited use only)
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA commercial TO commercial_platform_maintenance;
ALTER DEFAULT PRIVILEGES FOR ROLE commercial_platform_owner IN SCHEMA commercial
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO commercial_platform_maintenance;

-- ---------------------------------------------------------------- 7. post-conditions (fail = full rollback)
DO $$
DECLARE
    leftover text;
BEGIN
    SELECT string_agg(n.nspname || '.' || c.relname, ', ') INTO leftover
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE pg_get_userbyid(c.relowner) = 'commercial_platform_app' AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema';
    IF leftover IS NOT NULL THEN
        RAISE EXCEPTION 'Runtime role still owns relations: %', leftover;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_namespace WHERE pg_get_userbyid(nspowner) = 'commercial_platform_app')
       OR EXISTS (SELECT 1 FROM pg_proc WHERE pg_get_userbyid(proowner) = 'commercial_platform_app')
       OR EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                  WHERE pg_get_userbyid(t.typowner) = 'commercial_platform_app' AND n.nspname NOT LIKE 'pg\_%') THEN
        RAISE EXCEPTION 'Runtime role still owns schemas, routines or types';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE n.nspname IN ('commercial', 'hangfire') AND pg_get_userbyid(c.relowner) <> 'commercial_platform_owner') THEN
        RAISE EXCEPTION 'Not every commercial/hangfire relation belongs to the owner';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'commercial_platform_app'
               AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls OR NOT rolcanlogin)) THEN
        RAISE EXCEPTION 'Runtime role attributes are wrong';
    END IF;
    IF pg_get_userbyid((SELECT datdba FROM pg_database WHERE datname = current_database())) <> 'commercial_platform_owner' THEN
        RAISE EXCEPTION 'Database owner was not transferred';
    END IF;
    IF has_schema_privilege('commercial_platform_app', 'commercial', 'CREATE')
       OR has_schema_privilege('commercial_platform_app', 'hangfire', 'CREATE')
       OR has_schema_privilege('commercial_platform_app', 'public', 'CREATE')
       OR has_database_privilege('commercial_platform_app', current_database(), 'CREATE') THEN
        RAISE EXCEPTION 'Runtime role can still create objects';
    END IF;
    IF NOT has_table_privilege('commercial_platform_app', 'commercial.outbox_messages', 'INSERT,SELECT,UPDATE,DELETE')
       OR has_table_privilege('commercial_platform_app', 'commercial.__ef_migrations_history', 'INSERT') THEN
        RAISE EXCEPTION 'Runtime DML grants are wrong';
    END IF;
END $$;

COMMIT;

-- ---------------------------------------------------------------- 8. verification (read-only; run afterwards)
-- pwsh scripts/shared-db.ps1 inspect-dba
--
-- ---------------------------------------------------------------- rollback (only if ever needed, as superuser)
-- Reverse with explicit ALTER ... OWNER TO commercial_platform_app statements for the database, both schemas and
-- their objects; then DROP ROLE commercial_platform_owner / commercial_platform_maintenance after DROP OWNED BY.
