# Lightweight Teams: Research and Opus 5.5 Build Prompts

Nov 1, 2026 · @victor

Teams is chat, channels, files and meetings on top of Microsoft 365 identity and storage. A lightweight version needs only chat, channels, files, mentions and search, and the prompts below build that in phases.

## What Teams does

Teams bundles seven feature areas. Each row is what the research found, and the last column is the lightweight call.

| Area | What Teams does | Lightweight call |
| --- | --- | --- |
| Chat | One-on-one and group messaging with persistent history across devices; chats can be hidden or pinned | Keep |
| Teams and channels | A team is a shared workspace; channels are topic or project spaces holding conversations, files and meetings | Keep |
| Channel types | Standard (open to all team members), private (invite-only), shared (can include people outside the team) | Standard now, private later, shared dropped |
| Roles | Owners manage members and settings; members participate; guests are external with limited rights | Keep owner, member, guest |
| Files | Shared in chats and channels with version history; channel files live in SharePoint, chat files in OneDrive | Keep, on simpler storage |
| Activity and notifications | A feed of @mentions, @channel mentions, replies, likes and being added to a team; filters, unread toggle, banner or feed-only options | Keep a trimmed version |
| Presence | Status indicators and Do not disturb | Keep basic statuses |
| Search | Messages, files, people and meetings from one search box | Keep messages, files, people |
| Meetings | Audio, video, screen share, chat, captions, whiteboard, recording; scheduled or Meet now | Defer; start with a join link |
| Apps and extensibility | Tabs, bots, message extensions, webhooks, Graph API | Defer; keep one webhook and bot hook |
| Admin and compliance | Retention policies, eDiscovery, DLP, audit, communication compliance | Keep audit and retention basics |

## How Teams is built

A team is a Microsoft 365 group: an Entra ID object with an Exchange group mailbox, a SharePoint site and a OneNote notebook. As a rule, communication content sits in mailboxes and file content sits in SharePoint. You can borrow the ideas but replace the plumbing with one database and one file store.

| Teams concept | How Teams does it | Lightweight equivalent |
| --- | --- | --- |
| Identity | Entra ID for all Microsoft 365 workloads | The host application authenticates users and issues a JWT; Mode validates it and reads people from the host's users table |
| Team | Microsoft 365 group, with owners and members | A workspace table with a role per user |
| Channel | A subfolder in the team's SharePoint site | A channel row plus a files prefix in object storage |
| Private channel | Separate SharePoint site with its own permissions; max 30 per team, 250 members | A membership table checked on every read |
| Messages | Processed by the chat service, copied to mailboxes for retention and eDiscovery | A messages table with soft delete and an edit history |
| Chat files | OneDrive of the sender | Object storage with a per-conversation access check |
| Channel files | Team SharePoint library with version history | Object storage with a version table |
| Retention | Admin-set policies that keep or delete messages; holds override deletion | A retention setting per workspace and a nightly job |
| Extensibility | Tabs, bots, webhooks, Graph API | A REST API and one incoming webhook |

Two behaviors are worth copying. Edited and deleted messages disappear for users but stay available to compliance admins until retention ends. And if one policy says keep and another says delete, the content is kept.

## Lightweight MVP scope

Ship in three phases. Phase 1 is a usable internal chat tool; phases 2 and 3 add what Teams users miss most. This split is my recommendation, not something from the research.

| Phase | Features | Done when |
| --- | --- | --- |
| 1: Core | JWT sign-in from the host app, workspaces, standard channels, 1:1 and group chat, threaded replies, @mentions, file upload, message search, presence | A department can run its day on it |
| 2: Daily use | Private channels, activity feed with unread counts, per-channel mute, reactions, pinned messages, file versions, guests | Teams users stop switching back for chat |
| 3: Governance and calls | Audit log, retention policy, message export, incoming webhook and bot hook, meeting join links with video | IT and compliance sign off |

Left out on purpose: shared channels across organizations, whiteboard, live events, voicemail and phone, Planner, OneNote and Power BI tabs, and AI meeting features. Each can be added later behind the same API.

## Suggested stack

Every component is open source and free to run. The only real cost is the machine it runs on, so host it on a server your organization already owns. These are my recommendations; swap any row and the prompts still work.

| Layer | Choice (licence) | Why |
| --- | --- | --- |
| Core API | ASP.NET Core Web API on .NET 8, C# (MIT) | Chat, channels, permissions and the realtime hub live here; free and cross-platform |
| Realtime | SignalR (MIT) | Ships with ASP.NET Core; groups, reconnects, fallbacks |
| Database | PostgreSQL (PostgreSQL licence) | Relational fit for memberships and messages; built-in full-text search |
| Data access | EF Core 8 with Npgsql (MIT) | Typed queries and migrations |
| Search | PostgreSQL full-text search | Enough for 1,000 users; no separate search cluster |
| Authentication | JWT bearer validation (built into ASP.NET Core) | The host application signs users in and issues tokens; Mode only validates them and reads people from the host's users table through a read-only IUserDirectory |
| Files | MinIO (AGPLv3), S3-compatible, behind an IFileStore interface | Presigned URLs and versioning; SeaweedFS (Apache 2.0) is the swap-in |
| File processing | Python 3 with FastAPI (MIT), as a separate service | Thumbnails, previews and text extraction use Python's richer libraries |
| Job queue | PostgreSQL-backed queue; Valkey (BSD) if it outgrows that | No extra broker to run at 1,000 users |
| Virus scanning | ClamAV (GPL) | Scans uploads before they are released |
| Presence cache | In-process memory; Valkey if you run several servers | Presence is ephemeral |
| Frontend | React, TypeScript, Vite (MIT), shipped as a module that mounts in the host app | No login screen of its own |
| Push | Standard Web Push with VAPID keys | A browser feature; no vendor |
| Meetings (phase 3) | LiveKit server (Apache 2.0) or Jitsi Meet (Apache 2.0), plus coturn (BSD) for TURN | Self-hosted video without building media servers |
| Delivery | Docker Compose, Caddy (Apache 2.0) reverse proxy with automatic TLS, Jenkins (MIT) for CI | One Linux server is enough to start |

Mix languages sparingly. At 1,000 users the .NET 8 core should handle the chat path on its own, so use Python for jobs where its libraries win rather than for chat speed. Every extra service adds deployment, monitoring and testing work.

Licences are from memory, so confirm each before adopting; AGPL components such as MinIO are generally fine unmodified for internal use but check with legal. Open source also shifts work onto you: backups, upgrades and security patching are yours to run.

## Build prompts, part 1: foundation

Run these in order, each in a fresh Opus 5.5 session on the same repo. Replace anything in \[brackets\]. Start every later session by pasting Prompt 0, then the prompt for that step. Ask Opus 5.5 to plan before it writes code, and review the plan before approving it.

### Prompt 0: project context (paste first, every session)

```text
You are the lead engineer on Mode, a lightweight internal collaboration tool for [Org] (up to 1,000 users), built as a module that is merged into a larger host application whose database already holds all user accounts. Mode never creates or duplicates user accounts; it reads them from the host. It covers the core of Microsoft Teams: workspaces, channels, direct and group chat, threaded replies, @mentions, file sharing, search, presence and notifications. It does not copy Teams code or assets; we build our own product from the public feature set.

Stack (all open source, self-hosted, zero licence cost): ASP.NET Core Web API (C#, .NET 8) as the core, SignalR for realtime, EF Core with PostgreSQL, JWT validation only, because the host application handles authentication, MinIO as S3-compatible file storage behind an IFileStore interface, ClamAV for virus scanning, React + TypeScript + Vite for the web client, Docker Compose for local and deployed runs.

Polyglot rule: the core API and the chat realtime path stay in .NET 8. A component may be a separate service in another language, such as Python with FastAPI, only when that language is clearly better for the job (file processing, text extraction, bulk exports, future AI features). Each such service needs a documented HTTP or queue contract, its own Dockerfile and tests, and an entry in docs/DECISIONS.md explaining why it is separate. Do not split the chat hot path.

Cost rule: every dependency must be open source with a licence that allows free internal use. No paid SaaS, no cloud-only services, no paid tiers. Record each new dependency and its licence in docs/DEPENDENCIES.md, and ask before adding one.

Non-goals (do not build or stub): cross-organization shared channels, whiteboard, live events, voicemail or phone, third-party app store, AI features.

Working rules:
1. Before writing code, post a short plan: files you will touch, data changes, risks. Wait for my approval on anything that changes the data model.
2. Work in small, reviewable steps. After each step, run the build and tests and report the real output, not a summary of what you expect.
3. No placeholder logic, no TODO-only stubs, no hard-coded secrets. Configuration goes in environment variables with a documented .env.example.
4. Every endpoint enforces authorization through one shared policy layer. Never trust a client-supplied user id or role.
5. Add tests with each feature. A feature is not done until its tests pass.
6. Record each non-obvious decision in docs/DECISIONS.md with the reason.
7. If a requirement is ambiguous and the answer changes the design, ask one specific question; otherwise pick the simplest option and note it.

Quality bar: this should feel like a polished product, not an admin dashboard or a chatbot widget. Clean spacing, keyboard shortcuts, loading and empty states, and accessible controls.
```

### Prompt 1: architecture and data model

```text
Task: design and scaffold the architecture. Do not build features yet.

Produce docs/ARCHITECTURE.md containing:
- A component diagram (Mermaid) of client, API, SignalR hub, database, object storage, the Python file service, ClamAV and the host application's token issuer.
- An entity-relationship diagram (Mermaid) and the EF Core entities for: UserSettings (Mode-only preferences and last-read markers keyed by the host application's user id; there is no Mode user table), Workspace, WorkspaceMember (role: owner | member | guest), Channel (type: standard | private), ChannelMember, Conversation (type: direct | group), ConversationMember, Message, MessageEdit, Reaction, Mention, FileAsset, FileVersion, ActivityItem, AuditLog.
- Message rules: a message belongs to exactly one channel or one conversation; replies point to a parent message (one level of threading); edits and deletes are soft, the original text is kept in MessageEdit and stays visible to compliance admins only; every message has a monotonically increasing sequence number per channel or conversation for reliable ordering and gap detection.
- A REST API outline (routes, verbs, request and response shapes) and a SignalR event contract (event names and payloads) for: message created, edited, deleted, reaction changed, typing, presence changed, activity item created.
- A folder structure for the API, the client and tests.
- User directory: users live in the host application's database. Define an IUserDirectory interface (get by id, get many by ids, search by name or email, check active) and implement it by reading the host's users table through a read-only database role or a view, with table and column names taken from configuration: [host database engine, users table name, and columns for id, name, email, avatar and active flag]. Every Mode table stores the host user id as a plain column; add a foreign key only if the host data is in the same database. Cache lookups for a few minutes and never write to the host's tables.
- Capacity and service boundaries: design for up to 1,000 users and about 1,000 concurrent connections on one server, stateless so it can scale out later. List which components are separate services and why; the default is that only file processing runs outside .NET.

Then scaffold: solution and project files, a docker-compose.yml (API, PostgreSQL, MinIO, ClamAV, Python file service, a mock token issuer for local development, client dev server), EF Core initial migration, health endpoint, structured logging, and a Jenkinsfile that builds and runs tests. Include one passing smoke test.

Stop after scaffolding and list what I should review.
```

### Prompt 2: sign-in, workspaces, channels and permissions

```text
Task: implement identity and access, the foundation every other feature depends on.

Build:
1. Authentication belongs to the host application, which issues a signed JWT. Mode never shows a login screen and never stores credentials. Validate every token: signature (from the host's JWKS endpoint or a configured key), issuer, audience, expiry and a small clock skew. Write the claim contract in docs/AUTH.md first (subject id, display name, email, roles, and anything else: [list the host's claim names here]) and map host roles to Mode roles in one place. Accept the token on REST calls in the Authorization header and on SignalR connections through its access-token mechanism; re-validate on reconnect and close the connection when the token expires, so the client must fetch a fresh token from the host. Build a mock token issuer for local development and tests. Do not create a Mode user table or user records. Identify the caller by the token's subject id and look the person up through IUserDirectory when you need a name, email or avatar. Reject the request if the directory says the user is missing or inactive, which also covers revocation. Never store passwords.
2. Workspaces: create, rename, list mine, add people by searching the host's user directory, remove member. Roles are owner, member and guest. A workspace always has at least one owner; refuse to demote or remove the last one.
3. Channels: create, rename, archive, list. Standard channels are open to every workspace member. Private channels are visible and readable only to their own members, who must already belong to the workspace. Implement the private-channel check and unit-test it now; the private-channel UI comes later.
4. A single authorization service (policy handlers) that answers: can this user read, post in, or manage this channel, conversation, message or file? Every endpoint and every SignalR hub method calls it. No inline role checks elsewhere.
5. Guests: can only see the channels and conversations they were explicitly added to, cannot create channels, cannot see the member directory.
6. Write an audit-log entry for membership changes, role changes, channel creation and archive, and sign-ins.

Tests: write an authorization matrix test covering owner, member, guest and non-member against each action on standard and private channels, plus a test that proves a user cannot read another workspace's data by guessing ids.

Definition of done: all tests pass, and docs/DECISIONS.md explains any trade-off you made on roles.
```

## Build prompts, part 2: product features

### Prompt 3: messaging and realtime

```text
Task: build channel messaging, direct and group chat, and the realtime layer. Follow the data model and event contract in docs/ARCHITECTURE.md.

Build:
1. Send, list (cursor-paginated, newest first), edit and delete messages in channels and conversations. Direct conversations are created on demand and unique per pair; group conversations have 3 or more members and can be renamed.
2. Threaded replies, one level deep: a reply shows under its parent with a reply count and last-reply time.
3. Rich text limited to bold, italic, code, code block, links and lists, stored as sanitized Markdown. Sanitize on the server; never render raw HTML.
4. @mentions of a person, @channel and @workspace-members. Parse on the server, store in the Mention table, and create activity items for the recipients.
5. Reactions: add and remove an emoji; show counts and who reacted.
6. SignalR: clients join groups per channel and conversation they can access; authorization is checked on join and on every send. Support typing indicators, automatic reconnect, and gap recovery: on reconnect the client sends its last sequence number per room and the server replays what it missed.
7. Idempotent sends: the client generates a message id, so a retry after a dropped connection never creates a duplicate.
8. Edited messages show an Edited label; deleted messages show a tombstone for members while the original stays available to compliance admins only.

Tests: ordering under concurrent sends, duplicate-send retry, reconnect replay, and that a user removed from a channel stops receiving its events immediately.
```

### Prompt 4: files

```text
Task: file sharing in channels and chats.

Build:
1. Upload with an S3-compatible presigned, short-lived URL against MinIO, so file bytes never pass through the API process. Hide the store behind an IFileStore interface so it can be swapped. Record a FileAsset and FileVersion after the upload is confirmed.
2. Attach files to messages. A file in a channel is accessible to that channel's members; a file in a direct or group chat is accessible to its participants. Downloads use short-lived signed links issued only after an authorization check.
3. Version history: uploading a file with the same name in the same channel creates a new version; users can list and download earlier versions.
4. Limits and safety: configurable max size and allowed types, content-type sniffing, ClamAV scanning that marks a file as pending until cleared, and filename sanitizing.
5. Preview generation in a separate Python FastAPI service fed from a job queue: thumbnails for images, first-page previews and text extraction for PDFs and Office files, with the extracted text indexed for search. The client shows inline previews, and a files tab per channel listing all files with uploader, date and size.
6. Deleting a message does not silently delete its file; deleted files go to a recoverable state for a configurable number of days.

Tests: access checks for each role, version creation, an oversized file, a blocked file type, and an expired signed link.
```

### Prompt 5: activity feed, notifications, presence and search

```text
Task: the features that make the tool feel alive.

Build:
1. Activity feed: items for @mentions, @channel mentions, replies to my messages, reactions on my messages, and being added to a channel or workspace. Filter by type, an unread-only toggle, mark all as read, and a red unread badge. Items expire after 30 days.
2. Notification settings per user, with three modes per category (chats, channels, mentions): banner and feed, feed only, off. Per-channel mute that silences normal posts but still notifies on direct @mentions of me. Browser push using the standard Web Push protocol with VAPID keys (no third-party service) and an optional email digest through our own SMTP server, both respecting these settings.
3. Presence: available, busy, do not disturb, away, offline, plus a custom status message. Presence is ephemeral (kept in memory or a cache, not the main database) and set by heartbeat with automatic away after inactivity. Do not disturb mutes banners and push but does not change saved notification settings.
4. Unread counts per channel and conversation, with a last-read marker and a jump to first unread.
5. Search: messages and files from Mode's own database and people from the host's users table through IUserDirectory (never copied into Mode's search index; guests get no people search), all in one box, with filters for channel, author and date. Use database full-text search. Results must respect permissions: never return a message the user cannot open. Add tests that prove this for private channels and removed members.

Tests: badge counts, mute-with-mention behavior, do-not-disturb suppression, 30-day expiry job, and permission-filtered search.
```

### Prompt 6: the web client

```text
Task: build the React + TypeScript client. It must look and feel like a finished product, not a template.

Before coding, propose a design direction in a short note: type scale, a restrained color palette with light and dark themes, spacing system, and component list. Wait for approval, then build.

Embedding: the client ships as a module that mounts inside the host application and receives the access token and theme from the host through a small adapter, with no sign-in screens of its own. Provide a standalone dev shell with a fake token for local work.

Layout: a narrow left rail (Activity, Chat, Channels, Files), a collapsible channel and conversation list with unread badges and presence dots, a main message pane, and a right pane for thread, files or members.

Requirements:
- Composer with Markdown shortcuts, @mention autocomplete (channel members first, backed by the user directory), emoji picker, drag-and-drop file upload with progress, and draft saving per channel.
- Virtualized message list with smooth scrolling, day separators, grouped consecutive messages, optimistic sends with retry, and a New messages marker.
- A command bar (Ctrl or Cmd + K) for jumping to channels and people and running search.
- Full keyboard navigation, visible focus states, ARIA roles, and WCAG AA contrast.
- Skeleton loading, helpful empty states, and clear offline and reconnecting banners.
- Responsive down to a phone-width layout.

State: use a typed API client generated from the OpenAPI spec, and a single realtime service that feeds the store. Add component tests and two end-to-end flows with Playwright: sign in then post and reply, and upload a file then find it by search.
```

## Build prompts, part 3: hardening and phase 2 and 3 features

### Prompt 7: phase 2 additions

```text
Task: add the phase 2 features on top of the working core.

Build: the private-channel UI (lock icon, add and remove members, visible to owners under channel management but not in the channel list unless they are members); pinned messages per channel; message forwarding and permalinks that respect access; guest accounts with an invitation flow and an expiry date; per-channel member list with roles; and a message edit history view for the author.

For each feature, update the authorization matrix tests. Confirm that archiving a channel makes it read-only rather than deleting data.
```

### Prompt 8: admin, audit, retention and integrations

```text
Task: the governance layer IT will ask for.

Build:
1. Admin area (only for users whose host-issued token carries the admin role): workspace list, Mode role changes and workspace settings. User accounts are managed in the host application, so Mode has no create-user or deactivate-user screens.
2. Audit log viewer with filters and CSV export. Audit entries are append-only; no API can edit or delete them.
3. Retention: a per-workspace policy (keep forever, or keep N days then delete) applied by a nightly job. A legal-hold flag on a channel, conversation or user overrides deletion. When a keep rule and a delete rule conflict, keep the content. Edited and deleted message originals stay available to compliance admins until their retention period ends.
4. Compliance export: search messages and files by user, channel and date range, and export them with a signed manifest.
5. Incoming webhook: an admin creates a webhook URL for a channel; external systems POST JSON to post a formatted message as a named bot. Secrets are hashed at rest, rotatable, and rate-limited. Add a minimal bot identity so bot messages are visibly labeled.
6. Basic data-loss controls: an optional admin-defined list of blocked file types and a configurable pattern check (for example account-number-like strings) that warns or blocks before a message is sent.

Tests: retention with and without a hold, the conflict rule, audit immutability, webhook auth and rate limiting.
```

### Prompt 9: meetings (phase 3, optional)

```text
Task: add simple meetings without building our own media servers.

Use a self-hosted open-source video stack behind an interface called IMeetingProvider so it can be swapped: LiveKit server by default, or Jitsi Meet if a quicker embed is preferred. Add the docker-compose services, including a coturn TURN server, and document the ports to open.

Build: Meet now from any channel or chat; a scheduled meeting with a join link and a calendar file (.ics) download; a pre-join screen with camera and microphone check; mute, camera toggle, screen share, participant list, raise hand, and an in-meeting chat that is saved to the channel or conversation afterwards. Show a banner in the channel while a meeting is live. Meeting tokens are issued only after the authorization service confirms the user may join, and expire quickly.

Leave out for now: recording, live captions, breakout rooms, whiteboard, webinars. Note in docs/DECISIONS.md what each would need.

Tests: token issuance for allowed and denied users, and a provider-stub integration test.
```

### Prompt 10: security review and release

```text
Task: act as an adversarial reviewer of this codebase, then fix what you find. Do not just list risks.

Review against: broken access control (try cross-workspace id guessing, removed-member access, guest escalation, stale SignalR group membership), injection and XSS in message rendering, file upload abuse, signed-link leakage, webhook abuse, rate limiting and denial-of-service on search and uploads, dependency vulnerabilities, secrets in code or logs, and JWT validation (missing audience or issuer checks, algorithm confusion, expired and replayed tokens) and token handling on SignalR connections.

For each finding: severity, how to reproduce it with a failing test, the fix, and the passing test. Then produce: a threat-model summary in docs/SECURITY.md, a backup and restore runbook, a monitoring and alerting checklist, a load test of 1,000 concurrent connected users sending messages, and a deployment guide for a single Linux server using Docker Compose and a Caddy reverse proxy with automatic TLS, with environment configuration and a rollback plan.

Finish with a go or no-go release checklist and the open risks I should accept explicitly.
```

## Sources

Research drew on these search results (snippets, not full pages). The scope, stack and prompts are my own recommendations.

- [Standard, private, or shared channels in Microsoft Teams](https://support.microsoft.com/office/standard-private-or-shared-channels-in-microsoft-teams-de3e20b0-7494-439c-b7e5-75899ebe6a0e)
- [Private channels (Microsoft Learn)](https://learn.microsoft.com/en-in/MicrosoftTeams/private-channels)
- [Create a standard, private, or shared channel](https://support.microsoft.com/topic/fda0b75e-5b90-4fb8-8857-7e102b014525)
- [Explore the Activity feed in Microsoft Teams](https://support.microsoft.com/en-us/office/explore-the-activity-feed-in-microsoft-teams-91c635a1-644a-4c60-9c98-233db3e13a56)
- [Activity feed basics (30-day expiry)](https://wus.prod.support.services.microsoft.com/en-au/office/02b9ece4-6086-4daa-b4c0-627beeacbf08)
- [Retention policies for Teams (Microsoft Learn)](https://learn.microsoft.com/sr-latn-rs/MicrosoftTeams/retention-policies)
- [Teams security and compliance overview](https://learn.microsoft.com/microsoftteams/security-compliance-overview)
- [Teams extensibility points](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/extensibility-points)
- [Expert Microsoft Teams Solutions: architecture](https://www.packtpub.com/programming/expert-microsoft-teams-solutions)
- [Teams admin overview: what gets created with a team](https://learn.microsoft.com/nl-nl/microsoftteams/teams-overview)
- [Introducing Microsoft Teams (USNH)](https://td.usnh.edu/TDClient/60/Portal/KB/PrintArticle?ID=695)
- [Manage membership of a team or channel (Bowdoin)](https://bowdoin.teamdynamix.com/TDClient/1814/Portal/KB/Article/171194/Manage-Membership-of-a-Team-or-Channel?SIDs=11645)
- [Settings and notifications (FAU)](https://helpdesk.fau.edu/TDClient/2061/Portal/KB/Article/162808/Settings-and-Notifications)
