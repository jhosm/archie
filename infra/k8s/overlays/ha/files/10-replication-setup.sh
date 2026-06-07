#!/bin/sh
# Postgres PRIMARY first-boot replication setup (ADR-PC-005 §P1).
#
# Runs once via /docker-entrypoint-initdb.d on an empty PGDATA. A *.sh hook
# (not *.sql) so the replication password from POSTGRES_REPLICATION_PASSWORD
# reaches psql as a server-side variable instead of being baked into the image.
#
# Creates:
#   * the `replicator` role (REPLICATION + LOGIN only — no general DB access)
#     the warm standby streams as;
#   * the physical replication slot the standby attaches to
#     (primary_slot_name=standby1_slot), so the primary retains exactly the WAL
#     the standby still needs and recycles no more.
set -e

psql -v ON_ERROR_STOP=1 \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
     -v replication_password="$POSTGRES_REPLICATION_PASSWORD" <<-'EOSQL'
    CREATE ROLE replicator WITH REPLICATION LOGIN PASSWORD :'replication_password';
    SELECT pg_create_physical_replication_slot('standby1_slot');
EOSQL
