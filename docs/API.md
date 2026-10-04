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
| `INGREDIENT_EXISTS` | 409 | An ingredient with that normalized name exists; `details.existing_id` present |
| `UNIT_EXISTS` | 409 | A unit with that normalized symbol exists; `details.existing_id` present |
| `PROTEIN_EXISTS` | 409 | A protein with that normalized name exists; `details.existing_id` present |
| `TAG_EXISTS` | 409 | A tag with that normalized name exists; `details.existing_id` present |
| `ALIAS_EXISTS` | 409 | The normalized alias is already used by another entity; `details.entity_type`/`existing_id` present |
| `DEPARTMENT_IN_USE` | 409 | Department still referenced by ingredients; retire instead |
| `UNIT_IN_USE` | 409 | Unit still referenced; retire instead |
| `PROTEIN_IN_USE` | 409 | Protein still referenced; retire instead |
| `TAG_IN_USE` | 409 | Tag still referenced; remove it from recipes first |
| `RECIPE_EXISTS` | 409 | A recipe with the same normalized name exists; `details.existing_id` present |
| `IMAGE_TYPE_NOT_ALLOWED` | 415 | Recipe image is not a JPEG, PNG, or WebP |
| `IMAGE_INVALID` | 400 | Recipe image content is malformed or does not match its declared type |
| `IMAGE_TOO_LARGE` | 413 | Recipe image exceeds the 5 MB file cap |
| `IMAGE_DIMENSIONS_EXCEEDED` | 400 | Recipe image exceeds 2048 pixels on a side |
| `INVALID_UNIT_DIMENSION_CHANGE` | 400 | Unit dimensions are immutable; retire and create a new unit |

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
- Single-user mode has no session cookie and issues no antiforgery token; unsafe requests act as the system actor and are not CSRF-validated. Trusted-network deployment (and optionally the IP allowlist) is the protection model for this mode.

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

Import is **stateless and draft-only** (M4). `POST /import/recipe-url` fetches
and parses a page and returns a draft; no recipe, ingredient, unit, tag,
protein, image, or audit row is written before the user reviews and saves.
Saving reuses `POST /recipes` (§23).

### Routes

| Route | Permission | Purpose |
|-------|------------|---------|
| `POST /api/v1/import/recipe-url` | `recipes.edit` | Fetch a URL and return every parsed candidate as a draft |
| `POST /api/v1/import/check` | `recipes.view` (or `recipes.edit`) | Re-check duplicate source URL / similar names after a rename |

### `POST /api/v1/import/recipe-url`

Request:
```json
{ "url": "https://example.com/recipe" }
```

`url` is required, non-blank, and must be an absolute HTTP/HTTPS URL. The
server performs the fetch through the SSRF-hardened fetcher (HTTP/HTTPS only,
DNS revalidated per hop, max 5 redirects, 15 s total read timeout, 2 MiB HTML,
allowed content types, identifying user agent) and never executes third-party
JavaScript (docs/SECURITY.md §6–§7). Parsing tries schema.org Recipe JSON-LD
first; when no JSON-LD `Recipe` exists it falls back to a static HTML parse. A
page with zero parseable recipes fails with `422 IMPORT_NO_RECIPE`.

Response `200` (shape fixed by docs/M4_PLAN.md §2.2; `recipes` always has ≥ 1
element):

```json
{
  "source": {
    "url": "https://example.com/recipe",
    "site": "example.com",
    "page_title": null,
    "author": "Jane Cook",
    "fetched_at": "2026-10-03T00:00:00+00:00",
    "parse_method": "json_ld"
  },
  "source_url_match": { "id": 5, "name": "Lasagna" },
  "capabilities": { "can_create_reference_data": false },
  "recipes": [
    {
      "index": 0,
      "name": "Lasagna",
      "description": null,
      "author": "Jane Cook",
      "source_url": "https://example.com/recipe",
      "source_site": "example.com",
      "prep_minutes": 20,
      "cook_minutes": 45,
      "notes": null,
      "image_source_url": "https://example.com/lasagna.jpg",
      "name_matches": [{ "id": 6, "name": "Lasagna Bolognese" }],
      "ingredients": [
        {
          "raw": "1 1/2 cups all-purpose flour, sifted",
          "quantity": 1.5,
          "name": "all-purpose flour",
          "preparation_note": "sifted",
          "matched_ingredient_id": null,
          "match_kind": null,
          "is_unknown": true,
          "suggestions": [],
          "unit": {
            "raw": "cups",
            "matched_unit_id": 3,
            "matched_symbol": "cup",
            "is_unknown": false,
            "suggested_dimension": null,
            "suggestions": [{ "id": 3, "symbol": "cup", "display_name": "cup" }]
          }
        }
      ],
      "instruction_sections": [
        { "title": null, "steps": ["Boil the noodles."] }
      ],
      "proteins": [],
      "tags": [],
      "warnings": ["ingredient_unknown"]
    }
  ]
}
```

- `parse_method` is `json_ld` or `html`.
- `source.url` / `source.site` reflect the **final** URL after redirects;
  `recipe.source_url` and `recipe.image_source_url` are resolved absolute
  URLs (same-site relative references resolve against the final URL).
- `source_url_match` is the existing recipe whose normalized source URL equals
  the page URL, or `null`. `name_matches` is a bounded (max 5) normalized-name
  containment list.
- `capabilities.can_create_reference_data` reflects whether the actor holds
  `reference_data.edit`, so the review UI can hide inline create affordances.
  (Canonical creation during review is still enforced by the M2 endpoints.)
- `image_source_url` is a resolved preview URL only; the image is downloaded
  after review via `POST /recipes/{id}/image/from-url` (M4-07).
- Ingredient lines are split into `quantity`, `name`, and `preparation_note`
  (M4-05), and `unit` is resolved against active unit symbols and aliases
  (folded). A parsed quantity is clamped to the M3 bounds (`0`–`1,000,000`,
  ≤14 decimal places). Ranges, multipliers, and word quantities stay raw with
  `quantity = null`; an unresolvable unit stays `is_unknown = true` with an
  optional `suggested_dimension` (`mass`/`volume`/`count`) and up to five
  suggestions. Package sizes are preserved in the name and never converted.

**M4-05 limitations (filled in by later M4 issues):** `source.page_title` is
`null`; canonical ingredient matching, `match_kind`, and ingredient
`suggestions` are empty and every ingredient stays `is_unknown = true` (M4-06).
Advanced duplicate scoring and rename rechecks arrive in M4-11/M4-12.

### `POST /api/v1/import/check`

Request (either field may be omitted):
```json
{ "source_url": "https://example.com/recipe", "name": "Lasagna" }
```

Response `200`:
```json
{
  "source_url_match": { "id": 5, "name": "Lasagna" },
  "name_matches": [{ "id": 6, "name": "Lasagna Bolognese" }]
}
```

### Import error codes

| Code | HTTP | Meaning |
|------|------|---------|
| `IMPORT_URL_INVALID` | 400 | URL is malformed, not HTTP/HTTPS, or carries credentials |
| `IMPORT_URL_BLOCKED` | 422 | URL host/address is a blocked SSRF target |
| `IMPORT_FETCH_FAILED` | 502 | DNS, timeout, transport, or redirect failure |
| `IMPORT_UNSUPPORTED_CONTENT` | 415 | Response content type is not allowed |
| `IMPORT_TOO_LARGE` | 413 | Response body exceeds the per-kind size limit |
| `IMPORT_NO_RECIPE` | 422 | Fetched successfully but zero recipes parsed |
| `IMPORT_RATE_LIMITED` | 429 | Per-user import fetch budget exhausted; `Retry-After` present |

### Throttling

Page fetches are limited per user to **30 per hour** by default
(`Imports:Throttle:PageFetchesPerHour`; image downloads are 60 per hour,
M4-07). The limiter is a fixed window keyed by the authenticated user (the
single-user system actor included), so loopback and single-user callers are
throttled too. Over-limit requests return `429 IMPORT_RATE_LIMITED` with a
`Retry-After` header before any outbound fetch.

### Review/Save

The client edits the draft and submits through the normal recipe creation
endpoint (§23). The import route never bypasses ingredient-matching review.

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

---

## 22. M2 Contract: Reference Data

General rules (apply to departments, units, ingredients, proteins, tags):
- Reads require any authenticated actor (single-user system actor included); writes require the `reference_data.edit` permission; hard deletes are Admin-only.
- Names and aliases are normalized (lowercase, trimmed, whitespace-collapsed, diacritics folded) for uniqueness and matching; responses return the display form.
- `GET` list endpoints accept `?search=&limit=&include_retired=`; `limit` defaults to 20 and is clamped to 100; prefix matches rank before substring matches; aliases participate in ingredient/unit search; retired entities are excluded unless `include_retired=true`.
- `POST /{id}/retire` deactivates; retired entities still resolve for existing data but cannot be selected for new entries.
- Duplicate creates return the specific `*_EXISTS` code with `details.existing_id`; alias conflicts return `ALIAS_EXISTS` with the owning entity.
- Every write (create/update/retire/delete/merge/factor change) writes an `audit_events` row with actor and before/after; the audit read API remains M12.

### Departments
- `GET /departments?search=&include_retired=` — ordered by `sort_order`.
- `POST /departments` — `{ name }`; duplicate name → `400 VALIDATION_FAILED`.
- `PATCH /departments/{id}` — `{ name?, is_active? }`.
- `POST /departments/{id}/retire` — deactivate.
- `DELETE /departments/{id}` — Admin only; `409 DEPARTMENT_IN_USE` when ingredients reference it.
- `PUT /departments/order` — `{ "ids": [...] }` must be a complete permutation of all department ids; returns the reordered list.

### Units
- `GET /units?search=&limit=&include_retired=`.
- `POST /units` — `{ symbol, display_name, dimension, base_factor? }`; `base_factor` is required for mass/volume and forbidden otherwise; dimension is fixed thereafter.
- `PATCH /units/{id}` — `{ symbol?, display_name?, base_factor? }`; `base_factor` is editable only for mass/volume units, must be positive and within `1e-6 … 1e6`, and is audited (`details.field_errors` on failure); attempting a dimension change returns `400 INVALID_UNIT_DIMENSION_CHANGE`.
- Aliases: `POST /units/{id}/aliases` `{ alias }`, `DELETE /units/{id}/aliases/{aliasId}`.
- `POST /units/{id}/retire`, `DELETE /units/{id}` (Admin, `409 UNIT_IN_USE` when referenced).

### Ingredients
- `GET /ingredients?search=&limit=&include_retired=` — search matches canonical names and aliases.
- `POST /ingredients` — `{ canonical_name, department_id?, aliases? }`; duplicate → `409 INGREDIENT_EXISTS`.
- `PATCH /ingredients/{id}` — `{ canonical_name?, department_id? }`.
- Aliases: `POST /ingredients/{id}/aliases` `{ alias }`, `DELETE /ingredients/{id}/aliases/{aliasId}`.
- `POST /ingredients/{id}/retire`.
- `DELETE /ingredients/{id}` — Admin only; blocked while any recipe (including retired recipes) references it (M3 replaced the M2 zero-returning seam with real counts). In-use responses offer merge or retire (`409 INGREDIENT_IN_USE` with usage details).
- `POST /ingredients/merge` — `{ "source_id": N, "target_id": M }`, transactional: reassigns recipe references, copies the source's name and aliases to the target (conflicting aliases are skipped and returned in `skipped_aliases`), retires the source with `merged_into_ingredient_id = M`, and writes an audit event with before/after.

### Proteins and Tags
- Proteins: `GET/POST/PATCH`, `POST /{id}/retire`, `DELETE /{id}` (Admin, `409 PROTEIN_IN_USE` when referenced).
- Tags: `GET/POST/PATCH`, `DELETE /{id}` (Admin, `409 TAG_IN_USE` when referenced); no retire flag.

---

## 23. M3 Contract: Recipes

General rules:
- Reads require `recipes.view`; writes require `recipes.edit`; Admin holds both and the single-user system actor is treated as Admin.
- Recipe names normalize through `ReferenceDataNormalizer`; `normalized_name` is globally unique and responses keep the display form. Duplicate names return `409 RECIPE_EXISTS` with `details.existing_id`.
- Every write is a single transaction and writes one `audit_events` row (`entity_type = "recipe"`) with before/after snapshots that include the nested slices. No ordinary route hard-deletes a recipe; retirement is a status change (hard deletion stays the M12 Admin workflow).
- Errors use the shared envelope; nested validation keys are index-qualified (for example `ingredients[0].quantity`, `ingredient_groups[1].ingredients[0].unit_id`, `instruction_sections[0].steps[2]`).

### Recipe document

```json
{
  "id": 1,
  "name": "Ratatouille",
  "description": null,
  "source_site": null,
  "source_url": null,
  "author": null,
  "prep_minutes": 20,
  "cook_minutes": 45,
  "notes": null,
  "status": "normal",
  "is_favorite": false,
  "is_vegan": true,
  "is_vegetarian": true,
  "created_at": "…",
  "updated_at": "…",
  "image": { "id": 1, "url": "/api/v1/recipes/1/image", "alt_text": null, "source_url": null, "created_at": "…" },
  "ingredient_groups": [
    { "id": 1, "title": "Sauce", "sort_order": 0, "ingredients": [ /* … */ ] }
  ],
  "ingredients": [
    {
      "id": 1,
      "ingredient": { "id": 5, "name": "Tomato", "is_active": true },
      "quantity": 1.5,
      "unit": { "id": 2, "symbol": "kg", "display_name": "kilogram", "is_active": true },
      "preparation_note": "diced",
      "is_optional": false,
      "sort_order": 0
    }
  ],
  "instruction_sections": [
    { "id": 1, "title": "Prep", "sort_order": 0, "steps": [ { "id": 1, "text": "Chop.", "sort_order": 0 } ] }
  ],
  "proteins": [ { "id": 3, "name": "Chicken", "is_active": true } ],
  "tags": [ { "id": 4, "name": "Weeknight" } ],
  "statistics": { "last_eaten": null, "next_planned": null, "times_eaten": null }
}
```

- Quantities are precise decimals (`numeric(28,14)`) serialized as JSON numbers; `null` is valid ("to taste"). Bounds: `0 ≤ quantity ≤ 1,000,000`, at most 14 decimal places.
- `unit` is nullable. `ingredients` holds ungrouped lines; each group nests its own `ingredients`. `sort_order` reflects array order.
- `statistics` keys are always present and null until M5/M11 record history (never fake zeros). List `last_eaten`/`times_eaten` are always null.

### Routes

- `GET /recipes` — list envelope `{ items, total, limit, offset }`. Query: `search` (normalized name substring), `status` (`normal`, `manual_only`, `retired`), `favorite`, `vegan`, `vegetarian`, `protein_id`, `tag_id`, `sort`, `limit` (default 24, max 100), `offset`. `sort` is one of `recently_added` (default), `name`, `favorites`, `recently_eaten`, `least_recently_eaten`, `most_eaten`. History-dependent sorts are deterministic until M11 data exists and fall back to `recently_added` order. Ties break on `id`. Invalid values return `400 VALIDATION_FAILED`.
- `POST /recipes` — full create. Body accepts the base metadata plus `ingredient_groups`, `ingredients`, `instruction_sections`, `protein_ids`, and `tag_ids`. Collections default to empty. Returns `201` with the detail document.
- `GET /recipes/{id}` — detail document; `404 NOT_FOUND` when absent.
- `PATCH /recipes/{id}` — partial update. Omitted base fields are unchanged; explicit `null` clears an optional field. A present collection is a **full replacement** of that slice (`[]` clears it); omitted collections are untouched. At least one supported field is required.

### Nested write rules

- `ingredient_groups` is an ordered array of `{ title?, ingredients }`; `title` is optional. `ingredients` is an ordered array of `{ ingredient_id, quantity?, unit_id?, preparation_note?, is_optional? }`. Duplicate lines within a recipe are allowed.
- `instruction_sections` is an ordered array of `{ title?, steps }`; `steps` is an array of non-empty strings. Empty sections are allowed; a blank step is rejected. Section titles and step text are limited to 20,000 characters and reject control characters except tab/newline.
- `protein_ids` / `tag_ids` are integer arrays; duplicates are silently deduplicated preserving first-occurrence order.
- Selection rules: active ingredients/units/proteins are selectable. A retired ingredient/unit/protein that is already referenced by the recipe may be retained on update, but a retired reference cannot be newly selected (`400 VALIDATION_FAILED`). Tags have no retire flag.
- A failure anywhere in the nested payload aborts the whole write: no partial recipe, nested rows, or audit row are persisted (create), and the recipe is left unchanged (update).

### Image routes (M3-04)
- `GET /recipes/{id}/image` (image bytes; `recipes.view`).
- `PUT /recipes/{id}/image` (multipart `file`, optional `alt_text`; `recipes.edit`). Replaces the active image.
- `PATCH /recipes/{id}/image` — `{ alt_text }` (≤300 characters; null/empty clears it); `recipes.edit`.
- `DELETE /recipes/{id}/image` (`recipes.edit`). See docs/SECURITY.md for the content policy.
