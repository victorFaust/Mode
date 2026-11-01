# Authentication and roles

Mode has no login screen and stores no credentials. The host application signs users in and issues a JWT. Mode validates it on every request and reads the person from the host's users table.

## Token contract

| Claim | Default name | Required | Use |
| --- | --- | --- | --- |
| Subject | `sub` | yes | Host user id. Looked up in the host users table; at most 64 characters. |
| Name | `name` | no | Ignored for identity: the display name comes from the users table. |
| Email | `email` | no | Same as name. |
| Roles | `roles` | no | String or array. Mapped to Mode roles (below). |
| Expiry | `exp` | yes | Tokens without `exp` are rejected. |
| Issuer | `iss` | yes | Must equal `Auth:Issuer`. |
| Audience | `aud` | yes | Must equal `Auth:Audience`. |
| Token id | `jti` | recommended | Used to record one sign-in audit entry per token. |

The claim names are configurable under `Auth:Claims` (`Subject`, `Name`, `Email`, `Roles`).

## Validation rules

- **Signature:** only asymmetric algorithms are accepted (RS256/384/512, PS256, ES256/384). This rejects `alg: none` and HMAC tokens signed with the public key (algorithm confusion).
- **Signing keys:** configure exactly one of these.
  - `Auth:MetadataAddress`: the OpenID Connect discovery URL. Keys are read from its `jwks_uri` and refreshed automatically when the host rotates them.
  - `Auth:PublicKeyPem`: a fixed RSA or EC public key.
- **Claims and lifetime:** issuer, audience, expiry and not-before are always checked. Clock skew is `Auth:ClockSkewSeconds` (default 30).
- **Host directory check:** after the signature is valid, the subject must exist and be active in the host users table. If not, the request gets **401**. Lookups are cached for `UserDirectory:CacheMinutes`, so a deactivation takes effect within that time.
- **Id normalization:** the subject is normalized through `IUserDirectory` (for example, GUIDs are lowercased). That normalized id is the only identity Mode stores.

## Where the token goes

- **REST:** `Authorization: Bearer <token>`.
- **SignalR (`/hubs/mode`):** the same header, or the `access_token` query parameter for WebSockets. The query parameter is accepted **only** on `/hubs/*` paths; anywhere else it is ignored.
- **Expiry on realtime connections:** each connection is closed when its token expires (`CloseOnAuthenticationExpiration`). The client must get a fresh token from the host and reconnect. A reconnect is a new connection, so the token is validated again.

## Roles

There are two layers of roles:

1. **Host roles**, mapped once in `Auth:RoleMap` (host role name to Mode role, case-insensitive). Unmapped host roles are ignored.

   | Host role (default) | Mode role | Effect |
   | --- | --- | --- |
   | `mode-admin` | Admin | Admin area (Prompt 8). No content access by itself. |
   | `guest` | Guest | Always treated as a workspace **guest**, whatever role is stored. Cannot create workspaces or search the directory. |

2. **Workspace roles** (Owner, Member, Guest), stored by Mode per workspace. See the permission rules in [ARCHITECTURE.md](ARCHITECTURE.md) and `AccessPolicy`.

## Audit

- `auth.sign_in` is recorded the first time each token is used (keyed by `jti`).
- `auth.realtime_connected` is recorded for each SignalR connection.

## Local development

`tools/Mode.MockIssuer` plays the host. `appsettings.Development.json` points `Auth:MetadataAddress` at it.

```powershell
$token = (Invoke-RestMethod -Method Post http://localhost:5100/token -ContentType 'application/json' `
  -Body '{"sub":"0a1b0001-0000-4000-8000-000000000001","roles":["mode-admin"]}').access_token
Invoke-RestMethod http://localhost:5080/api/v1/me -Headers @{ Authorization = "Bearer $token" }
```

## Production configuration

Set these environment variables:

```
Auth__Issuer=https://host.example/
Auth__Audience=mode-api
Auth__MetadataAddress=https://host.example/.well-known/openid-configuration   # or Auth__PublicKeyPem
Auth__RoleMap__<host-admin-role>=Admin
Auth__RoleMap__<host-guest-role>=Guest
```

The API refuses to start if `Auth:Issuer` or `Auth:Audience` is missing, if neither or both key sources are set, or if `Auth:RoleMap` contains an unknown Mode role.
