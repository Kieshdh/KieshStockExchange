#!/usr/bin/env bash
# 7e — nightly Postgres backup. Cron suggestion (02:00 UTC daily):
#   0 2 * * * /opt/kse-server/deploy/backup.sh >> /var/log/kse-backup.log 2>&1
#
# Dumps the dockerised Postgres to a dated .sql.gz and prunes dumps older than
# RETENTION_DAYS. Off-site upload (S3/object storage) is a later hardening pass.
set -euo pipefail

REPO_DIR="${REPO_DIR:-/opt/kse-server}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/kse}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"

cd "$REPO_DIR"
# Load POSTGRES_USER / POSTGRES_DB from the env file.
set -a; [ -f .env.production ] && . ./.env.production; set +a
PG_USER="${POSTGRES_USER:-kse}"
PG_DB="${POSTGRES_DB:-kse}"

mkdir -p "$BACKUP_DIR"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="$BACKUP_DIR/kse-${STAMP}.sql.gz"

echo "[$(date -u)] dumping ${PG_DB} -> ${OUT}"
docker compose --env-file .env.production exec -T postgres \
    pg_dump -U "$PG_USER" "$PG_DB" | gzip > "$OUT"

echo "[$(date -u)] pruning dumps older than ${RETENTION_DAYS} days"
find "$BACKUP_DIR" -name 'kse-*.sql.gz' -type f -mtime "+${RETENTION_DAYS}" -delete

echo "[$(date -u)] backup complete: $(du -h "$OUT" | cut -f1)"
