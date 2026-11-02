# Decisions

Each entry records a non-obvious choice and why it was made. The newest entries are at the bottom.

## D1. Host user ids are stored as opaque strings without foreign keys

The host users live in a separate SQL Server database, so Mode tables cannot reference them with foreign keys. Every `*UserId` column is `varchar(64)`. This works whether the host uses GUIDs, integers or strings. `IUserDirectory` normalizes ids (for example, lowercase GUIDs) before Mode compares or stores them.

## D2. Host directory access uses a read-only SQL login and a configurable mapping

`SqlServerUserDirectory` reads the users table with a login that only has `SELECT`; `INSERT`, `UPDATE`, `DELETE` and `ALTER` are explicitly denied. The table and column names come from configuration because the real host schema is not yet known. They are restricted to plain identifiers and bracket-quoted, so configuration can never become SQL injection.

## D3. Directory lookups are cached for 5 minutes

This keeps the chat path from hitting the host database on every message render. The trade-off is that a user deactivated in the host can keep using Mode for up to `CacheMinutes`. In Prompt 2, the host JWT expiry bounds this as well. Set `CacheMinutes` to 0 if revocation must be immediate.

## D4. Local development uses a local SQL Server instance (now the default instance), and the API runs on the host

TCP is disabled on the local SQL Server instances, so a container cannot reach them. The API therefore runs with `dotnet run` and connects over shared memory, while the other services run in Docker. The `full` compose profile runs the API in Docker too, but that requires TCP to be enabled on SQL Server.

On 2026-11-02 the mock host database moved from `.\MYDB` to the default instance `.`, because `.\MYDB` kept stalling for minutes. `.\SQLEXPRESS` was not used: it allows Windows logins only, and the reader account is a SQL login. The old copy on `.\MYDB` was left in place. On this 8 GB machine every instance stalls on the first connection after an idle spell, because Windows pages SQL Server out when memory runs short. Retry, or free memory, before treating a directory timeout as a code failure.

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

Activity items go to mentioned people who can read the room according to their stored workspace role. Whether someone is a host-flagged guest is only known from their own token, so such a user could receive an activity item for a standard channel they cannot open. The activity feed re-checks access with the caller's token when listing items, so nothing leaks (see D32).

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

## D32. One notification path decides feed, banner, push and email

`NotificationService` handles every notification (mentions, replies, chat messages, reactions, "added to"):
- **One item per person per message**, keeping the most specific reason: Mention > @channel > Reply > Reaction > Chat message. Someone already notified about a message is not notified again, so an edit or a second reaction does not re-notify.
- **Readers only:** recipients who cannot read the room are dropped. The feed, unread counts and search also filter by the caller's readable rooms at read time. When access is lost, that user's unread items from the room are marked read, so the badge stays truthful.
- **Categories** (Mentions, Chats, Channels), each set to banner and feed, feed only, or off. The defaults are: mentions and chats banner and feed, channels feed only.
- **Muted room:** silent except for a direct @mention of the person.
- **Do not disturb:** the feed still fills, but no banner and no push. DND holds while the person is offline too.

## D33. Web Push with VAPID, sent only to people with no Mode tab open

Web Push is free; the keys are generated locally by `scripts/dev-setup.ps1`. The server needs outbound HTTPS to the browser vendors' push services (Google FCM, Mozilla, Microsoft, Apple). A push is a job that runs right after the change. It is skipped if the person has a live connection (they already got the in-app banner), has read the item, or is on DND.

Subscription endpoints come from browsers, so they are accepted only for those push services over HTTPS on the default port (`Push:AllowedHosts`). This stops a subscription from making the server call internal addresses. Redirects are not followed. Endpoints that answer 404 or 410 are deleted.

## D34. Email digests over SMTP, off by default

People opt in to an hourly or daily digest of unread activity. Items get 15 minutes to be seen in Mode first. Each item is emailed at most once (`ActivityItem.EmailedAt`), and content is HTML-encoded. Development uses Gmail SMTP (smtp.gmail.com:587, STARTTLS) with an App Password kept in user secrets. Gmail allows about 500 messages a day, so production should use the bank's own SMTP relay through the same settings.

## D35. Presence is in memory

Status is derived from live connections and client heartbeats: Available, then Away after 5 minutes without activity, then Offline when the last connection closes. A chosen status (Busy, DND, Away) and message last until changed or until the API restarts. Presence is visible only to yourself and people you share a workspace or chat with. Running several API instances would need a shared store (for example Redis), which this single-server design does not have.

## D36. Search is PostgreSQL full-text search

Search uses english stemming and web-style syntax (quotes, `OR`, `-word`). Results are limited to readable rooms before ranking, so nothing hidden affects counts or order. File results cover current, clean, undeleted versions only. Snippets are returned as plain text with highlight offsets, never as HTML. People search follows the directory rule: none for guests.

## D37. The client is a mountable module; the host owns sign-in and the token

`mountMode(element, { getToken, apiBaseUrl, theme })` is the whole contract. Mode calls `getToken` before every request and on every realtime reconnect, so the host decides how tokens are cached and refreshed. The API serves no cross-origin requests until `Cors:AllowedOrigins` lists the host's origin. The dev shell avoids CORS by proxying everything through the Vite server.

## D38. One store, one realtime connection, one controller

State lives in a single Zustand store. All behaviour lives in `ModeController`, and SignalR events reach the store only through it. This keeps realtime updates, optimistic sends and REST results in one place. Components only read the store through selectors and call controller methods.

## D39. Messages render as React elements, never as HTML

Stored Markdown is rendered with react-markdown, with raw HTML skipped. Links are limited to http(s) and mailto and open with `rel="noopener noreferrer nofollow"`. Images in text are shown as their alt text. Search snippets arrive as plain text with offsets and are wrapped in `<mark>` on the client. Together with the server-side sanitizing in D23, nothing a user types can run as script.

## D40. UBA brand colours, checked for contrast

The accent is UBA red `#D51709` (`--text-brand-secondary` on ubagroup.com). White on it is about 5.3:1. Dark mode uses UBA's tint `#FFA19A` for red text. Neutrals follow UBA's greys. A host may pass a different accent, but it must keep 4.5:1 against white. Presence is shown with a shape as well as a colour. Both end-to-end flows run axe and fail on any serious or critical WCAG A/AA problem.

## D41. The OpenAPI file is generated and committed

`scripts/export-openapi.ps1` builds the API and writes `client/openapi.json` with the Swashbuckle CLI. The environment variable `MODE_OPENAPI_EXPORT=1` makes the API skip startup validation and background services, because no secrets are present during an export. The script then regenerates `client/src/api/schema.d.ts` with openapi-typescript. That tool runs through `npx` with TypeScript 5 because it needs the TypeScript 5 compiler API, which TypeScript 7 doesn't have. The generated files are committed, so builds never depend on a running API. Rerun the script after any API change.

## D42. Drafts stay in the browser

Unsent text is saved per user and per room in `localStorage` and cleared when the message is sent. Drafts never reach the server and don't move between devices. On shared PCs, signing out of the host doesn't clear them. If that matters for UBA's branch machines, the host can call `localStorage.clear()` on sign-out.

## D43. The client focuses on chats; Channels and Activity are paused

At the product owner's request, the first release of the client is chat only: direct and group chats with threads, reactions, mentions, files, presence and search. `client/src/app/features.ts` switches off the Channels and Activity sections. Their code and the API behind them remain and are still tested. While they are off:
- the rail shows Chat, Files and Search;
- channel lists are not loaded;
- channel results are filtered out of search;
- banners for channel activity are not shown.

## D44. A rich-text composer that stores Markdown, and Undo for your own delete

As in Teams, formatting shows while you type instead of as raw `**` markers. The composer and the edit box use Lexical (MIT). It offers:
- a Format toolbar: bold, italic, strikethrough, inline code, link, bulleted and numbered lists, quote and code block;
- shortcuts: Ctrl+B, Ctrl+I, Ctrl+E for code and Ctrl+Shift+X for strikethrough, plus Markdown shortcuts such as `**bold**` and `- `.

The editor reads and writes the same Markdown subset the server sanitizes (D23). Mentions are stored as `@{userId}`, so the stored format and the API are unchanged.

Underline is blocked because Markdown has no form for it.

Edit and Delete are shown in the hover toolbar on your own messages. Up arrow in an empty composer edits your last message.

When an author deletes their own message, they can choose Undo. `POST /messages/{id}/restore` brings the text back from the delete record kept by D7 and records a `Restore` change. A message removed by a channel or workspace owner can't be restored by its author.

## D45. Chat pins, forwarding, edit history and chat list settings (Prompt 7, chats only)

Prompt 7 was cut down to chats while channels are paused (D43). Guests, the private-channel UI and the channel member list wait until channels return. Archived channels already stay read-only with their data kept; the authorization tests check that.

- **Pinned messages.** Anyone who can post may pin a top-level message. A room holds at most 20 pins. Deleting a message unpins it, and restoring it doesn't pin it again. A pin takes a room sequence and goes out as `messageEdited`, so missed pins replay after a reconnect. The server works for channels too; the client shows pins in chats.
- **Forwarding** copies the message into a chat the sender can post in, as a card under an optional note. The server makes the copy, and the copy can't be edited, so a card can't be faked. Mentions in the copy become plain names, so nobody is notified again. Files stay behind and are listed by name, so forwarding never gives a chat access to another chat's files. "View original" and message links open only for people who can read the original. Forwarding a forward that has no note passes on the original card instead of nesting cards. Forwarded text is searchable.
- **Edit history.** This changes D7. Authors can see their own earlier versions. Text removed by a delete is never shown to users, and other people see only the "Edited" label.
- **Pinned and hidden chats** are per person (`ConversationMember.IsPinned` and `IsHidden`). A new message in a hidden chat brings it back, as in Teams.

Turning a flag back on restores the section.
