-- PHANTOM database roles (security review B-F3).
-- Run once as the database OWNER (the role that runs migrations), after the first migration:
--   psql "$MIGRATIONS_CONNECTION_STRING" -v app_password="'<strong-random-password>'" -f docs/deployment/db-roles.sql
-- The API then connects as phantom_app (ConnectionStrings__Phantom). Because phantom_app does not own the tables,
-- it cannot ALTER/DISABLE the trigger that keeps audit_logs append-only, and it has no UPDATE/DELETE/TRUNCATE there.

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'phantom_app') THEN
    CREATE ROLE phantom_app LOGIN;
  END IF;
END $$;
ALTER ROLE phantom_app WITH LOGIN PASSWORD :app_password NOSUPERUSER NOCREATEDB NOCREATEROLE;

GRANT CONNECT ON DATABASE :"DBNAME" TO phantom_app;
GRANT USAGE ON SCHEMA public TO phantom_app;

-- Normal application access to every table and sequence...
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO phantom_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO phantom_app;
-- ...including tables and sequences created by future migrations (run by this owner role).
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO phantom_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO phantom_app;

-- ...except the audit log, which is append-only (Requirement 15.3).
REVOKE UPDATE, DELETE, TRUNCATE ON audit_logs FROM phantom_app;
-- The migrations history belongs to the release step alone.
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON __ef_migrations_history FROM phantom_app;
