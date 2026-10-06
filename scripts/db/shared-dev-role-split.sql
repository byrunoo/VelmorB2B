-- =====================================================================================================
-- ADR-0001 — owner / runtime / maintenance role split for the SHARED DEVELOPMENT database
--
--   MANUAL DBA SCRIPT. NOT executed by the application, by migrations or by CI.
--   Not yet executed on commercial_platform_dev (prepared 2026-10-07).
--
-- Target state (ADR-0001 §2.3):
--   commercial_platform_owner        owns database, schemas, tables, sequences, functions; runs EF migrations
--                                    and Hangfire schema provisioning. Never used by API/Worker runtime.
--   commercial_platform_app          runtime (API + Worker): LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE,
--                                    NOBYPASSRLS, owns NOTHING, DML grants only.
--   commercial_platform_maintenance  controlled DBA/ops: BYPASSRLS; never in application configuration.
--
-- Prerequisites:
--   * run as a DBA role with CREATEROLE (or superuser) that may SET ROLE to commercial_platform_app and to the
--     new owner role (needed by REASSIGN OWNED / ALTER ... OWNER);
--   * API and Worker stopped (no open sessions of commercial_platform_app);
--   * application code already supports separate migration credentials and
--     Hangfire:PrepareSchemaIfNecessary=false (Phase 1 readiness items) — otherwise the runtime can no longer
--     install/upgrade the Hangfire schema;
--   * passwords supplied at run time as psql variables (never stored in the repository), e.g.
--       psql "host=192.168.1.250 port=5432 dbname=commercial_platform_dev user=<dba_role>" \
--            -v owner_password="$OWNER_PW" -v maintenance_password="$MAINT_PW" \
--            -f scripts/db/shared-dev-role-split.sql
--   * afterwards store the new passwords only in user-secrets / the secret store.
-- =====================================================================================================

\set ON_ERROR_STOP on

BEGIN;

-- ---------------------------------------------------------------- 0. preconditions (fail = nothing changes)
DO $$
BEGIN
    IF current_database() <> 'commercial_platform_dev' THEN
        RAISE EXCEPTION 'Wrong database: %', current_database();
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
END $$;

-- ---------------------------------------------------------------- 1. roles
CREATE ROLE commercial_platform_owner
    LOGIN PASSWORD :'owner_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;

CREATE ROLE commercial_platform_maintenance
    LOGIN PASSWORD :'maintenance_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE BYPASSRLS NOREPLICATION;

-- Runtime role: assert/repair attributes (no-op if already correct).
ALTER ROLE commercial_platform_app NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;

-- ---------------------------------------------------------------- 2. ownership transfer (no data is touched)
ALTER DATABASE commercial_platform_dev OWNER TO commercial_platform_owner;
ALTER SCHEMA commercial OWNER TO commercial_platform_owner;
ALTER SCHEMA hangfire OWNER TO commercial_platform_owner;
-- Every remaining object owned by the runtime role in THIS database (tables incl. __ef_migrations_history,
-- sequences, functions, types) moves to the owner.
REASSIGN OWNED BY commercial_platform_app TO commercial_platform_owner;

-- ---------------------------------------------------------------- 3. database / schema privileges
REVOKE ALL ON DATABASE commercial_platform_dev FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE commercial_platform_dev TO commercial_platform_app, commercial_platform_maintenance;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

GRANT USAGE ON SCHEMA commercial, hangfire TO commercial_platform_app, commercial_platform_maintenance;
-- The runtime role gets NO CREATE on any schema (it cannot create or alter objects).

-- ---------------------------------------------------------------- 4. runtime (DML only) — existing objects
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA commercial TO commercial_platform_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA hangfire TO commercial_platform_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA commercial, hangfire TO commercial_platform_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA commercial TO commercial_platform_app;
-- EF migration history is owner-only metadata; the runtime only needs to read it for diagnostics.
REVOKE INSERT, UPDATE, DELETE ON commercial.__ef_migrations_history FROM commercial_platform_app;

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
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE pg_get_userbyid(c.relowner) = 'commercial_platform_app'
          AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema') THEN
        RAISE EXCEPTION 'Runtime role still owns relations';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'commercial_platform_app'
               AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls)) THEN
        RAISE EXCEPTION 'Runtime role has forbidden attributes';
    END IF;
    IF pg_get_userbyid((SELECT datdba FROM pg_database WHERE datname = current_database())) <> 'commercial_platform_owner' THEN
        RAISE EXCEPTION 'Database owner was not transferred';
    END IF;
END $$;

COMMIT;

-- ---------------------------------------------------------------- 8. verification (read-only; run afterwards)
-- SELECT rolname, rolsuper, rolcreaterole, rolcreatedb, rolbypassrls FROM pg_roles WHERE rolname LIKE 'commercial_platform_%';
-- SELECT n.nspname, c.relname, pg_get_userbyid(c.relowner) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
--   WHERE n.nspname IN ('commercial','hangfire') AND c.relkind IN ('r','S') ORDER BY 1, 2;
-- SELECT has_table_privilege('commercial_platform_app', 'commercial.outbox_messages', 'INSERT');      -- true
-- SELECT has_schema_privilege('commercial_platform_app', 'commercial', 'CREATE');                     -- false
--
-- ---------------------------------------------------------------- rollback (only if needed, as DBA)
-- REASSIGN OWNED BY commercial_platform_owner TO commercial_platform_app;  -- then DROP OWNED / DROP ROLE as appropriate
