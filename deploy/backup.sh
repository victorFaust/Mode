#!/usr/bin/env sh
# Nightly backup of everything Mode needs to restore: the database, the file store and the deploy config.
# Run from deploy/ on the server, e.g. cron: 30 1 * * * cd /opt/mode/deploy && ./backup.sh >> backups/backup.log 2>&1
# See docs/RUNBOOK-backup-restore.md.
set -eu

KEEP_DAYS="${KEEP_DAYS:-14}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
DIR="backups/$STAMP"
mkdir -p "$DIR"

# Custom format so pg_restore can restore selectively; written inside the container to the mounted /backups.
docker compose -f docker-compose.prod.yml exec -T postgres \
  pg_dump -U mode -d mode --format=custom --file="/backups/$STAMP/mode.dump"

# File bytes (originals, versions, previews, compliance exports).
docker compose -f docker-compose.prod.yml run --rm --no-deps -v "$(pwd)/$DIR:/out" --entrypoint sh api \
  -c 'tar -czf /out/files.tar.gz -C /data files'

# Secrets are needed to verify signed links and compliance manifests after a restore.
cp .env "$DIR/env.backup"
chmod 600 "$DIR/env.backup"

( cd "$DIR" && sha256sum mode.dump files.tar.gz env.backup > SHA256SUMS )
echo "Backup written to $DIR"

find backups -mindepth 1 -maxdepth 1 -type d -mtime +"$KEEP_DAYS" -exec rm -rf {} +
