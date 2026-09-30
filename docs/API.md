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
- no business logic duplicated in frontend

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
| `AUTH_REQUIRED` | 401 | No valid session |
| `CSRF_VALIDATION_FAILED` | 403 | Unsafe request carried a session cookie but the antiforgery token was missing or invalid |
| `INVALID_CREDENTIALS` | 401 | Login failed (generic; never reveals whether the username exists) |
| `ACCOUNT_LOCKED` | 423 | Account temporarily locked; `details.locked_until` present |
| `RATE_LIMITED` | 429 | Too many attempts; `Retry-After` header present |
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
- `423 ACCOUNT_LOCKED` — `details.locked_until` present (ISO 8601).
- `429 RATE_LIMITED` — with `Retry-After`; part 2 of M1-07.

`POST /auth/logout` (requires a valid session and the `X-CSRF-TOKEN` header):
- `204` — revokes the current session server-side and deletes the cookie.
- `401 AUTH_REQUIRED` without a valid session.

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

Admin only.

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
  `admin` is required if and only if `auth_mode` is `multiuser`. `week_starts_on` is 0–6 (Sunday = 0, default 0). `startup_page` is `calendar` or `dashboard`.
  Response `201`:
  ```json
  {
    "auth_mode": "multiuser",
    "recovery_key": "<base64url, 32 bytes>",
    "session": { "user": { "...": "..." } }
  }
  ```
  The recovery key is returned exactly once here. It is stored at `/data/config/recovery.key` (mode 0600), never logged, and only exportable later via the M13 admin export route. In multiuser mode the response also establishes the first Admin session. Repeated calls return `409 SETUP_ALREADY_COMPLETED`.

### Household and security settings

- `GET /settings/household`, `PATCH /settings/household` — name, timezone (IANA, validated), `week_starts_on`, `default_diner_count` (1–20), `startup_page`. Admin-only for PATCH; any authenticated/single-user caller may GET.
- `GET /settings/security`, `PATCH /settings/security` — `ip_allowlist_enabled`, `ip_allowlist_ranges` (IPv4/IPv6 CIDR), `trusted_proxies` (CIDR). Admin-only. Enabling the allowlist requires `confirmed: true` in the PATCH body; the server validates that at least one range is present and that the caller's own address is covered.
- `POST /settings/auth-mode` — single-user → multiuser conversion; body carries the first Admin's `username`/`password`. Admin-only once configured. Multiuser → single-user returns `409 AUTH_MODE_DOWNGRADE_NOT_SUPPORTED` (deferred).

### User management

- Existing routes in section 6, plus:
  - `POST /users/{id}/reset-password` — Admin issues a one-time temporary password (returned once), sets `must_change_password`, and revokes the user's sessions.
  - `PATCH /users/{id}/permissions` — replaces the permission key set; edit keys imply their view key server-side.
- Safety rules: the last active Admin cannot be disabled, demoted, or have Admin removed; disabling a user revokes all sessions immediately.

### Pre-setup gating

Until `POST /setup/complete` succeeds, every `/api/v1` route except `/health` and `/setup/*` returns `409 SETUP_REQUIRED`. The frontend routes to the wizard on that code.
