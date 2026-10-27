-- Least-privilege database role for the running app. Run once, as the server administrator, AFTER the first
-- migration step has created the schema (the administrator owns every table because it runs the migrations):
--
--   psql "$MIGRATOR_CONNECTION_STRING" -v app_password="$APP_DB_PASSWORD" -f infra/sql/create-app-role.sql
--
-- The password must be the appDbPassword given to infra/main.bicep (it is inside the Key Vault connection string).
-- The app can read and write rows, but it cannot create, alter, or drop tables.

\set ON_ERROR_STOP on

DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'harbourops_app') THEN
    CREATE ROLE harbourops_app LOGIN;
  END IF;
END
$$;

ALTER ROLE harbourops_app WITH LOGIN PASSWORD :'app_password';

GRANT CONNECT ON DATABASE harbourops TO harbourops_app;
GRANT USAGE ON SCHEMA public TO harbourops_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO harbourops_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO harbourops_app;

-- Tables and sequences that later migrations create (as the administrator) get the same grants automatically.
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO harbourops_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO harbourops_app;
