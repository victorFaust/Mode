# Decisions

Each entry records a non-obvious choice and why it was made. The newest entries are at the bottom.

## D1. Host user ids are stored as opaque strings without foreign keys

The host users live in a separate SQL Server database, so Mode tables cannot reference them with foreign keys. Every `*UserId` column is `varchar(64)`. This works whether the host uses GUIDs, integers or strings. `IUserDirectory` normalizes ids (for example, lowercase GUIDs) before Mode compares or stores them.

## D2. Host directory access uses a read-only SQL login and a configurable mapping

`SqlServerUserDirectory` reads the users table with a login that only has `SELECT`; `INSERT`, `UPDATE`, `DELETE` and `ALTER` are explicitly denied. The table and column names come from configuration because the real host schema is not yet known. They are restricted to plain identifiers and bracket-quoted, so configuration can never become SQL injection.

## D3. Directory lookups are cached for 5 minutes

This keeps the chat path from hitting the host database on every message render. The trade-off is that a user deactivated in the host can keep using Mode for up to `CacheMinutes`. In Prompt 2, the host JWT expiry bounds this as well. Set `CacheMinutes` to 0 if revocation must be immediate.

## D4. Local development uses the existing SQL Server `.\MYDB` instance, and the API runs on the host

TCP is disabled on the local SQL Server instances, so a container cannot reach them. The API therefore runs with `dotnet run` and connects over shared memory, while the other services run in Docker. The `full` compose profile runs the API in Docker too, but that requires TCP to be enabled on SQL Server.

## D5. Conversations are organization-wide, not scoped to a workspace

This matches Teams: a direct or group chat exists outside any team. Access to a conversation is checked by conversation membership only.

## D6. One sequence counter per room, shared by top-level messages and replies

A single counter lets the client detect any missed event (messages, replies, edits, deletes) with one number per room. The counter row lock serializes sends within a room, which is acceptable at 1,000 users.

## D7. Deleted message text moves to `MessageEdit` and the body is emptied

This keeps the original for compliance while making sure deleted text drops out of the full-text index and can never be returned by a regular query by mistake.

## D8. Read markers live in their own table

Last-read markers change on almost every view, while preferences rarely change. Separating them avoids rewriting the preferences JSON on every read.

## D9. Readiness includes the host directory

`/health/ready` fails if either PostgreSQL or the host users table is unreachable, because Mode cannot authorize anyone without the directory. `/health/live` only reports that the process is running.

## D10. SeaweedFS replaced MinIO as the local S3 store (superseded by D26)

`docker pull minio/minio` now fails (the repository no longer exists on Docker Hub), and `quay.io/minio/minio` returns 401. MinIO's community edition stopped publishing images in 2025. SeaweedFS (Apache 2.0) is the swap-in named in the build plan. It exposes the S3 API, which is all that `IFileStore` will use. It is pinned to `chrislusf/seaweedfs:4.50`, and anonymous requests are rejected (verified: 403).

## D11. The mock token issuer refuses to start outside Development

It signs a token for any subject, so it must never run anywhere else. It generates a new RSA key on each start, so tokens issued before a restart become invalid.

## D12. Target .NET 10 instead of .NET 8

.NET 8 support ends on 10 November 2026. .NET 10 is the current LTS release, supported until November 2028. The build plan's ".NET 8" is replaced by .NET 10 everywhere; nothing else about the stack changes. `Microsoft.Data.SqlClient` 7.1.1 pulls in `System.Security.Cryptography.Pkcs` 9.0.18, which has known advisories, so it is pinned to 10.0.14 with central transitive pinning.

## D13. Who may manage a channel

Workspace owners can manage every channel, and the member who created a channel can manage that one. This avoids a separate channel-owner role and the schema change it would need. Owners can manage a private channel they are not in (rename, archive, membership) but cannot read its messages.

## D14. Every workspace starts with a "General" standard channel

This matches Teams and gives new members somewhere to land.

## D15. Unreadable resources return 404, not 403

If the caller cannot see a workspace or channel at all, the API answers 404 whether or not the id exists, so ids cannot be probed. 403 is used only when the caller can see the resource but may not perform the action.

## D16. Host guests are capped at the Guest role at check time

The host directory does not expose roles, so Mode cannot refuse to store a higher role for a host guest when one is added. Instead, `AccessPolicy` treats any user whose token carries the mapped guest role as a workspace guest, whatever role is stored.

## D17. A user's channel memberships are removed when they leave a workspace

Without this, removing and later re-adding a user would quietly restore their access to private channels.

## D18. Membership changes lock the workspace row

Role changes and removals take `SELECT ... FOR UPDATE` on the workspace row and re-check the caller's permission after acquiring the lock. Two owners demoting each other at the same moment therefore cannot leave the workspace with no owner (covered by a test).

## D19. Sign-in auditing is per token

JWTs have no sign-in event. The first request made with each token (keyed by `jti`) writes one `auth.sign_in` entry. The seen-token cache is in memory, so a restart, or two simultaneous first requests, can occasionally produce a duplicate entry. That is acceptable for an audit trail.

## D20. Reconnect replay uses `Message.UpdatedSequence` rather than an event log

Every change in a room (create, edit, delete, reaction, new reply) takes the next room sequence and stamps it on the message. Replay returns the current state of each message changed since the client's last sequence. This costs one column and one index per room type, against a separate event-log table written on every change. The trade-off is that the client receives final states rather than every intermediate event, which is all a chat UI needs.

## D21. Messages are sent over REST, not through the hub

REST sends are idempotent through the client-generated id, go through the same validation and authorization as every other endpoint, and return a proper status code. The hub only delivers events.

## D22. Realtime access is re-checked after every membership or role change

The server tracks which rooms each live connection has joined. After a workspace removal, role change, channel removal or chat removal, it re-checks that person's joined rooms before the API responds. Rooms they can no longer read get `roomAccessRevoked` and the connection is removed from the group. This tracking is per server; scaling out needs a shared store (Valkey) as well as the SignalR backplane.

## D23. Markdown is parsed with Markdig and sanitized, not re-rendered

The stored Markdown is the user's text with raw HTML spans escaped (`<` becomes `&lt;`). Unsafe links and images are refused with 400 rather than silently changed, so the sender sees why. The client must still render with HTML disabled (Prompt 6).

## D24. Mention activity is addressed by stored roles

Activity items go to mentioned people who can read the room according to their stored workspace role. Whether someone is a host-flagged guest is only known from their own token, so such a user could receive an activity item for a standard channel they cannot open. The activity feed (Prompt 5) re-checks access when listing items, so nothing leaks.

## D25. Group chat membership

Anyone can leave a group chat. Only the person who started it can remove others. Direct chats cannot be left or renamed. Group chats are limited to 250 people.

## D26. Files are stored on local disk and served by the API

Everything is deployed on-premise. Bytes go through the API to a folder on the server's disk (`LocalFileStore` behind `IFileStore`), so there is no object store to run or expose.
- **Upload:** a short-lived upload session. The client sends the bytes with its bearer token, and the API checks the declared size.
- **Download:** browsers get a 5-minute HMAC-signed link, which works in `<a>` and `<img>` tags where a bearer header cannot be sent.
- **Later:** a second `IFileStore` (S3-compatible) can be added without other changes.
- **Backups:** `Files:StoragePath` must be backed up together with the database.

## D27. The API owns the job queue; the Python processor has no credentials

The API's background worker claims jobs from PostgreSQL (`FOR UPDATE SKIP LOCKED`) and does the following:
- checks the real file type and scans the file with ClamAV;
- calls the processor over HTTP with a shared key;
- gives the processor 15-minute signed links to read the original and write PNG previews.

The processor refuses any URL outside `MODE_API_BASE_URL`. A job is enqueued in the same transaction as the change that needs it, so nothing is lost if the server stops.

## D28. Unsafe files fail closed

All of these are deleted from disk, cannot be downloaded, and keep their metadata so people can see why:
- files whose content doesn't match their extension (Blocked);
- infected files, including files Windows Defender quarantines on write;
- files the scanner refuses or that fail every scan attempt (Failed).

ClamAV's stream limit is raised to 110 MB in `docker/clamav/clamd.conf`; keep it above `Files:MaxSizeMb`.

## D29. Downloads are attachments; only Mode's own PNG previews display inline

Every download carries `X-Content-Type-Options: nosniff` and `Content-Security-Policy: sandbox`. Uploaded HTML, SVG, scripts, executables and archives are refused by design and cannot be enabled in configuration.

## D30. Office previews are text only

Rendering Word, Excel and PowerPoint pages needs LibreOffice, a large extra service. Their text is extracted for search; the client shows an icon instead of a page image.

## D31. Signed link tokens are redacted from request logs

Paths under `/api/v1/files/content/` and `/internal/files/` are logged as `[redacted]`.
