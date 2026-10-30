#!/usr/bin/env bash
# Runs exactly once: the official postgres image executes files under /docker-entrypoint-initdb.d/ only when
# $PGDATA is empty, i.e. on the first start of a fresh volume. Safe to re-run by hand inside the container:
# CREATE happens only when the object is missing, and a re-run brings the role's password in line with .env.
#
# Creates the least-privilege application role (LOGIN only — no SUPERUSER, CREATEDB or CREATEROLE) and the
# database it owns. Owning the database is what lets it run the EF Core migrations; nothing beyond that.
set -euo pipefail

: "${APP_DB_NAME:?APP_DB_NAME must be set}"
: "${APP_DB_USER:?APP_DB_USER must be set}"
: "${APP_DB_PASSWORD:?APP_DB_PASSWORD must be set}"
: "${POSTGRES_USER:=postgres}"

# Values reach SQL only as psql -v variables, quoted by format(%I/%L) — never concatenated into the script.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
     -v app_user="$APP_DB_USER" -v app_password="$APP_DB_PASSWORD" -v app_db="$APP_DB_NAME" <<-'EOSQL'
    SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD %L', :'app_user', :'app_password')
    WHERE NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = :'app_user')
    \gexec

    SELECT format('ALTER ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', :'app_user', :'app_password')
    WHERE EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = :'app_user')
    \gexec

    SELECT format('CREATE DATABASE %I OWNER %I ENCODING ''UTF8'' TEMPLATE template0', :'app_db', :'app_user')
    WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = :'app_db')
    \gexec
EOSQL

echo "Application role/database ready (db=${APP_DB_NAME}, user=${APP_DB_USER})."
