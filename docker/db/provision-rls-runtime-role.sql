-- ADR-020 — provisions the non-privileged RUNTIME role for the row-level-security tenancy backstop.
--
-- Run as the database OWNER (or superuser). Postgres exempts superusers/BYPASSRLS roles from RLS
-- entirely, and this repo's local `dev` compose user is a superuser — so local dev keeps working
-- unchanged, and this role exists for anyone who wants to run the app RLS-enforced locally (the
-- integration test suite always does, via its own provisioning).
--
-- Locally: mounted into the Postgres container's /docker-entrypoint-initdb.d, so it runs
-- automatically on a FRESH volume (`docker compose down -v && up`). Existing volumes: run it once
-- via `docker compose exec db psql -U dev -d dev_db -f /docker-entrypoint-initdb.d/provision-rls-runtime-role.sql`.
--
-- Staging/prod (Neon): run via psql as the database owner, but CHANGE THE PASSWORD — the literal
-- below is a dev-only default. Full runbook: docs/DEPLOYMENT.md ("Row-level security").
--
-- The app then connects with:
--   ConnectionStrings__DefaultConnection = ...Username=app_runtime;Password=<the password>
--   ConnectionStrings__Migrations       = the owner/migrator connection (startup migrations do DDL)
--   Rls__EnforceRuntimeRole             = true   (fail-closed posture check at startup)

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_runtime') THEN
        CREATE ROLE app_runtime LOGIN PASSWORD 'app_runtime_password';
    END IF;
END $$;

GRANT USAGE ON SCHEMA public TO app_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO app_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_runtime;

-- Future tables/sequences created by the migrator (the role running this script) stay granted, so
-- later migrations don't strand the runtime role.
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO app_runtime;
