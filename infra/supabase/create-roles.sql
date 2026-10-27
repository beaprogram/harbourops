-- Supabase (free public demo): least-privilege roles. Run once, as the project's "postgres" user, through the
-- session pooler with full TLS verification (docs/deployment-render.md):
--
--   PGPASSWORD=<postgres password> HARBOUROPS_OWNER_PASSWORD=... HARBOUROPS_APP_PASSWORD=... \
--     psql "host=<pooler host> port=5432 dbname=postgres user=postgres.<project-ref> sslmode=verify-full sslrootcert=<Supabase root CA>" \
--     -f infra/supabase/create-roles.sql
--
-- Passwords come from environment variables (\getenv, psql 15 or newer), never from the command line, so they
-- don't appear in shell history or process listings. Then run owner-default-privileges.sql as harbourops_owner.
--
--   harbourops_owner  owns the schema objects: the deploy workflow's migrations run as this role. It is not
--                     "postgres", so a leaked migration secret can't reach Supabase's own schemas or roles, and
--                     Supabase's default Data API grants (made for objects "postgres" creates) never apply.
--   harbourops_app    what the running app uses: read and write rows, nothing else.

\set ON_ERROR_STOP on
\getenv owner_password HARBOUROPS_OWNER_PASSWORD
\getenv app_password HARBOUROPS_APP_PASSWORD
SELECT :{?owner_password} AND :{?app_password} AS have_passwords \gset
\if :have_passwords
\else
  DO $$ BEGIN RAISE EXCEPTION 'Set HARBOUROPS_OWNER_PASSWORD and HARBOUROPS_APP_PASSWORD in the environment.'; END $$;
\endif

DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'harbourops_owner') THEN
    CREATE ROLE harbourops_owner LOGIN;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'harbourops_app') THEN
    CREATE ROLE harbourops_app LOGIN;
  END IF;
END
$$;

-- New roles start with no SUPERUSER, CREATEDB, CREATEROLE, REPLICATION, or BYPASSRLS. Those attributes are not
-- restated: changing them needs a superuser, which Supabase's "postgres" is not.
ALTER ROLE harbourops_owner WITH LOGIN PASSWORD :'owner_password';
ALTER ROLE harbourops_app WITH LOGIN PASSWORD :'app_password';

GRANT CONNECT ON DATABASE postgres TO harbourops_owner, harbourops_app;
GRANT USAGE, CREATE ON SCHEMA public TO harbourops_owner;
GRANT USAGE ON SCHEMA public TO harbourops_app;
