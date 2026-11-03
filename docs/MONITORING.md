# Monitoring and alerting checklist

Mode writes JSON log lines (Production) to stdout; ship them from Docker to the bank's log platform. There is no metrics endpoint yet, so the alerts below are built from health checks, logs and container stats.

## Health checks (poll every 30 s from outside the server)

| Check | Alert when |
| --- | --- |
| `GET https://<domain>/health/live` | Not 200 for 2 minutes (page). |
| `GET https://<domain>/health/ready` (database and host directory) | Not 200 for 5 minutes (page). |
| TLS certificate expiry | Under 14 days (Caddy renews at 30; this means renewal is failing). |

## Log signals

Field names are from the request log (`RequestPath`, `StatusCode`, `Elapsed`) and the message templates.

| Signal | Query | Alert when |
| --- | --- | --- |
| Server errors | `StatusCode >= 500` | More than 1% of requests over 5 minutes, or 20 in 5 minutes. |
| Slow requests | `Elapsed > 2000` on `/api/` | p95 over 2 s for 10 minutes. |
| Rate limiting | `StatusCode = 429` | More than 50 in 5 minutes from one person (abuse or a broken client). |
| Failed sign-ins | `StatusCode = 401` | More than 100 in 5 minutes (token or issuer misconfiguration, or probing). |
| Webhook key failures | `RequestPath` starts with `/api/v1/hooks/` and `StatusCode = 401` | More than 20 in 5 minutes. |
| Data-loss blocks | `StatusCode = 422` on message endpoints | Daily report to the compliance team, not a page. |
| Background jobs | level Error and `gave up after` in the message | Any (a scan, preview, push, retention or export job failed every retry). |
| Virus scanner | `JobType = file.scan` with level Warning | More than 5 in 15 minutes (ClamAV is likely down; uploads stay Pending until it is back). |
| Realtime revocations | `Closed * realtime connections of * deactivated people` | Informational; audit trail of deactivations reaching live sessions. |
| Host directory | `SqlException` | Any for 5 minutes (sign-ins and names fail). |
| Retention | audit action `retention.checked` | Missing for 26 hours (the job is not running). |
| Audit sealing | `Sealing the audit log failed` (Error) | Any for 10 minutes. Keep the `Audit log sealed through entry` lines: they are the outside anchors for the chain. |

## Container and host

| Metric | Alert when |
| --- | --- |
| Container restarts (`docker events`, or `docker ps` restart count) | Any restart of `api` or `postgres`. |
| Disk free on the Docker volume host | Under 20%. Files and backups grow; compliance exports are large. |
| Memory | Host over 90% for 10 minutes. |
| PostgreSQL connections | Over 80% of `max_connections` (100 by default). |
| Backup | No new `backups/<date>` folder in 26 hours, or the off-server copy failed. |

## Security events to review weekly

From the audit log (Admin, Audit log, or `GET /api/v1/admin/audit`): run **Check integrity** and record the result (it must say Intact with no unsealed older entries); then review `auth.realtime_revoked`, `hold.*`, `retention.changed`, `dlp.updated`, `webhook.*`, `compliance.export_*`, `audit.exported`, and admin role changes in the host application.

## Load test

`tools/Mode.LoadTest` connects N people in group chats of 10, keeps them connected over SignalR WebSockets, and has each send a message every 10 seconds. It reports connection success, HTTP status codes, send latency, delivery latency and the share of expected deliveries that arrived.

```sh
sqlcmd -S . -E -d ModeHostMock -v UserCount=1000 -i db/host-mock/loadtest-users.sql   # mock directory only
dotnet run -c Release --project tools/Mode.LoadTest -- --users 1000 --group 10 --interval 10 --minutes 10 \
  --api https://<staging-domain> --issuer <mock-issuer-url>
```

It needs the mock issuer, so run it against a staging copy of the production setup, never production itself. Each person sends 6 messages a minute, well under the 60-a-minute limit.

**Pass criteria for release:** every connection succeeds and stays connected; no non-2xx statuses; at least 99.9% of expected deliveries; delivery p95 under 1 second and p99 under 2 seconds.

### Results

| Date | Where | People | Duration | Connected | Sent (all 201) | Delivered | Send p50 / p95 / p99 | Delivery p50 / p95 / p99 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-11-03 | Developer laptop (8 GB, API + PostgreSQL + SQL Server + ClamAV + test client on one machine, console logging on) | 200 | 2 min | 200 / 200 | 2,292 | 22,920 of 22,920 (100%) | 208 / 1,614 / 3,514 ms | 151 / 1,207 / 3,046 ms |
| pending | Staging server (production size) | 1,000 | 10 min | | | | | |

The laptop run shows correctness (nothing dropped, no errors) but not capacity: the tail latency comes from everything sharing 8 GB of RAM. **The 1,000-person run on a production-sized server is a release gate** (see [SECURITY.md](SECURITY.md)).
