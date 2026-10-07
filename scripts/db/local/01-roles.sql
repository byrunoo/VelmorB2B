-- =====================================================================================================
-- ADR-0001 three-role model — CLUSTER-LEVEL part (roles). DEVELOPMENT / TEST ONLY.
--
-- Used by: docker-compose (mounted into /docker-entrypoint-initdb.d, runs on a fresh volume) and the
-- Testcontainers fixture. Idempotent: safe to re-run (e.g. to upgrade an existing local volume):
--   docker compose exec -T postgres psql -U commercial -d commercial -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/01-roles.sql
--
-- No passwords here. Local docker uses trust auth bound to 127.0.0.1 (no credential exists in the repository);
-- Testcontainers sets generated throw-away passwords at run time. Shared/production roles are created by a DBA
-- (scripts/db/shared-dev-role-split.sql), never by this file.
-- =====================================================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'commercial_platform_owner') THEN
        CREATE ROLE commercial_platform_owner LOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'commercial_platform_app') THEN
        CREATE ROLE commercial_platform_app LOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'commercial_platform_maintenance') THEN
        CREATE ROLE commercial_platform_maintenance LOGIN;
    END IF;
END $$;

-- Enforce attributes on every run (repairs drift on existing volumes).
ALTER ROLE commercial_platform_owner       LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
ALTER ROLE commercial_platform_app         LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
ALTER ROLE commercial_platform_maintenance LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE BYPASSRLS   NOREPLICATION;
