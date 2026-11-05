# Integrating Mode into the host application

Mode is embedded in the bank's existing (host) application. The host owns people, sign-in and roles; Mode owns messages, files, rooms and their governance. This is the checklist for the host team. Token details are in [AUTH.md](AUTH.md); security risks in [SECURITY.md](SECURITY.md).

## 1. Who does what

| Area | Host application | Mode |
| --- | --- | --- |
| People | Owns the users table: stable ids, display name, email, active flag. | Reads it (read-only), caches for `UserDirectory:CacheMinutes`. |
| Sign-in | Signs people in, issues short-lived JWTs for Mode, renews them. | Validates every token; has no login screen or passwords. |
| Sign-out, deactivation | Stops issuing tokens at once and calls `mode.unmount()`; sets `IsActive = 0`. | Refuses inactive people; closes their live connections within about 6 minutes (R2). |
| Roles | Puts `mode-admin` / `guest` in the `roles` claim for the right people. | Maps them (`Auth:RoleMap`); manages workspace roles itself. |
| Hosting the client | Loads the bundle and mounts it in a page. | Serves the bundle at `/mode/` and the API. |
| Audit | Sign-ins, role grants, deactivations. | Everything that happens inside Mode (tamper-evident, F8). |

## 2. Tokens (closes most of R3)

Required claims: `sub`, `exp`, `iat`, `iss`, `aud`; recommended `jti` and `roles`.

- **Lifetime 60 minutes or less.** Mode refuses longer tokens (`Auth:MaxTokenLifetimeMinutes`, default 60). Shorter (15 to 30 minutes) is better: a stolen token is useful only until it expires.
- **Audience `mode-api`** (or the agreed value). Do not reuse tokens issued for other systems.
- **Asymmetric signing** (RS256, PS256 or ES256) with keys published at an OpenID Connect metadata URL, so key rotation needs no Mode redeploy.
- **Renew silently.** `getToken()` is called before every request and every realtime reconnect; return a cached token while it has more than about 2 minutes left, otherwise fetch a new one.
- **Never** put Mode tokens in URLs, `localStorage` or logs. Keep them in memory.

## 3. Mounting the client

Allow the host page to load the bundle from `https://<mode-domain>/mode/` (built with `vite build --mode lib`, served by Caddy). Add the stylesheet, then mount:

```html
<link rel="stylesheet" href="https://mode.your-bank.example/mode/mode.css">
<div id="mode" style="height: 100%"></div>
```

```ts
import { mountMode } from "https://mode.your-bank.example/mode/mode.js";

let cached: { token: string; expiresAt: number } | null = null;
async function getModeToken(): Promise<string> {
  if (cached && cached.expiresAt - Date.now() > 120_000) return cached.token;
  // Host endpoint that issues a Mode token for the signed-in user (same-origin, session cookie).
  const res = await fetch("/auth/mode-token", { method: "POST", credentials: "same-origin" });
  if (res.status === 401) { window.location.assign("/login"); throw new Error("signed out"); }
  const body = await res.json() as { access_token: string; expires_at: string };
  cached = { token: body.access_token, expiresAt: Date.parse(body.expires_at) };
  return cached.token;
}

const mode = mountMode(document.getElementById("mode")!, {
  apiBaseUrl: "https://mode.your-bank.example",
  getToken: getModeToken,
  onUnauthorized: () => { cached = null; },          // the next call fetches a fresh token
  theme: { mode: "system" },
  serviceWorkerUrl: "/mode-sw.js",                   // optional: browser push; copy client/public/mode-sw.js to the host origin
  messageLink: (id) => `${location.origin}/chat#mode-message=${id}`,
});

// On sign-out or session timeout:
function signOut() { mode.unmount(); cached = null; /* then the host's own sign-out */ }
```

If the host uses `messageLink`, it must call `mode.openMessage(id)` when such a link is opened.

## 4. Browser settings on the host page

Add the Mode origin to the host's Content-Security-Policy:

```
script-src  'self' https://mode.your-bank.example;
style-src   'self' https://mode.your-bank.example;
connect-src 'self' https://mode.your-bank.example wss://mode.your-bank.example;
img-src     'self' data: blob: https://mode.your-bank.example;
worker-src  'self';
```

The host origin goes into Mode's `HOST_APP_ORIGIN` (the only origin CORS allows). Both sites must use HTTPS.

## 5. Read-only access to the users table (gate G6)

Run on the host SQL Server, adjusting names to the real table and columns (also set in Mode's `UserDirectory__*` settings):

```sql
CREATE LOGIN [mode_reader] WITH PASSWORD = N'<strong password>', CHECK_POLICY = ON;
USE [HostApp];
CREATE USER [mode_reader] FOR LOGIN [mode_reader];
GRANT SELECT ON dbo.Users TO [mode_reader];   -- nothing else
```

Mode needs: id (stable, never reused), display name, email, optional avatar URL, active flag. Connect with `Encrypt=True`. Mode queries by id and by name prefix; an index on the display name helps.

## 6. Session timeout and deactivation (reduces R2)

- When the host session ends (sign-out or idle timeout), call `mode.unmount()` and stop returning tokens.
- When someone leaves or is suspended, stop issuing tokens immediately and set them inactive in the users table. Existing tokens keep working only until they expire (at most 60 minutes, and REST access stops once Mode's directory cache refreshes: 5 minutes by default).
- For urgent cases, lower Mode's `UserDirectory:CacheMinutes` and `Realtime:RecheckSeconds`; both trade speed for more reads on the host SQL Server.

## 7. What the host team must send the Mode team

| Item | Mode setting |
| --- | --- |
| Issuer (`iss`) and audience (`aud`) values | `AUTH_ISSUER`, `AUTH_AUDIENCE` |
| OpenID metadata URL (or public key) | `AUTH_METADATA_ADDRESS` / `AUTH_PUBLIC_KEY_PEM` |
| Token lifetime | must be ≤ `Auth:MaxTokenLifetimeMinutes` (60) |
| Role names for Mode admins and guests | `Auth__RoleMap__<name>` |
| Users table name, id type and column names | `UserDirectory__Table`, `__IdType`, `__IdColumn`, … |
| `mode_reader` connection string | `HOST_DIRECTORY_CONNECTION_STRING` |
| Host origin(s) | `HOST_APP_ORIGIN` |

## 8. Acceptance tests with the host

1. A real host token signs in; `GET /api/v1/me` shows the right name and roles.
2. A token for another audience, an expired token, and a token longer than 60 minutes are refused (401).
3. Deactivate a test person: new tokens are refused at once; their open Mode tab stops receiving messages within about 6 minutes.
4. Sign out in the host: the Mode panel disappears and no further API calls are made.
5. A `mode-admin` sees Admin; a `guest` cannot search the directory.
6. The host page's CSP shows no violations in the browser console while chatting, uploading and receiving realtime messages.
