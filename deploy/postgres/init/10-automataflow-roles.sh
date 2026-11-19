#!/bin/sh
# Runs ONCE, when the PostgreSQL data directory is first initialized (docker-entrypoint-initdb.d), as the bootstrap
# superuser. Creates (ADR-078):
#   automataflow_owner  owns the database and schema; used ONLY by the one-shot `migrate` service (DDL)
#   automataflow_app    used by the Api and the Workers; data access only (no DDL, no ownership, no superuser)
# Tables and sequences the owner creates later (every migration) are granted to the app role automatically through
# default privileges, so upgrades need no manual grants.
set -eu
: "${AF_DB_OWNER_PASSWORD:?AF_DB_OWNER_PASSWORD is required}"
: "${AF_DB_APP_PASSWORD:?AF_DB_APP_PASSWORD is required}"
: "${AF_DB_NAME:=automataflow}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
     -v owner_pw="$AF_DB_OWNER_PASSWORD" -v app_pw="$AF_DB_APP_PASSWORD" -v db="$AF_DB_NAME" <<'SQL'
CREATE ROLE automataflow_owner LOGIN PASSWORD :'owner_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
CREATE ROLE automataflow_app   LOGIN PASSWORD :'app_pw'   NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
CREATE DATABASE :"db" OWNER automataflow_owner;
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE :"db" TO automataflow_app;
\connect :"db"
ALTER SCHEMA public OWNER TO automataflow_owner;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO automataflow_app;
ALTER DEFAULT PRIVILEGES FOR ROLE automataflow_owner IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO automataflow_app;
ALTER DEFAULT PRIVILEGES FOR ROLE automataflow_owner IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO automataflow_app;
SQL
