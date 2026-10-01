# Fate & Fridge — Internal API Contract

## 1. API Philosophy
V1 is internally API-first.

The web frontend uses the same backend domain/API surface that future clients can reuse.

Base path:

```text
/api/v1
```

External API keys/service accounts are deferred.

---

## 2. API Families

```text
/api/v1/setup
/api/v1/auth
/api/v1/users
/api/v1/settings
/api/v1/calendar
/api/v1/meals
/api/v1/recipes
/api/v1/ingredients
/api/v1/units
/api/v1/proteins
/api/v1/tags
/api/v1/groceries
/api/v1/statistics
/api/v1/import
/api/v1/admin/audit
/api/v1/admin/history
/api/v1/admin/backup
```

---

## 3. General Rules
- JSON request/response
- JSON property names are snake_case
- consistent error envelope
- UTC timestamps
- household-local calendar dates represented as ISO date strings
- pagination for potentially large collections
- server-side authorization on every protected route
- JSON request bodies are limited to 16 KiB for settings and user payloads; oversize or malformed bodies return `400 VALIDATION_FAILED` with `details.field_errors.body`. A present `Content-Type` must be `application/json` (charset parameters allowed) or the request returns `415 UNSUPPORTED_MEDIA_TYPE`; a missing `Content-Type` is accepted for compatibility.
- no business logic duplicated in frontend

### Permission enforcement

- Permission keys are enforced server-side on every protected route; the frontend must not rely on hidden UI.
- Keys: `calendar.view`, `calendar.edit`, `groceries.view`, `groceries.edit`, `recipes.view`, `recipes.edit`, `reference_data.edit`.
- Edit implies view: granting an edit key effectively grants the matching view key (`reference_data.edit` has no view counterpart).
- The single-user system actor and Admins hold all keys by explicit bypass; members are evaluated from their granted rows plus implied view keys.
- Unauthenticated requests receive `401 AUTH_REQUIRED`; authenticated requests without the required key receive `403 FORBIDDEN`.
- Grants are read on every request, so permission changes apply on the next request (no session caching).

---

## 4. Example Error Shape

```json
{
  "error": {
    "code": "CALENDAR_DATE_HISTORICAL",
    "message": "Historical calendar entries cannot be edited in normal mode.",
    "details": {}
  }
}
```

Use stable error codes.

### M1 error codes

| Code | HTTP | Meaning |
|------|------|---------|
| `SETUP_REQUIRED` | 409 | Setup is not complete; only `/health` and `/setup/*` are callable |
| `SETUP_ALREADY_COMPLETED` | 409 | `POST /setup/complete` was called after setup finished |
| `VALIDATION_FAILED` | 400 | Request body/query failed validation; `details.field_errors` present |
| `UNSUPPORTED_MEDIA_TYPE` | 415 | A JSON-body endpoint received a present `Content-Type` that is not `application/json` (charset parameters allowed) |
| `AUTH_REQUIRED` | 401 | No valid session |
| `CSRF_VALIDATION_FAILED` | 403 | Unsafe request carried a session cookie but the antiforgery token was missing or invalid |
| `INVALID_CREDENTIALS` | 401 | Login failed (generic; never reveals whether the username exists) |
| `ACCOUNT_LOCKED` | 423 | Account temporarily locked; `details.locked_until` present |
| `RATE_LIMITED` | 429 | Too many attempts; `Retry-After` header present |
| `NOT_FOUND` | 404 | The requested entity does not exist |
| `INTERNAL_ERROR` | 500 | Unexpected server failure; `message` may carry an operator-facing hint |
| `LAST_ADMIN_PROTECTED` | 409 | Operation would remove the last active Admin |
| `AUTH_MODE_ALREADY_MULTIUSER` | 409 | The household already uses multiuser mode |
| `PASSWORD_CHANGE_REQUIRED` | 403 | Session must complete a forced password change first |
| `FORBIDDEN` | 403 | Authenticated but missing the required permission |
| `IP_NOT_ALLOWED` | 403 | Client address is outside the configured IP allowlist |
| `AUTH_MODE_DOWNGRADE_NOT_SUPPORTED` | 409 | Multiuser → single-user conversion is deferred |

Clients must branch on `code`, never on `message`.

---

## 5. Auth

Possible routes:
- `POST /auth/login`
- `POST /auth/logout`
- `GET /auth/session`
- `POST /auth/change-password`

Admin:
- `POST /users/{id}/reset-password`

Rate limits apply to authentication routes.

Session model (approved M1):
- Login sets an opaque, HttpOnly session cookie (`SameSite=Lax`, `Secure` when served over HTTPS).
- Authenticated state-changing requests must present an antiforgery token; the frontend reads the token from the `X-CSRF-TOKEN` response header and echoes it in the `X-CSRF-TOKEN` request header on unsafe requests. Missing or invalid tokens fail with `403 CSRF_VALIDATION_FAILED`; safe methods and unauthenticated requests (including stale or invalid session cookies) are not validated, so those requests fall through to normal `401 AUTH_REQUIRED` handling.
- Session lifetime is 30 days idle / 90 days absolute, with rotation on login, password change, and privilege change.
- Login, session, change-password, and reset-password responses are sent with `Cache-Control: no-store`.
- `GET /auth/session` returns the current user (or single-user household identity), effective permissions, auth mode, and `must_change_password`.
- When `must_change_password` is true, every route except `/auth/session`, `/auth/change-password`, and `/auth/logout` fails with `PASSWORD_CHANGE_REQUIRED`.

### M1 auth response shapes

`POST /auth/login` (public). Request:
```json
{ "username": "smith", "password": "correct horse battery staple" }
```

Responses:
- `200` — same payload as the multiuser `GET /auth/session` shape below. Sets a persistent `faf_session` cookie with the 30-day idle window and returns a fresh `X-CSRF-TOKEN` header.
- `400 VALIDATION_FAILED` — missing, blank, or non-string `username`/`password`, or malformed JSON; `details.field_errors` present.
- `401 INVALID_CREDENTIALS` — generic; the body is identical for unknown usernames, inactive users, and wrong passwords.
- `423 ACCOUNT_LOCKED` — `details.locked_until` present (ISO 8601). Defaults (docs/SECURITY.md §3): 10 consecutive failures engage a 15-minute lock; 5 lockouts within 24 hours escalate to 24 hours. A future `locked_until` short-circuits before password verification; the failure that engages the lock still returns `401`.
- `429 RATE_LIMITED` — `Retry-After` header (whole seconds until the window resets). Per-IP defaults: 20 login attempts per 10 minutes (`Auth:LoginThrottle`). Every attempt reserves a slot before verification and a successful login clears that address's slots, so effectively only failures consume the budget; over-limit attempts are rejected before any credential check.

`POST /auth/logout` (requires a valid session and the `X-CSRF-TOKEN` header):
- `204` — revokes the current session server-side and deletes the cookie.
- `401 AUTH_REQUIRED` without a valid session.

`POST /auth/change-password` (requires a valid session and the `X-CSRF-TOKEN` header). Request:
```json
{ "current_password": "correct horse battery staple", "new_password": "a much better passphrase" }
```
- `204` — password updated. The current session is rotated (fresh `faf_session` cookie and `X-CSRF-TOKEN`), every other session of the user is revoked with reason `password_change`, `must_change_password` is cleared, and a `password_change` auth event is written.
- `400 VALIDATION_FAILED` — missing, blank, or non-string fields, malformed JSON, a `new_password` outside 12–128 characters, or a `new_password` identical to `current_password`; `details.field_errors.new_password` is present for the password-policy cases.
- `401 INVALID_CREDENTIALS` — generic failure when `current_password` is wrong.
- `401 AUTH_REQUIRED` without a valid session.

Forced-change gate: while `must_change_password` is true, every `/api/v1` route fails with `403 PASSWORD_CHANGE_REQUIRED` except `GET /auth/session`, `POST /auth/change-password`, and `POST /auth/logout`; `/health` and `/setup/*` remain reachable. The gate runs before antiforgery validation, so gated routes return `PASSWORD_CHANGE_REQUIRED` even when the CSRF token is missing.

`GET /auth/session`, multiuser authenticated:
```json
{
  "authenticated": true,
  "auth_mode": "multiuser",
  "user": { "id": 1, "username": "smith", "role": "admin" },
  "permissions": ["calendar.view", "calendar.edit", "groceries.view", "groceries.edit", "recipes.view", "recipes.edit", "reference_data.edit"],
  "must_change_password": false
}
```
Multiuser unauthenticated: `401 AUTH_REQUIRED`. Single-user:
```json
{
  "authenticated": true,
  "auth_mode": "single_user",
  "user": null,
  "permissions": ["calendar.view", "calendar.edit", "groceries.view", "groceries.edit", "recipes.view", "recipes.edit", "reference_data.edit"],
  "must_change_password": false
}
```

Effective permissions: Admins hold all seven keys; members hold their granted keys plus the matching view key for every granted edit key.

`POST /users/{id}/reset-password` (Admin in multiuser mode, or the single-user system actor; `X-CSRF-TOKEN` required). Response `200`:
```json
{ "temporary_password": "Kd7mQ2xRf9Tn4Wpz8Hs6" }
```
- The temporary password is returned exactly once, never logged or stored in plaintext, satisfies the 12–128 policy, and uses an unambiguous alphabet (no `0/O/1/I/l`).
- Sets the target's `must_change_password`, clears all lockout state (`failed_login_count`, `last_failed_login_at`, `locked_until`, `lockout_count`, `lockout_window_started_at`) so a locked user can log in again, revokes every session of the target with reason `password_reset`, and writes a `password_reset` auth event whose `metadata_json` carries `actor_user_id`. The next login with the temporary password is subject to the forced-change gate.
- `401 AUTH_REQUIRED` without a valid session; `403 FORBIDDEN` for a non-Admin member; `404 NOT_FOUND` for an unknown user id. Resetting an inactive user succeeds (the Admin may be preparing to re-enable them), but login stays blocked until the user is re-enabled (M1-10).

Password-lifecycle auth event types: `password_change`, `password_reset` (alongside `login_success`, `login_failure`, and `account_locked`).

Login CSRF note: `POST /auth/login` is unauthenticated, so the antiforgery check does not apply (M1-06 decision). The JSON-body requirement and SameSite=Lax session cookie are the interim cross-site mitigations; M1-15 revisits login CSRF explicitly.

---

## 6. Users
- `GET /users`
- `POST /users`
- `GET /users/{id}`
- `PATCH /users/{id}`
- `PATCH /users/{id}/permissions`
- `POST /users/{id}/disable`
- `POST /users/{id}/enable`

Admin only. New members default to view-only (`calendar.view`, `groceries.view`, `recipes.view`); demoting an Admin to member seeds those view-only defaults only when the user has no granted rows (existing grants are preserved); admin-created accounts must change their password on first login.

---

## 7. Calendar

### Read
- `GET /calendar?start=YYYY-MM-DD&end=YYYY-MM-DD`
- `GET /calendar/{date}`

### Mutate
- `PUT /calendar/{date}`
- `DELETE /calendar/{date}` only for permitted future/today entries
- `POST /calendar/{date}/lock`
- `POST /calendar/{date}/unlock`

Past-date mutation must fail unless using Admin historical-correction API.

---

## 8. Randomization

### Single Date
- `POST /calendar/{date}/reroll`

Behavior:
- immediate
- returns replacement
- returns undo token/operation id where supported

### Preview 7 Days
- `POST /calendar/reroll/preview`

Body example:
```json
{
  "start_date": "2026-10-06",
  "mode": "seven_days"
}
```

### Rest of Month Preview
Same route with:
```json
{
  "start_date": "2026-10-06",
  "mode": "rest_of_month"
}
```

### Try Another
Generate a fresh preview, not persisted.

### Apply
- `POST /calendar/reroll/apply`

Must be atomic.

### Fill Empty
- `POST /calendar/fill-empty`

---

## 9. Undo
Potential route:
- `POST /operations/{operation_id}/undo`

Undo must verify:
- operation still undoable
- actor has required permission
- no conflicting subsequent change invalidates undo

---

## 10. Recipes
- `GET /recipes`
- `POST /recipes`
- `GET /recipes/{id}`
- `PATCH /recipes/{id}`
- `POST /recipes/{id}/retire`
- `POST /recipes/{id}/activate`
- `POST /recipes/{id}/favorite`
- `POST /recipes/{id}/unfavorite`

Admin destructive:
- `DELETE /admin/recipes/{id}`

---

## 11. Ingredients
- `GET /ingredients`
- `POST /ingredients`
- `PATCH /ingredients/{id}`
- `POST /ingredients/{id}/retire`
- `POST /ingredients/merge`

Admin hard delete only when unused:
- `DELETE /admin/ingredients/{id}`

---

## 12. Units / Proteins / Tags
Standard CRUD-ish reference-data routes with permission checks.

No silent deletion when referenced.

---

## 13. Grocery List

Read:
- `GET /groceries?start=...&days=...`
- presets may map to resolved ranges server-side

Manual items:
- `POST /groceries/manual`
- `PATCH /groceries/manual/{id}`
- `DELETE /groceries/manual/{id}`

Check state:
- `POST /groceries/check`
- `POST /groceries/uncheck`

Staples:
- `GET /groceries/staples`
- `POST /groceries/staples`
- `POST /groceries/staples/{id}/add-to-list`

---

## 14. Statistics
- `GET /statistics/overview`
- `GET /statistics/recipes/{id}`
- `GET /statistics/history?start=...&end=...`

Keep API flexible enough for additional metrics later.

---

## 15. Import

### Start
- `POST /import/recipe-url`

Returns parsed draft only.

No permanent recipe created.

### Review/Save
Client edits draft and submits through normal recipe creation endpoint.

The import route must not bypass ingredient-matching review.

---

## 16. Admin Audit
- `GET /admin/audit`
- filters by date/user/event/entity

Admin only.

---

## 17. Historical Correction
- `GET /admin/history/{date}`
- `POST /admin/history/{date}/correct`

Correction request must include:
- reason
- explicit confirmation flag
- desired replacement

Backend verifies Admin role regardless of frontend state.

---

## 18. Backup
- `POST /admin/backup`
- `GET /admin/backups`
- `POST /admin/restore/validate`
- `POST /admin/restore/apply`
- `POST /admin/recovery-key/export`

Exact download mechanism may use streamed responses.

---

## 19. WebSocket
Suggested endpoint:

```text
/ws
```

Event examples:
- `calendar.updated`
- `groceries.updated`
- `recipe.updated`
- `settings.updated`

Payloads should carry:
- event type
- resource identity
- operation id
- timestamp

Clients can invalidate/refetch relevant queries.

---

## 20. Future API Compatibility
Future voice/integration operations should be possible without new domain logic:

- read today's dinner
- read tomorrow's dinner
- read dinner for arbitrary date
- add grocery item
- read grocery list
- reroll date
- select recipe for date
- read upcoming meals

The API should expose domain actions cleanly enough that future service accounts can reuse them.

---

## 21. M1 Contract: Setup, Settings, Sessions

### Setup

- `GET /setup/status` (public while unconfigured)
  ```json
  { "setup_completed": false, "auth_mode": null, "recovery_key_present": false }
  ```
- `POST /setup/complete` (public, single-shot, atomic)
  Request:
  ```json
  {
    "household_name": "The Smiths",
    "timezone": "America/Chicago",
    "week_starts_on": 0,
    "default_diner_count": 4,
    "startup_page": "calendar",
    "auth_mode": "multiuser",
    "admin": { "username": "smith", "password": "correct horse battery staple" },
    "ip_allowlist": { "enabled": false, "ranges": [] },
    "update_check_enabled": true
  }
  ```
  `admin` is required if and only if `auth_mode` is `multiuser`. `week_starts_on` is 0–6 (Sunday = 0, default 0). `startup_page` is `calendar` or `dashboard`. `admin.username` is 3–32 characters using ASCII letters, digits, `"."`, `"_"`, or `"-"`; it is stored trimmed and its lowercase lookup form (`normalized_username`) is derived from it. `admin.password` follows the 12–128 character policy. `ip_allowlist` is optional; when `enabled` is true at least one valid IPv4/IPv6 CIDR range is required, and a bare IPv4/IPv6 address is accepted as a single-host range (`/32` or `/128`). Enabling the allowlist during setup also requires the caller's resolved client address to be covered by at least one range (loopback callers are exempt); otherwise setup fails with `400 VALIDATION_FAILED` on `ip_allowlist.ranges` and nothing is written, so the device completing setup cannot lock itself out. The explicit setup payload is the confirmation for enabling; the M1-13 wizard shows the lockout warning. Validation failures return `400 VALIDATION_FAILED` with `details.field_errors`.
  Response `201`:
  ```json
  {
    "auth_mode": "multiuser",
    "recovery_key": "<base64url, 32 bytes>",
    "session": { "user": { "...": "..." } }
  }
  ```
  The recovery key is returned exactly once here. It is stored at `/data/config/recovery.key` (mode 0600), never logged, and only exportable later via the M13 admin export route. An existing key file is reused, never overwritten. In multiuser mode the response also establishes the first Admin session (same payload, cookie, and `X-CSRF-TOKEN` as login); single-user responses omit `session`. Repeated calls return `409 SETUP_ALREADY_COMPLETED`, and concurrent calls are serialized so exactly one succeeds.

### Household and security settings

- `GET /settings/household`, `PATCH /settings/household` — name, timezone (IANA, validated), `week_starts_on`, `default_diner_count` (1–20), `startup_page`. Admin-only for PATCH; any authenticated/single-user caller may GET. `household_name` length is measured in UTF-16 code units (the same unit the server validates), so astral characters such as emoji count as two. `PATCH` is a last-write-wins partial update: concurrent disjoint patches all persist, and concurrent writes to the same field keep the last committed value (no optimistic concurrency). `ETag`/`If-Match` preconditions are deferred to V1.x.
- `GET /settings/security`, `PATCH /settings/security` — Admin-only. Fields: `ip_allowlist_enabled` (bool), `ip_allowlist_ranges` (array of IPv4/IPv6 addresses or CIDRs), `trusted_proxies` (array, same rules). Bare addresses normalize to single-host ranges (`/32` or `/128`); legacy IPv4 shorthand/hex forms and scope IDs are rejected. Enabling the allowlist requires `confirmed: true` in the same PATCH, at least one range, and coverage of the caller's resolved client address (a loopback caller is exempt because loopback is always allowed); otherwise `400 VALIDATION_FAILED` with `details.field_errors`. Disabling requires no confirmation. GET returns the normalized stored values and changes apply to the next request.
- Allowlist enforcement: when enabled, every route except `/api/v1/health/*` is limited to loopback and clients covered by `ip_allowlist_ranges`; blocked clients receive `403 IP_NOT_ALLOWED` **before** authentication and setup gating, so a blocked client cannot observe setup state (an allowed client pre-setup still receives `409 SETUP_REQUIRED`).
- Trusted proxies: forwarded headers are honored only when the immediate socket peer is inside `trusted_proxies`. `X-Forwarded-Proto: https` then sets the request scheme, so `Secure` session/CSRF cookies work behind a TLS-terminating proxy, and `X-Forwarded-For` is walked right-to-left skipping trusted proxies to resolve the real client address. Forwarded headers from untrusted peers are ignored entirely. `/api/v1/health/*` bypasses both the forwarded-header resolution and the allowlist.
- If the security settings cannot be read (database outage), the allowlist is treated as disabled and forwarded headers as untrusted, with a warning log (fail open); the primary authentication boundary is unaffected.
- `POST /settings/auth-mode` — auth-mode switch (Admin-only; the single-user system actor may also call it).
  - Single-user → multiuser: body `{ "admin": { "username": "smith", "password": "..." } }` (an optional `"auth_mode": "multiuser"` is accepted). Creates the first Admin (3–32 character username, 12–128 character password), sets `household_settings.auth_mode`, and returns the signed-in multiuser session payload with the usual cookie and `X-CSRF-TOKEN` pair.
  - Already multiuser: `409 AUTH_MODE_ALREADY_MULTIUSER`.
  - Explicit single-user request (`"auth_mode": "single_user"`): `409 AUTH_MODE_DOWNGRADE_NOT_SUPPORTED` (deferred).
  - Invalid admin credentials or body: `400 VALIDATION_FAILED` with `details.field_errors`.

### User management

All routes require an Admin (or the single-user system actor); members receive `403 FORBIDDEN`.

- `GET /users` — list of `{ id, username, role, is_active, must_change_password, last_login_at, permissions }`, where `permissions` is the effective set (Admins hold all seven keys).
- `POST /users` — body `{ username, role?, password? }`; `role` defaults to `"member"`. Usernames are 3–32 characters using ASCII letters, digits, `"."`, `"_"`, or `"-"`, stored trimmed with a lowercase lookup form; duplicates return `400 VALIDATION_FAILED`. New members default to **view-only** (`calendar.view`, `groceries.view`, `recipes.view`); Admins hold all keys without rows. When `password` is omitted a one-time temporary password is generated and returned once as `temporary_password`. Admin-created accounts always start with `must_change_password = true`, so the first login is gated until the password is changed.
- `GET /users/{id}` — single user payload; `404 NOT_FOUND` for unknown ids.
- `PATCH /users/{id}` — body `{ username?, role? }` (at least one). Renames revalidate the username and reject duplicates. Changing a role revokes that user's sessions so the new role applies on the next login. Demoting an Admin to member seeds the view-only defaults (`calendar.view`, `groceries.view`, `recipes.view`) only when the user has no granted rows, so a demoted Admin is never left with zero visibility; existing grants are preserved. Demoting the last active Admin returns `409 LAST_ADMIN_PROTECTED`.
- `PATCH /users/{id}/permissions` — body `{ "permissions": ["calendar.edit", "recipes.view"] }` replaces the member's granted rows; unknown keys return `400 VALIDATION_FAILED`, Admin targets return `400` (Admins always hold all keys), duplicates are removed, and changes take effect on the next request.
- `POST /users/{id}/disable` — sets `is_active = false` and revokes all sessions immediately; the last active Admin is protected (`409 LAST_ADMIN_PROTECTED`).
- `POST /users/{id}/enable` — sets `is_active = true` and clears all lockout state (`locked_until`, `failed_login_count`, `lockout_count`, `lockout_window_started_at`) so the account can log in again; login stays generic while disabled.
- `POST /users/{id}/reset-password` — Admin issues a one-time temporary password (returned once), sets `must_change_password`, clears lockout state, and revokes the user's sessions.
- Safety rules: the last active Admin cannot be disabled, demoted, or have Admin removed; concurrent demotions/disables are serialized so the household always keeps at least one active Admin; temporary passwords are returned exactly once and never logged or stored in plaintext.

### Pre-setup gating

Until `POST /setup/complete` succeeds, every `/api/v1` route except `/health` and `/setup/*` returns `409 SETUP_REQUIRED`. The frontend routes to the wizard on that code.
