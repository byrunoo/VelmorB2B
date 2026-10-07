-- =====================================================================================================
-- ADR-0001 three-role model — DATABASE-LEVEL part. DEVELOPMENT / TEST ONLY. Run as a superuser while
-- connected to the target database (current_database()). Requires 01-roles.sql first. Idempotent.
--
--   owner        owns the database, schemas `commercial` + `hangfire` and every object in them
--   app          CONNECT + USAGE + DML only (via grants and owner default privileges); cannot CREATE anything
--   maintenance  CONNECT + USAGE + DML on `commercial` (BYPASSRLS role) — never used by API/Worker
--
-- Upgrading an existing local volume: objects previously created by the bootstrap superuser are transferred
-- to the owner. No data is modified or dropped.
-- =====================================================================================================

DO $$
DECLARE
    db text := current_database();
BEGIN
    EXECUTE format('ALTER DATABASE %I OWNER TO commercial_platform_owner', db);
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', db);
    EXECUTE format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO commercial_platform_app, commercial_platform_maintenance', db);
END $$;

-- Nobody but the owner may create objects in public.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA IF NOT EXISTS commercial AUTHORIZATION commercial_platform_owner;
CREATE SCHEMA IF NOT EXISTS hangfire   AUTHORIZATION commercial_platform_owner;
ALTER SCHEMA commercial OWNER TO commercial_platform_owner;
ALTER SCHEMA hangfire   OWNER TO commercial_platform_owner;

-- Transfer pre-existing objects (local volumes created before the role split) to the owner.
DO $$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT c.oid::regclass AS obj
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('commercial', 'hangfire') AND c.relkind IN ('r', 'p')
          AND pg_get_userbyid(c.relowner) <> 'commercial_platform_owner'
    LOOP
        EXECUTE format('ALTER TABLE %s OWNER TO commercial_platform_owner', r.obj);
    END LOOP;

    FOR r IN -- sequences not owned by a table column (owned ones moved with their table)
        SELECT c.oid::regclass AS obj
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('commercial', 'hangfire') AND c.relkind = 'S'
          AND pg_get_userbyid(c.relowner) <> 'commercial_platform_owner'
    LOOP
        EXECUTE format('ALTER SEQUENCE %s OWNER TO commercial_platform_owner', r.obj);
    END LOOP;

    FOR r IN
        SELECT p.oid::regprocedure AS obj
        FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname IN ('commercial', 'hangfire') AND pg_get_userbyid(p.proowner) <> 'commercial_platform_owner'
    LOOP
        EXECUTE format('ALTER ROUTINE %s OWNER TO commercial_platform_owner', r.obj);
    END LOOP;
END $$;

-- Runtime: usage + DML on existing objects (no CREATE on any schema).
GRANT USAGE ON SCHEMA commercial, hangfire TO commercial_platform_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA commercial, hangfire TO commercial_platform_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA commercial, hangfire TO commercial_platform_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA commercial TO commercial_platform_app;
REVOKE CREATE ON SCHEMA commercial, hangfire FROM commercial_platform_app;

-- Runtime: future objects created by the owner (migrations / Hangfire provisioning).
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

-- Migration history is owner metadata: runtime may read it, never write it.
DO $$
BEGIN
    IF to_regclass('commercial.__ef_migrations_history') IS NOT NULL THEN
        REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON commercial.__ef_migrations_history FROM commercial_platform_app;
    END IF;
END $$;

-- Maintenance (explicit, audited use only).
GRANT USAGE ON SCHEMA commercial, hangfire TO commercial_platform_maintenance;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA commercial TO commercial_platform_maintenance;
ALTER DEFAULT PRIVILEGES FOR ROLE commercial_platform_owner IN SCHEMA commercial
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO commercial_platform_maintenance;
