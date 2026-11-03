# Backup and restore runbook

What must be backed up together, because they only make sense as a set:

| Item | Where | Why |
| --- | --- | --- |
| PostgreSQL database `mode` | `pgdata` volume | Messages, memberships, file metadata, audit log, holds, settings. |
| File store | `files` volume (`/data/files`) | File bytes, versions, previews and compliance exports. The database points into it. |
| `deploy/.env` | server | Signing keys: without `FILES_LINK_SIGNING_KEY` old links fail (harmless, they expire in minutes); without `COMPLIANCE_SIGNING_KEY` existing export manifests can't be verified; without `AUDIT_CHAIN_KEY` the audit log's integrity can't be checked. Keep a second copy of the keys in the bank's secret store. |

Not backed up by Mode: people and roles (the host application owns them) and ClamAV signatures (re-downloaded).

## Schedule

- **Nightly** `deploy/backup.sh` (cron example in the script). It writes `backups/<UTC time>/` with `mode.dump`, `files.tar.gz`, `env.backup` and `SHA256SUMS`, and deletes local copies older than `KEEP_DAYS` (14).
- **Copy off the server** right after, to the bank's backup store, encrypted at rest. A backup that only exists on the same disk is not a backup.
- **Before every upgrade**, run it by hand.
- Targets: lose at most 24 hours (RPO) and be back within 4 hours (RTO). For a tighter RPO, add PostgreSQL WAL archiving (for example with `pgBackRest`); that is a separate decision.

The dump is taken while Mode runs. Files uploaded during the few minutes between the dump and the archive may be in one but not the other: a file row without bytes shows as unavailable, and bytes without a row are ignored. Both are harmless.

## Restore (whole server)

On a clean server with Docker and the same release tag checked out (see [DEPLOYMENT.md](DEPLOYMENT.md)):

```sh
cd /opt/mode/deploy
B=backups/20261103T013000Z                       # the backup to restore
(cd $B && sha256sum -c SHA256SUMS)               # stop if this fails
cp $B/env.backup .env && chmod 600 .env

docker compose -f docker-compose.prod.yml up -d postgres
docker compose -f docker-compose.prod.yml exec -T postgres pg_restore -U mode -d mode --clean --if-exists --no-owner /backups/$(basename $B)/mode.dump
docker compose -f docker-compose.prod.yml run --rm --no-deps -v "$PWD/$B:/in:ro" --entrypoint sh api \
  -c 'rm -rf /data/files/* && tar -xzf /in/files.tar.gz -C /data'
docker compose -f docker-compose.prod.yml up -d
```

`pg_restore --clean` recreates the audit-log triggers with the rest of the schema. Check afterwards:

```sh
docker compose -f docker-compose.prod.yml exec postgres psql -U mode -d mode -c \
  "select tgname from pg_trigger where tgrelid = '\"AuditLogs\"'::regclass and not tgisinternal;"
# expect audit_logs_no_change and audit_logs_no_truncate
curl -fsS https://$MODE_DOMAIN/health/ready
```

Then sign in, open a chat with history, download an older file, and run Admin, Audit log, **Check integrity** (it must say Intact).

## Restore drill (quarterly)

1. Restore the latest off-server backup to a separate test server (never over production).
2. Time it, and compare with the 4-hour target.
3. Check: message counts per room match production at backup time; a file downloads; an old compliance export's `manifest.sig` verifies with the restored key; the audit log is still append-only.
4. Record the date, duration and any problems in the change log.

## Partial recovery

- **One deleted file:** files are soft-deleted and recoverable in the app for `Files:RecoveryDays` (30). After that, restore `files.tar.gz` and the dump to a test server and copy the bytes back by hand.
- **Deleted messages:** authors can undo their own delete in the app. Older content is in `MessageEdits` (admins can export it) until retention removes it.
