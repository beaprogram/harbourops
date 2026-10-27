-- Supabase (free public demo): run as harbourops_owner after create-roles.sql, before or after migrations.
-- Tables and sequences that harbourops_owner creates (every migration) give the app role row access only.
-- No grant goes to Supabase's anon, authenticated, or service_role roles, so the Data API can't expose them
-- even if it is switched back on.

\set ON_ERROR_STOP on

ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO harbourops_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO harbourops_app;

-- Objects created before this script ran (if migrations went first).
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO harbourops_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO harbourops_app;
