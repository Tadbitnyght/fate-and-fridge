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
| `GROCERY_STAPLE_EXISTS` | 409 | An active staple with that normalized name exists; `details.existing_id` present |

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

M5 delivers the calendar core (docs/M5_PLAN.md §2.4). Dates are household-local
`YYYY-MM-DD`. Reads require `calendar.view`; writes require `calendar.edit`.
Single-user mode acts with full permissions. Past dates are read-only for every
actor including Admin (`CALENDAR_DATE_IN_PAST`); only the M12
historical-correction API may change history.

### Routes

| Route | Permission | Purpose |
|-------|------------|---------|
| `GET /api/v1/calendar?start&end` | `calendar.view` | Bounded range read (≤ 366 days; both ends within ±5 years of today) |
| `GET /api/v1/calendar/{date}` | `calendar.view` | One date (blank dates return `entry: null`) |
| `GET /api/v1/calendar/{date}/repeat-check?recipe_id` | `calendar.view` | Exact-recipe occurrences in the 7-back/14-forward window |
| `PUT /api/v1/calendar/{date}` | `calendar.edit` | Full-replace upsert |
| `DELETE /api/v1/calendar/{date}` | `calendar.edit` | Hard-delete a today/future entry (`204`) |
| `POST /api/v1/calendar/{date}/lock` | `calendar.edit` | Lock |
| `POST /api/v1/calendar/{date}/unlock` | `calendar.edit` | Unlock |
| `POST /api/v1/calendar/{date}/reroll` | `calendar.edit` | Reroll one date to a random `Normal` recipe (M6) |
| `POST /api/v1/operations/{operation_id}/undo` | `calendar.edit` | Undo a reroll operation (M6) |
| `POST /api/v1/calendar/reroll/preview` | `calendar.edit` | Batch reroll preview: a persisted draft plus the per-day diff (M7) |
| `POST /api/v1/calendar/reroll/apply` | `calendar.edit` | Atomically apply a batch reroll preview draft (M7) |
| `POST /api/v1/batch-operations/{batch_operation_id}/undo` | `calendar.edit` | Undo an entire batch apply (M7) |

### `day` document

```json
{
  "date": "2026-11-15",
  "is_today": true,
  "is_historical": false,
  "can_edit": true,
  "entry": {
    "id": 12,
    "entry_type": "host_family_dinner",
    "recipe": { "id": 5, "name": "Lasagna", "image_url": null, "status": "normal" },
    "display_text": "Lasagna",
    "is_locked": false,
    "diner_count": null,
    "effective_diner_count": 4,
    "restaurant_name": null,
    "location_text": null,
    "custom_label": null,
    "notes": null,
    "icon_key": "star",
    "include_groceries": true,
    "blocks_automation": false,
    "created_at": "2026-11-01T00:00:00+00:00",
    "updated_at": "2026-11-01T00:00:00+00:00"
  },
  "repeat_warning": {
    "recipe_id": 5,
    "window_start": "2026-11-08",
    "window_end": "2026-11-29",
    "occurrences": [{ "date": "2026-11-20", "entry_type": "recipe" }]
  }
}
```

Range reads return `{ start, end, today, week_starts_on, entries: [day…] }` with
only non-blank days, ascending.

### Entry types and rules

`entry_type` is one of `recipe`, `eat_out`, `fend_for_yourself`,
`host_family_dinner`, `go_to_family_dinner`, `holiday`, `misc`, `vacation`.
`recipe` requires `recipe_id`; `misc` requires `custom_label`;
`include_groceries` is derived (recipe types and special types with a recipe);
`blocks_automation` is true only for `vacation`; `icon_key` is accepted only for
the special types and only from the curated catalog. `diner_count` is 1–99;
`null` uses the household default and the response exposes the effective count.
Text fields reuse the M3 sanitization and limits. Snapshots preserve display
meaning: non-historical entries track recipe renames, historical entries are
frozen. Locks restrict automatic mutation only — manual edits keep the lock;
blank dates cannot be locked (`CALENDAR_BLANK_CANNOT_LOCK`).

### Reroll (M6)

`POST /api/v1/calendar/{date}/reroll` requires `calendar.edit`, takes no body,
and replaces the date with a randomly selected `Normal` recipe entry. It
immediately commits and returns the M5 `day` document plus a compact operation
handle:

```json
{
  "date": "2026-11-15",
  "is_today": true,
  "is_historical": false,
  "can_edit": true,
  "entry": { "…": "the day document entry" },
  "repeat_warning": { "…": "the day document repeat warning or null" },
  "operation": { "id": "3f1c…", "undo_expires_at": null }
}
```

Behavior (docs/M6_PLAN.md §1.11):

- A blank date is filled; any non-locked, non-vacation entry is replaced.
- Only `status = Normal` recipes are candidates (`manual_only`/`retired` never).
  With no candidate the route fails `RANDOMIZER_NO_CANDIDATES`.
- The replacement resets entry metadata: `is_locked = false`,
  `diner_count = null`, `notes = null`, `include_groceries = true`, cleared
  restaurant/location/custom/icon fields, and the snapshot from the chosen
  recipe.
- Exactly one `calendar_entry_history` row is written atomically (entry +
  history) with a fresh `operation_id`, `before_json`, `after_json`, and
  `metadata_json` (docs/M6_PLAN.md §2.3). `undo_expires_at` is currently
  `null`; the operation stays undoable until the entry is next modified and
  only while the date is still today or future (a past date fails
  `CALENDAR_DATE_IN_PAST`).
- Reroll broadcasts nothing (live sync is M9); it emits the operation id that
  M9 will use.

### Undo (M6)

`POST /api/v1/operations/{operation_id}/undo` requires `calendar.edit`, takes
no body, and returns the restored M5 `day` document (docs/M6_PLAN.md §2.2). A
malformed `{operation_id}` fails `400 VALIDATION_FAILED` with a field error on
`operation_id`.

Behavior (docs/M6_PLAN.md §1.13):

- Restores the exact pre-reroll state: the previous entry (every field the
  reroll reset), or a blank date if the date was empty before the reroll.
- Exactly one `calendar_entry_history` row is written atomically (entry +
  history) with a fresh `operation_id`, `before_json`, `after_json`, a
  `metadata_json` of `{"kind":"undo","undone_operation_id":"…"}`, and
  `change_reason` `undo:<operation_id>`.
- Only a `reroll` operation is undoable. An operation that is missing, whose
  history row is not a reroll (`metadata_json` lacks `"kind":"reroll"`), or that
  has already been undone fails `UNDO_EXPIRED` (410), so repeating an undo is
  safe.
- Undo is offered until the entry is next modified. A later lock/upsert/delete
  makes it fail `UNDO_CONFLICT` (409).
- Undo is only available while the date is still today or future. A date that
  has since become historical fails `CALENDAR_DATE_IN_PAST` (422), consistent
  with past-date immutability; undo does not rewrite history even though it
  restores prior state.
- Undo broadcasts nothing (live sync is M9) and writes no `audit_events` rows.

### Batch reroll preview (M7)

`POST /api/v1/calendar/reroll/preview` requires `calendar.edit`. Body:

```json
{ "start_date": "2026-11-15", "mode": "seven_days" }
```

`mode` ∈ `seven_days` | `rest_of_month` | `fill_empty`. The response is a
persisted draft handle plus the ordered per-day diff:

```json
{
  "draft": {
    "token": "3f1c…",
    "mode": "seven_days",
    "start_date": "2026-11-15",
    "end_date": "2026-11-21",
    "expires_at": "2026-11-15T18:15:00+00:00"
  },
  "days": [
    {
      "date": "2026-11-15",
      "status": "proposed",
      "unchanged_reason": null,
      "current": null,
      "proposed": { "entry_type": "recipe", "recipe_id": 5, "display_text": "Lasagna" }
    },
    {
      "date": "2026-11-16",
      "status": "unchanged",
      "unchanged_reason": "locked",
      "current": { "…": "the M5 day-document entry" },
      "proposed": null
    }
  ]
}
```

Behavior (docs/M7_PLAN.md §1.3–§1.7):

- Ranges: `seven_days` = start … start + 6; `rest_of_month` and `fill_empty` =
  start … last day of that calendar month. A start before the household today
  fails `CALENDAR_DATE_IN_PAST` (422); a range outside the ±5-year horizon fails
  `VALIDATION_FAILED` (400).
- 7-day and rest-of-month reroll change blank dates and existing plain `recipe`
  entries only. Special events (`holiday`, `host_family_dinner`, `misc`,
  `go_to_family_dinner`), `eat_out`, `fend_for_yourself`, `vacation`, and locked
  dates are shown `unchanged` with `unchanged_reason` `special`,
  `existing_non_recipe`, `vacation`, or `locked`. `fill_empty` fills blank dates
  only and leaves every existing entry unchanged.
- Only `status = Normal` recipes are candidates; an empty pool fails
  `RANDOMIZER_NO_CANDIDATES` (422). Generation runs in ascending date order, and
  recipes chosen earlier in the same preview count as occurrences and neighbors
  for later dates (docs/RANDOMIZER.md §9).
- The preview persists **no** `calendar_entries` or `calendar_entry_history`
  rows. It stores one `calendar_reroll_drafts` row keyed by the opaque `token`
  with a **15-minute TTL** (`expires_at`). A new preview supersedes the actor's
  previous unapplied draft (Try Another), so the latest token is the live one.
  The full per-date score breakdown stays server-side in `proposal_json`.
- Preview broadcasts nothing (live sync is M9).

### Batch reroll apply (M7)

`POST /api/v1/calendar/reroll/apply` requires `calendar.edit`. Body:

```json
{ "token": "3f1c…" }
```

A missing or malformed `token` fails `400 VALIDATION_FAILED` with a field error
on `token`. Success returns the batch operation handle plus the changed day
documents:

```json
{
  "operation": { "id": "9a2b…", "undo_expires_at": null },
  "days": [ { "…": "the M5 day document" } ],
  "changed_count": 5
}
```

Behavior (docs/M7_PLAN.md §1.8–§1.12):

- Apply commits **exactly** the recipes shown in the preview; it never rerolls.
  A blank date gains a `recipe` entry and an existing plain `recipe` entry is
  replaced, with metadata reset exactly like single reroll (`is_locked = false`,
  `diner_count = null`, `notes = null`, `include_groceries = true`, cleared
  restaurant/location/custom/icon fields, snapshot from the chosen recipe).
- It runs in one transaction. Every proposed target date is serialized with the
  same `pg_advisory_xact_lock` used by M6 (acquired in ascending date order) and
  re-checked against the before-state captured at preview time. A target date
  changed since the preview fails `BATCH_CONFLICT` (409) and **nothing** is
  written (the conflict scope is proposed target dates only; unchanged, locked,
  vacation, and special dates are not targets).
- Exactly one `calendar_entry_history` row is written per changed date, each with
  its own `operation_id` and a shared `batch_operation_id`, a `before_json` /
  `after_json` pair, and a `metadata_json` breakdown with `kind` =
  `batch_reroll` (or `fill_empty`) and the `mode` (docs/M7_PLAN.md §2.3).
- A draft that is missing, expired (`expires_at`, a 15-minute TTL), or already
  applied fails `BATCH_DRAFT_EXPIRED` (410). Drafts are per-actor: another
  actor's token fails `NOT_FOUND` (404) so existence is not leaked. On any
  failure the draft is not consumed (`applied_at` stays null) and no calendar
  row is written.
- Apply broadcasts nothing (live sync is M9) and writes no `audit_events` rows.

### Batch undo (M7)

`POST /api/v1/batch-operations/{batch_operation_id}/undo` requires
`calendar.edit`, takes no body, and reverts the whole batch apply that wrote the
given `batch_operation_id`. A malformed `{batch_operation_id}` fails
`400 VALIDATION_FAILED` with a field error on `batch_operation_id`. Success
returns the operation handle plus the restored day documents:

```json
{
  "operation": { "id": "9a2b…" },
  "days": [ { "…": "the M5 day document" } ],
  "reverted_count": 5
}
```

Behavior (docs/M7_PLAN.md §1.10):

- Restores **every** date the apply changed to its exact pre-apply state: the
  previous entry (every field the apply reset), or a blank date if the date was
  empty before the apply. The day documents are returned in ascending date order.
- It runs in one transaction, serializing each affected date with the same
  `pg_advisory_xact_lock` used by apply (ascending date order). The undo is only
  offered while **every** affected date is still exactly as the apply left it; a
  later lock/upsert/delete/reroll on any one date fails `UNDO_CONFLICT` (409) and
  **nothing** is reverted.
- Exactly one `calendar_entry_history` row is written per restored date, all
  sharing one **fresh** `batch_operation_id` (distinct from the applied one),
  each with its own `operation_id`, a `before_json` / `after_json` pair, a
  `metadata_json` of
  `{"kind":"undo","undone_batch_operation_id":"<applied batch_operation_id>"}`,
  and `change_reason` `undo:<applied batch_operation_id>`.
- A batch that is unknown, is not a batch apply (`metadata_json` `kind` is not
  `batch_reroll` / `fill_empty`), or has already been undone fails
  `UNDO_EXPIRED` (410), so repeating an undo is safe. The `operation.id` in the
  response echoes the batch operation that was undone.
- A date that has since become historical fails `CALENDAR_DATE_IN_PAST` (422),
  consistent with past-date immutability; undo does not rewrite history even
  though it restores prior state.
- Undo broadcasts nothing (live sync is M9) and writes no `audit_events` rows.

### Errors

| Code | HTTP | Meaning |
|------|------|---------|
| `CALENDAR_DATE_IN_PAST` | 422 | The date is historical and read-only |
| `CALENDAR_BLANK_CANNOT_LOCK` | 422 | Cannot lock a date with no entry |
| `CALENDAR_ENTRY_LOCKED` | 409 | Automatic mutation (reroll) of a locked date |
| `RANDOMIZER_NOT_APPLICABLE` | 422 | Reroll of a vacation date |
| `RANDOMIZER_NO_CANDIDATES` | 422 | Reroll with no `Normal` recipe available |
| `CALENDAR_RECIPE_REQUIRED` | 400 | `recipe` entry without `recipe_id` |
| `CALENDAR_MISC_LABEL_REQUIRED` | 400 | `misc` entry without `custom_label` |
| `UNDO_EXPIRED` | 410 | Undo of an operation or batch that is missing or already undone |
| `UNDO_CONFLICT` | 409 | Undo of an operation or batch whose date changed afterwards |
| `BATCH_CONFLICT` | 409 | Apply: a target date changed since the preview |
| `BATCH_DRAFT_EXPIRED` | 410 | Apply: the draft is missing, expired, or consumed |
| `NOT_FOUND` | 404 | Unknown resource (shared envelope) |
| `FORBIDDEN` | 403 | Authenticated actor lacks the required permission |
| `VALIDATION_FAILED` | 400 | Other field/range/horizon errors |

---

## 8. Randomization

### Single Date
- `POST /calendar/{date}/reroll`

Behavior:
- immediate
- returns replacement
- returns undo token/operation id where supported

### Batch Preview (M7)
- `POST /calendar/reroll/preview` — see "Batch reroll preview (M7)" above for the
  full contract.

Body:
```json
{
  "start_date": "2026-10-15",
  "mode": "seven_days"
}
```

`mode` ∈ `seven_days` | `rest_of_month` | `fill_empty`. `rest_of_month` uses the
same route with `"mode": "rest_of_month"`.

### Try Another
Call the preview route again with the same body. The fresh preview supersedes the
actor's previous unapplied draft and is not applied until Apply is called.

### Apply
- `POST /calendar/reroll/apply` — see "Batch reroll apply (M7)" above for the
  full contract.

Apply is atomic: one transaction writes every changed date and the per-date
history rows sharing one `batch_operation_id`, and a conflicting target date
aborts the whole apply with no writes.

### Fill Empty
`POST /calendar/fill-empty` is **superseded** by `mode = "fill_empty"` on
`POST /calendar/reroll/preview`. Fill Empty proposes **blank dates only** and
never alters an existing meal, special event, `eat_out`, `fend_for_yourself`,
`vacation`, or locked entry (each is returned `unchanged`). When the range is
fully occupied the preview proposes nothing and Apply is a no-op
(`changed_count = 0`) that still consumes the draft.

---

## 9. Undo
Routes:
- `POST /operations/{operation_id}/undo` (single reroll, M6)
- `POST /batch-operations/{batch_operation_id}/undo` (batch apply, M7)

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

### `GET /api/v1/groceries` (M8-03)

The live grocery projection over the active range (docs/M8_PLAN.md §1.3–§1.12,
§2.2). Permission: `groceries.view` (`groceries.edit` implies it). An
unauthenticated request gets `401 AUTH_REQUIRED`; an authenticated member
without the key gets `403 FORBIDDEN`; the single-user system actor has full
access.

Query — exactly one of the two forms, both optional:

| Parameter | Value |
|-----------|-------|
| `preset` | `next_3_days` \| `next_7_days` \| `next_14_days` \| `rest_of_month` \| `custom` |
| `start` + `days` | explicit range: `start=YYYY-MM-DD`, `days=<positive integer>` |

Rules:

- Supplying `preset` (other than `custom`) together with `start` or `days` fails
  `400 VALIDATION_FAILED`.
- `preset=custom` requires both `start` and `days`; `start` without `days` (or
  vice versa) fails `400 VALIDATION_FAILED`.
- An unknown `preset`, a malformed `start`, or a non-positive/non-integer `days`
  fails `400 VALIDATION_FAILED` with `details.field_errors`.
- Ranges are today/future only: a `start` before the household today fails
  `400 VALIDATION_FAILED` on `start`.
- A resolved range longer than 366 days fails `400 GROCERY_RANGE_TOO_LARGE`.
- With no query parameters the server resolves the caller's remembered
  `user_grocery_preferences` range (`range_preset`/`range_start`/`range_days`),
  falling back to `next_7_days` when there is no row or an unusable/legacy
  value. This read never writes the preference (the M8-04 PATCH owns that).
- `next_N_days`: `start` = household today, `end` = today + (N − 1).
  `rest_of_month`: `start` = today, `end` = last day of the current month, so the
  range is a **single day** when today is the last day of the month.
  `custom`: `end` = `start` + `days` − 1. Dates are household-local
  (`YYYY-MM-DD`).

Response `200`:

```json
{
  "range": { "preset": "next_7_days", "start": "2026-10-10", "end": "2026-10-16", "day_count": 7 },
  "departments": [
    {
      "department_id": 12,
      "name": "Produce",
      "sort_order": 3,
      "is_other": false,
      "items": [
        {
          "identity": "<64-char lowercase hex>",
          "name": "Onion",
          "quantity_text": "1 1/2",
          "quantity": 1.5,
          "unit": "lb",
          "ingredient_id": 42,
          "department_id": 12,
          "checked": false,
          "source_type": "recipe",
          "manual_item_id": null,
          "sources": [
            { "recipe_id": 5, "recipe_name": "Tacos", "date": "2026-10-06" }
          ]
        }
      ]
    }
  ]
}
```

- `identity` is the stable `GroceryItemIdentity` hash the check API (M8-06) keys
  on: for recipe-derived lines the aggregation identity (canonical ingredient +
  unit dimension, quantity-independent); for manual lines the 64-char lowercase
  SHA-256 hex hash of the canonical `manual|{id}` string (the same hashing the
  recipe identity uses). `sources` has one row per contributing meal (`[]` for
  manual items).
  `ingredient_id` is `null` for a free-text manual item; `department_id` is
  `null` for the "Other" group.
- **Quantity-less lines:** `quantity` is `null` and `quantity_text` is `""`.
  `unit` is the line's display-unit symbol when one exists — M8-02 preserves the
  display unit for a quantity-less line (for example a nonconvertible "to
  taste") — and is `null` only when the line has no unit at all (a unitless
  count).
- **Contribution scope:** only calendar entries in the range whose derived
  `include_groceries` is true **and** that carry a recipe contribute
  recipe-derived lines. `eat_out`, `fend_for_yourself`, `go_to_family_dinner`,
  and vacation never do; `host_family_dinner`/`holiday`/`misc` do when they
  carry a recipe. A retired or missing recipe still contributes through its
  stored ingredient rows. Aliases resolve to their canonical ingredient (merge
  redirect). Compatible units in one dimension combine into the largest present
  unit (1 lb + 8 oz → `1 1/2` lb); incompatible dimensions stay separate.
- **Manual items:** a manual item is included only when its stored
  `(range_start, range_days)` **exactly equals** the resolved range, so it is
  hidden the moment the range changes. Its department is its own
  `department_id`, else the linked canonical ingredient's department, else the
  trailing "Other" group. Manual lines are never merged into recipe buckets.
- **Ordering:** departments ascending `sort_order`, then name; the
  null-department "Other" group is last (`is_other: true`, `sort_order`
  `2147483647`). Within a department, unchecked lines come first (alphabetical
  by name, then identity, then unit symbol), then checked lines alphabetically.
- `checked` is the household-shared `grocery_check_state` flag for the line's
  identity.

### Manual items (M8-04)

Range-scoped free-text or ingredient-linked items added by hand
(docs/M8_PLAN.md §1.8). A manual item belongs to the range that begins at
`range_start` and spans `range_days` days and is shown only while that pair
**exactly equals** the resolved list range. All three routes require
`groceries.edit`; unauthenticated requests get `401 AUTH_REQUIRED`, an
authenticated member without the key gets `403 FORBIDDEN`, and the single-user
system actor has full access.

#### `POST /api/v1/groceries/manual`

Request:

```json
{
  "name": "Paper towels",
  "quantity_numeric": 2,
  "unit_id": null,
  "ingredient_id": null,
  "department_id": null,
  "range_start": "2026-10-10",
  "range_days": 7,
  "save_for_future": false
}
```

| Field | Rules |
|-------|-------|
| `name` | required string, trimmed, 1–200 characters, no control characters |
| `quantity_numeric` | optional number strictly greater than 0 |
| `unit_id` | optional integer; must reference an existing unit |
| `ingredient_id` | optional integer; must reference an existing ingredient |
| `department_id` | optional integer; must reference an existing department |
| `range_start` | required `YYYY-MM-DD`, household-local, **today or later** |
| `range_days` | required integer 1–366 |
| `save_for_future` | optional boolean, default `false` |

Behavior:

- A merged `ingredient_id` is redirected to its canonical ingredient before it is
  stored (same redirect as the list).
- Department default: explicit `department_id`, else the linked canonical
  ingredient's `department_id`, else `null` (the trailing "Other" group).
- `created_by_user_id` is the acting user, or `null` for the single-user system
  actor.
- When `save_for_future` is `true` the item also promotes a household-wide
  `grocery_staples` row (`name`, canonical `ingredient_id`, `department_id`,
  `default_quantity_numeric = quantity_numeric`, `unit_id`, `is_active = true`).
  Promotion is **idempotent**: an active staple whose normalized name already
  matches is not duplicated. `save_for_future` is stored on the manual row.

Response `201`:

```json
{
  "id": 5,
  "name": "Paper towels",
  "quantity_numeric": 2,
  "unit_id": null,
  "ingredient_id": null,
  "department_id": null,
  "range_start": "2026-10-10",
  "range_days": 7,
  "save_for_future": false,
  "created_at": "2026-10-10T00:00:00+00:00",
  "updated_at": "2026-10-10T00:00:00+00:00"
}
```

Validation failures return `400 VALIDATION_FAILED` with
`details.field_errors` keyed by the offending field (`name`, `quantity_numeric`,
`unit_id`, `ingredient_id`, `department_id`, `range_start`, `range_days`).

#### `PATCH /api/v1/groceries/manual/{id}`

Partial update of any subset of the create fields. An explicit `null` clears a
nullable field (`quantity_numeric`, `unit_id`, `ingredient_id`, `department_id`);
`range_start` and `range_days` cannot be cleared. Transitioning `save_for_future`
from `false` to `true` performs the same idempotent staple promotion. Returns
`200` with the updated item document, or `404 NOT_FOUND` when the item does not
exist. Validation failures return `400 VALIDATION_FAILED`.

#### `DELETE /api/v1/groceries/manual/{id}`

Deletes the item and its household check-state row (identity = the 64-char
lowercase SHA-256 hex hash of the canonical `manual|{id}` string), so no orphan
check state remains. A staple the item may have promoted is
**retained**. Returns `204`, or `404 NOT_FOUND` when the item does not exist.

### Preferences (M8-04)

Per-user `hide_checked` plus the remembered range (docs/M8_PLAN.md §1.7, §1.11).
Both routes require `groceries.view` (edit implies view) — a member with only
`groceries.view` may read **and** write preferences.

#### `GET /api/v1/groceries/preferences`

Response `200` (defaults when no stored row):

```json
{ "hide_checked": false, "range_preset": null, "range_start": null, "range_days": null }
```

#### `PATCH /api/v1/groceries/preferences`

Body is a subset of:

```json
{ "hide_checked": true, "range_preset": "next_7_days", "range_start": null, "range_days": null }
```

Rules:

- `range_preset` must be one of `next_3_days`, `next_7_days`, `next_14_days`,
  `rest_of_month`, `custom`, or `null`.
- `custom` requires a `range_start` (household-local, today or later) and a
  `range_days` (1–366).
- `range_start`/`range_days` only carry a value with `range_preset = custom`; a
  **non-null** `range_start` or `range_days` sent without `range_preset: custom`
  fails `400 VALIDATION_FAILED` on `range_preset`. An explicit `null` is accepted
  as **clearing** them (a no-op when there is no remembered range), so a client
  may echo the `GET` body verbatim — for example `range_start: null`/`range_days:
  null` alongside a fixed preset or `range_preset: null` is valid.
- Normalization: a fixed preset stores only `range_preset` and clears
  `range_start`/`range_days`; `custom` stores `range_preset = "custom"` plus the
  start and day count; clearing the range (`range_preset: null`) stores all three
  as `null`.

Response `200` is the stored preference in the `GET` shape. The remembered range
is honored by `GET /api/v1/groceries` when no query is supplied (a read never
writes the preference). The single-user system actor's preference is stored in
`application_settings` rather than `user_grocery_preferences` (docs/DATABASE.md
§9).

### Staples (M8-05)

A household-wide reusable catalog (name, optional canonical ingredient,
department, optional default quantity/unit) (docs/M8_PLAN.md §1.9). Staples are
**never auto-added** to a grocery list; the user explicitly adds one, which
creates a manual item for the active range. Reads require `groceries.view`;
writes require `groceries.edit`. Unauthenticated requests get `401 AUTH_REQUIRED`,
an authenticated member without the key gets `403 FORBIDDEN`, and the single-user
system actor has full access.

#### `GET /api/v1/groceries/staples`

Optional query `include_inactive=true` includes soft-retired staples (default:
active only). Any other value fails `400 VALIDATION_FAILED` on
`include_inactive`.

Response `200`:

```json
{
  "staples": [
    {
      "id": 3,
      "name": "Milk",
      "ingredient_id": 42,
      "department_id": 12,
      "default_quantity_numeric": 1,
      "unit_id": 7,
      "is_active": true,
      "created_at": "2026-10-10T00:00:00+00:00",
      "updated_at": "2026-10-10T00:00:00+00:00"
    }
  ]
}
```

Ordering is name ascending case-insensitively, then ordinal, then `id`.

#### `POST /api/v1/groceries/staples`

Request:

```json
{
  "name": "Milk",
  "ingredient_id": 42,
  "department_id": null,
  "default_quantity_numeric": 1,
  "unit_id": 7
}
```

| Field | Rules |
|-------|-------|
| `name` | required string, trimmed, 1–200 characters, no control characters |
| `ingredient_id` | optional integer; must reference an existing ingredient |
| `department_id` | optional integer; must reference an existing department |
| `default_quantity_numeric` | optional number strictly greater than 0 |
| `unit_id` | optional integer; must reference an existing unit |

Behavior:

- A merged `ingredient_id` is redirected to its canonical ingredient before it is
  stored (the same redirect as the list and manual items).
- Department default: explicit `department_id`, else the linked canonical
  ingredient's `department_id`, else `null` (the trailing "Other" group).
- The new row is `is_active = true`.
- A second **active** staple whose normalized name (the same normalization used
  by manual promotion) matches an existing active staple fails
  `409 GROCERY_STAPLE_EXISTS` with `details.existing_id`.

Response `201` is the staple document shown above. Validation failures return
`400 VALIDATION_FAILED` with `details.field_errors` keyed by the offending field
(`name`, `default_quantity_numeric`, `ingredient_id`, `department_id`,
`unit_id`).

#### `PATCH /api/v1/groceries/staples/{id}`

Partial update of any subset of the create fields plus `is_active`. An explicit
`null` clears a nullable field (`ingredient_id`, `department_id`,
`default_quantity_numeric`, `unit_id`); `name` cannot be null or empty. Renaming
(or reactivating) to a name that collides with **another** active staple fails
`409 GROCERY_STAPLE_EXISTS`. Returns `200` with the updated staple, or
`404 NOT_FOUND` when the staple does not exist.

Changing `ingredient_id` does **not** recompute `department_id` (unlike `POST`,
where the department defaults from the linked ingredient). Send `department_id`
explicitly to set it, or `null` to clear it.

#### `POST /api/v1/groceries/staples/{id}/add-to-list`

Creates a range-scoped manual grocery item from the staple:

```json
{ "range_start": "2026-10-10", "range_days": 7 }
```

- `range_start` is household-local and must be **today or later**; `range_days`
  is 1–366. A malformed range fails `400 VALIDATION_FAILED` keyed
  `range_start`/`range_days`.
- The manual item copies the staple's `name`, `ingredient_id`, `department_id`,
  `quantity_numeric = default_quantity_numeric`, and `unit_id`, with
  `save_for_future = false`; `created_by_user_id` is the acting user (null for the
  single-user system actor).
- A missing **or inactive** staple returns `404 NOT_FOUND`.
- The staple row is **never modified**, and adding the same staple twice creates
  two manual items.
- Response `201` is the manual-item document from `POST /groceries/manual`.

### Check state (M8-06)

Household-shared checkbox state keyed by the stable `GroceryItemIdentity` hash
(the `identity` field of each grocery-list line). One row per identity means any
member's check is visible to all. Both routes require `groceries.edit`.

#### `POST /api/v1/groceries/check` and `POST /api/v1/groceries/uncheck`

Request:

```json
{ "identity": "<64-char lowercase hex>" }
```

- `identity` must be exactly 64 lowercase hex characters (the format produced by
  `GroceryItemIdentity`); anything else fails `400 VALIDATION_FAILED` on
  `identity`.
- The single `grocery_check_state` row for that identity is upserted. Checking
  sets `checked = true`, `checked_at = now`, and `checked_by_user_id =` the acting
  user (null for the system actor). Unchecking sets `checked = false` and clears
  `checked_at`; `checked_by_user_id` records the actor of the last check **or**
  uncheck. `updated_at` is always refreshed.
- The operation is **idempotent** and robust to a concurrent first write on the
  unique hash (the losing writer retries as an update rather than failing).

Response `200`:

```json
{
  "identity": "<64-char lowercase hex>",
  "checked": true,
  "checked_at": "2026-10-10T00:00:00+00:00",
  "checked_by_user_id": 5
}
```

`checked_at` is `null` when the line is unchecked; `checked_by_user_id` is `null`
for the single-user system actor. The M8-03 list reads this table for each line's
`checked` flag.

### Print view (M8-08)

M8-08 adds **no new API endpoint**. The print-optimized grocery view is
**client-rendered** in the web app at the client route `/groceries/print`,
inside the session-guarded app shell, over the same `GET /api/v1/groceries` read
(no query parameters, so the caller's remembered range applies).

- Departments follow the **server-provided order** (ascending `sort_order`), with
  the null-department **"Other"** group last.
- Each item renders a **decorative, non-interactive checkbox** the shopper ticks
  on paper, its name and quantity/unit text, its per-meal attribution rows, and
  the same icon+text **"Manual"** badge as the list (never colour alone).
- A screen-only **"Include checked items"** control (plus a Print button and a
  "Back to grocery list" link) is hidden by `@media print`. The control is **off
  by default**, so checked items are excluded unless the shopper opts in; it is
  independent of the per-user `hide_checked` preference.
- `groceries.view` gates the content; a caller without it sees a permission
  message.
- Print CSS (`frontend/src/index.css`) hides the app header/nav/footer, the skip
  link, and the screen-only controls, forces black-on-white single-column output,
  and keeps each item (`break-inside: avoid`) with its department heading
  (`break-after: avoid`).

### Print views (M10)

M10 adds **no new API endpoint**. The calendar print view is **client-rendered**
at the client route **`/calendar/print`** (mirroring `/groceries/print`), inside
the session-guarded app shell, reusing the existing month-scoped calendar range
read (one request per printed month). Printing stays a browser-print feature —
there is **no bulk/print endpoint and no server-side PDF export**.

- **Multi-month:** the view selects a **start month** (carried as
  `?month=YYYY-MM`, defaulting to the month being viewed) and a **month count of
  1–12**, and prints **one month per page** in chronological order, including
  jobs that cross a year boundary. Each page carries a **month/year heading**;
  the household name is never printed. Past months are printable.
- **Wrap / shrink / spill:** long names wrap fully (never ellipsis-clipped). A
  busy month auto-shrinks (a deterministic function of the measured content
  height and the fixed page geometry) down to the **≈10pt legibility floor**; a
  month that still overflows at the floor continues onto a following page rather
  than shrinking further or clipping.
- **Printed content:** day number and recipe/special-event text; curated
  special-event icons in black; eat-out and fend-for-yourself as text. No meal
  photos, lock icons, notes, diner count, or restaurant/location.
- **Named `@page` ownership (per view):** the calendar opts into
  `@page calendar-print { size: A4 landscape; margin: 10mm }` and the grocery
  list into `@page grocery-list { size: A4 portrait; margin: 12mm }`. The
  app-wide `@page { size: landscape }` (M5) and the global
  `@page { margin: 12mm }` (M8) are **removed**, so printing any other page keeps
  the browser defaults. The calendar `@page` margin is pinned to the shared
  `--calendar-print-page-margin-mm` geometry so CSS and the JS fit budget cannot
  drift.
- `calendar.view` gates the calendar print content; a caller without it sees a
  permission message.

### Errors

Errors: `VALIDATION_FAILED` (range/preset/quantity/reference data/identity),
`GROCERY_RANGE_TOO_LARGE` (resolved range exceeds 366 days),
`GROCERY_STAPLE_EXISTS` (duplicate active staple name), `NOT_FOUND`, and
`FORBIDDEN` (shared envelope).

---

## 14. Statistics
- `GET /statistics/overview`
- `GET /statistics/recipes/{id}`
- `GET /statistics/history?start=...&end=...`

Keep API flexible enough for additional metrics later.

### `GET /api/v1/statistics/overview`

Permission: `recipes.view`. Returns the household overview computed on demand
from `calendar_entries` (no cache, no statistics table, no background job;
docs/M11_PLAN.md §1.14). All windows are household-local (`HouseholdClock`
timezone), never the server wall clock.

- **Occurrence** = an entry carrying a recipe: `recipe` entries plus
  `host_family_dinner` / `holiday` / `misc` that carry a recipe (the
  `include_groceries` rule). `eat_out`, `fend_for_yourself`,
  `go_to_family_dinner`, and vacation never count toward recipe metrics.
- **Eaten** is strictly past: `meal_date < household today`, so today's dinner
  is not yet eaten (`docs/M11_PLAN.md` §1.3).
- **Grouping identity** = `recipe_id` when present, else the normalized stored
  snapshot name (a tombstone; hard delete is M12). A retired recipe still counts
  and carries its status badge; a hard-deleted recipe's meals still count under
  the stored snapshot name. Nothing is derived solely from a live `recipes` row.
- `most_eaten` and `least_recently_eaten` each return the **top 10**, ordered by
  times eaten / last-eaten date with a deterministic name tie-break.
  `least_recently_eaten` only lists recipes eaten at least once; recipes never
  eaten are reported as the `never_eaten` **count** of live recipes with no
  eaten occurrence.
- `meal_count_month` / `meal_count_year` count **every** entry type in the
  household calendar month/year.
- `protein_distribution` counts **recipe occurrences** per primary protein (an
  occurrence whose recipe lists several proteins counts toward each bar) plus an
  explicit `no_primary_protein` figure (a recipe with no protein rows, or a
  tombstone whose live recipe is gone). No-recipe days are excluded from the
  distribution and appear only in the special-event counts.
- `special_event_counts` gives one count per special-event type (all seven,
  excluding `recipe`) over the household calendar year.

Response `200`:

```json
{
  "windows": {
    "today": "2026-11-15",
    "timezone": "America/Chicago",
    "month": { "label": "November 2026", "start": "2026-11-01", "end": "2026-11-30" },
    "year": { "label": "2026", "start": "2026-01-01", "end": "2026-12-31" }
  },
  "most_eaten": [
    {
      "recipe_id": 5,
      "name": "Tacos",
      "status": "normal",
      "times_eaten": 9,
      "last_eaten": "2026-11-01"
    }
  ],
  "least_recently_eaten": [
    {
      "recipe_id": null,
      "name": "Grandma's Stew",
      "status": "deleted",
      "times_eaten": 3,
      "last_eaten": "2025-12-24"
    }
  ],
  "never_eaten": 42,
  "meal_count_month": 12,
  "meal_count_year": 140,
  "protein_distribution": {
    "proteins": [
      { "protein_id": 2, "name": "Chicken", "count": 30 }
    ],
    "no_primary_protein": 7
  },
  "special_event_counts": [
    { "entry_type": "eat_out", "count": 4 },
    { "entry_type": "host_family_dinner", "count": 1 },
    { "entry_type": "go_to_family_dinner", "count": 2 },
    { "entry_type": "holiday", "count": 1 },
    { "entry_type": "misc", "count": 0 },
    { "entry_type": "fend_for_yourself", "count": 6 },
    { "entry_type": "vacation", "count": 1 }
  ]
}
```

A ranked entry's `status` is `normal`, `manual_only`, `retired`, or `deleted`
(a tombstone). `recipe_id` is null only for a tombstone. Errors: `401` for an
unauthenticated caller and `403 FORBIDDEN` for a member lacking `recipes.view`
(shared envelope).

### `GET /api/v1/statistics/recipes/{id}`

Permission: `recipes.view`. The three per-recipe metrics from
`docs/PRODUCT_SPEC.md` §12, computed on demand from `calendar_entries` with the
same occurrence and eaten rules as the overview (docs/M11_PLAN.md §1.2, §1.6,
§1.21). The route requires a **live** recipe; a hard-deleted recipe returns
`404 NOT_FOUND` while its meals still appear in the household overview rankings
as a tombstone (docs/M11_PLAN.md §1.5, §1.20).

- `last_eaten` — latest **eaten** occurrence (`meal_date < household today`), or
  `null` when the recipe was never eaten.
- `next_planned` — nearest **today-or-future** occurrence, or `null`.
- `times_eaten` — the historical (eaten) occurrence count, or `null` when there
  is none (never a fake zero). A recipe that is only planned has `next_planned`
  set but `times_eaten` null.

Response `200`:

```json
{
  "last_eaten": "2026-11-14",
  "next_planned": "2026-11-15",
  "times_eaten": 3
}
```

Errors: `404 NOT_FOUND` when no live recipe has that id; `401` for an
unauthenticated caller and `403 FORBIDDEN` for a member lacking `recipes.view`
(shared envelope).

The same three metrics are embedded per item on `GET /api/v1/recipes` (§23) and
in the recipe detail document's `statistics` object.

### `GET /api/v1/statistics/history`

Permission: `calendar.view`. The read-only meal log: a reverse-chronological,
month-grouped list of the **days that have an entry**, computed on demand from
`calendar_entries` (no cache, no statistics table; docs/M11_PLAN.md §1.9,
§1.14, §1.22). Past dates are immutable in normal use, so the current rows are
what was eaten — editing past dates and the audit trail are M12. Dates are
household-local (`HouseholdClock`), never the server wall clock, and there is no
edit affordance in the contract.

Query parameters (all optional):

| Parameter | Meaning |
|-----------|---------|
| `start` / `end` | Household-local `yyyy-MM-dd` range bounds. Default: the household calendar month plus the eleven before it, ending at the household today. The range is capped at **366 days inclusive** (`HouseholdClock.MaxRangeDays`). |
| `month` | Household-local `yyyy-MM` page selector. Default: the **newest month in the applied range that has an entry**, falling back to the newest month in the range. |
| `entry_type` | One of the eight entry types: `recipe`, `eat_out`, `host_family_dinner`, `go_to_family_dinner`, `holiday`, `misc`, `fend_for_yourself`, `vacation`. |
| `recipe` | A live recipe **id** (`5`) or a **normalized stored snapshot name** (`Grandma's Stew`), so a renamed or hard-deleted recipe's meals stay filterable. Matching is by the id or the snapshot name stored with the entry, not by a live recipe name. |
| `protein` | A primary-protein **id** (`2`, the same shape as the recipes-list `protein_id`). Matches the row's primary protein (below), not every protein a recipe lists. |

Filters are server-side and AND-combined with the range, the selected month, and
the strictly-past rule. Only **strictly past** days
(`meal_date < household today`, the "eaten" rule) that **have an entry** appear:
no blank filler rows. Days are clamped to the applied range (`range.start`
.. `range.end`), while `month.start`/`month.end` describe the full calendar
month.

- **Day rows.** `calendar_entries.meal_date` is unique, so a day holds exactly
  one entry and the day row **is** the entry, newest first:
  `{ date, entry_type, recipe_id, name, protein }`.
- `recipe_id` is `null` for a tombstone (an entry whose recipe is gone; M12 owns
  hard delete, so the meal still shows).
- `name` is the live recipe name when the recipe survives, else the stored
  snapshot name (a tombstone), else the entry's own display text (an eat-out
  restaurant, host, or custom label).
- `protein` is the entry's **primary protein name**, `null` when the entry
  carries no recipe or its recipe lists no protein. A recipe that lists several
  proteins has one deterministic primary: **the lowest protein id**.
- **Paging.** One month per page. `paging.newer` is the next **later** month
  inside the applied range and `paging.older` the next **earlier** one, one
  month per step; both are `null` at the range ends.

Response `200`:

```json
{
  "range": { "start": "2025-11-01", "end": "2026-10-16", "timezone": "America/Chicago" },
  "month": {
    "key": "2026-10",
    "label": "October 2026",
    "start": "2026-10-01",
    "end": "2026-10-31",
    "days": [
      { "date": "2026-10-15", "entry_type": "recipe", "recipe_id": 5, "name": "Tacos", "protein": "Chicken" },
      { "date": "2026-10-14", "entry_type": "eat_out", "recipe_id": null, "name": "Sushi place", "protein": null }
    ]
  },
  "paging": { "newer": null, "older": "2026-09" }
}
```

Errors: `400 VALIDATION_FAILED` (shared envelope) for a malformed `start`,
`end`, or `month`; `start` later than `end` (reported on `end`); a range longer
than 366 days (reported on `range`); a `month` outside the applied range; an
unknown `entry_type`; a blank or non-positive `recipe`; and a non-positive or
non-numeric `protein`. `401` for an unauthenticated caller and `403 FORBIDDEN`
for a member lacking `calendar.view` (the overview and per-recipe reads keep
requiring `recipes.view`).

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
| `POST /api/v1/recipes/{id}/image/from-url` | `recipes.edit` | Download an image from a URL and replace the recipe's active image |

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
- `source_url_match` (M4-11) is the existing recipe whose normalized source URL
  equals the page URL, or `null`. Equality uses the same canonical folding as
  recipe save (`BuildNormalizedSourceUrl`): lowercase scheme/host, dropped
  default port, dropped fragment, trailing slashes trimmed (so the root path
  `https://example.com/` collapses to `https://example.com`), and query
  parameters sorted, so `?a=1&b=2` matches `?b=2&a=1` and `/recipe/` matches
  `/recipe`. Folding is deliberately narrow and only ever causes a warning to be
  *missed*, never a false warning: an empty query (`…?`) stays distinct from no
  query, a trailing `&` is preserved, percent-encoding case is not folded, and
  repeated keys are compared as sorted pairs. Recipes saved before this
  canonicalization are matched by re-folding up to 200 same-origin rows in
  memory, so a legacy duplicate beyond that bound may not warn.
- `name_matches` (M4-12) is the bounded (max 5) list of existing recipes whose
  folded name is similar, ranked by descending score with ties broken by id.
  Similarity is, in descending confidence: folded exact equality, whole-word
  containment in either direction (so `lasagna` matches `Best Lasagna Ever` but
  not `lasagnabake`), one or more shared whole tokens (two characters or
  longer), or a Levenshtein distance of at most 2 on the folded forms. The
  candidate set is a bounded, id-ordered scan of the lowest-id 1000 rows, so a
  catalog beyond that window could miss a warning for a newer, higher-id
  recipe. Editing the draft name
  re-runs this through `POST /import/check`; it is a warning only, never a save
  block.
- `capabilities.can_create_reference_data` reflects whether the actor holds
  `reference_data.edit`, so the review UI can hide inline create affordances.
  (Canonical creation during review is still enforced by the M2 endpoints.)
- `image_source_url` is a resolved preview URL only; the image is downloaded
  after review via `POST /recipes/{id}/image/from-url` (M4-07).
- Ingredient lines are split into `quantity`, `name`, and `preparation_note`
  (M4-05), the `unit` is resolved against active unit symbols and aliases
  (folded), and the `name` is matched (M4-06) against active canonical
  ingredients. `match_kind` is `exact` for a folded canonical-name match,
  `alias` for a folded alias match, and `null` when unknown; `is_unknown` is
  true when no confident match exists. `suggestions` is a bounded (max 5),
  deterministic list (exact-prefix before substring, then shorter phrase, then
  id) over active canonical names and aliases. Retired/merged ingredients are
  never matched or suggested; a folded key shared by more than one active
  ingredient stays unknown rather than guessed. A parsed quantity is clamped to
  the M3 bounds (`0`–`1,000,000`, ≤14 decimal places). Ranges, multipliers, and
  word quantities stay raw with `quantity = null`; an unresolvable unit stays
  `is_unknown = true` with an optional `suggested_dimension`
  (`mass`/`volume`/`count`) and up to five suggestions. Package sizes are
  preserved in the name and never converted.
- `warnings` contains `ingredient_unknown` only when at least one ingredient
  line is `is_unknown`; a draft whose lines all match carries no such warning.
  The review screen maps an unknown line to an existing ingredient or creates
  one when permitted; no free-text ingredient id is ever sent to `POST /recipes`.

**M4 note:** `source.page_title` is `null` (page-title enrichment is deferred);
duplicate detection covers the exact normalized source URL (M4-11) plus the
scored similar-name list above (M4-12), including a rename recheck.

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

### `POST /api/v1/recipes/{id}/image/from-url`

Downloads an image from a user-supplied URL and replaces the recipe's active
image (M4-07, docs/M4_PLAN.md §1.8/§2.2). Requires `recipes.edit`; a missing
recipe returns `404 NOT_FOUND` **before** any outbound request.

Request:
```json
{ "source_url": "https://example.com/lasagna.jpg" }
```

`source_url` is required, non-blank, and must be an absolute HTTP/HTTPS URL. The
server re-validates it through the same SSRF-hardened fetcher used by
`POST /import/recipe-url` (docs/API.md §15, docs/SECURITY.md §6–§7): HTTP/HTTPS
only, DNS revalidated per hop, max 5 redirects, connection timeout 5 s, total
read timeout 15 s, and a 5 MiB image ceiling. The response body is treated as
untrusted: the declared `Content-Type` and the actual bytes must both pass the
M3 image policy (`RecipeImageFormatDetector`), which accepts only JPEG, PNG, and
WebP still images with each dimension ≤ 2048 px and rejects SVG, HTML, GIF,
truncated signatures, animated formats, and content-type/signature mismatches.

On success the active image is replaced atomically: the new file is written to a
randomly named file under `/data/images/recipes` and the database swap (new
`recipe_images` row, one `audit_events` row of type `image_updated`, removal of
the previous row) is committed in a single transaction; the previous file is
deleted afterwards best-effort. `source_url` on the image records the **final**
URL after redirects. A failed download never modifies the existing image row,
file, or audit history, and leaves no partial file on disk.

Response `200` — the M3 image document (docs/API.md §23):
```json
{
  "id": 12,
  "alt_text": null,
  "source_url": "https://example.com/lasagna.jpg",
  "created_at": "2026-10-04T00:00:00+00:00",
  "url": "/api/v1/recipes/5/image"
}
```

Errors (shared envelope):

| Code | HTTP | Meaning |
|------|------|---------|
| `IMPORT_URL_INVALID` | 400 | URL is malformed, not HTTP/HTTPS, or carries credentials |
| `IMPORT_URL_BLOCKED` | 422 | URL host/address is a blocked SSRF target |
| `IMPORT_FETCH_FAILED` | 502 | DNS, timeout, transport, redirect, or non-success status |
| `IMPORT_TOO_LARGE` | 413 | Response body exceeds the 5 MiB image ceiling |
| `IMPORT_UNSUPPORTED_CONTENT` | 415 | Declared type or actual bytes fail the M3 image policy |
| `IMPORT_RATE_LIMITED` | 429 | Per-user image fetch budget exhausted; `Retry-After` present |
| `NOT_FOUND` | 404 | The recipe does not exist |
| `VALIDATION_FAILED` | 400 | `source_url` is missing, blank, or not a string |

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
(`Imports:Throttle:PageFetchesPerHour`); image downloads
(`POST /recipes/{id}/image/from-url`) are limited to **60 per hour**
(`Imports:Throttle:ImageFetchesPerHour`). The limiter is a fixed window keyed by
the authenticated user (the single-user system actor included), so loopback and
single-user callers are throttled too. Over-limit requests return
`429 IMPORT_RATE_LIMITED` with a `Retry-After` header before any outbound fetch.

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

## 19. WebSocket (live sync)

Implemented contract (M9; this **supersedes** the earlier `/ws` suggestion). The
household live-sync channel is a **SignalR hub mapped at `/hub/live`**, mapped
before the SPA catch-all so the frontend fallback cannot swallow it.

### Connection and authentication
- The hub is reached over the **WebSockets transport only** with
  `skipNegotiation: true`, so no `/negotiate` POST is made. The hub path is
  exempt from `CsrfMiddleware` (a body-less, same-origin, cookie-authenticated
  upgrade); `SetupGatingMiddleware` and `MustChangePasswordMiddleware` still
  apply to the connect request.
- **Multiuser:** the upgrade requires the existing opaque `faf_session` cookie;
  no valid session → the connection is refused (no anonymous socket). **No
  tokens in query strings or URLs.**
- **Single-user:** there is no cookie, so the socket acts as the **system
  actor** (`actor_user_id` is `null`).
- Connect guards: a **same-origin** `Origin` check (an absent `Origin`, e.g. a
  non-browser client, is allowed; a foreign origin is refused), the existing
  `IpAllowlistMiddleware` (health/loopback exemptions unchanged), a bounded
  inbound message size (**32 KiB**, `MaximumReceiveMessageSize`), and a
  per-session (**8**) / per-household (**32**) connection cap.
  `EnableDetailedErrors = false`.

### Direction and client method
- **Server → client notifications only.** The hub exposes **no client-invokable
  methods**; all writes stay on the REST API, which enforces permissions, CSRF,
  and historical rules (docs/SECURITY.md §16).
- Every notification is delivered through the single client method **`event`**
  (`connection.on('event', …)`), with `type` as the discriminator, so a new
  event type never needs a new client method. Notifications are broadcast to
  every connected session of the household (group `household`), including the
  originating one.

### Envelope
Coarse and stable (snake_case wire fields; no entity bodies, no diffs):

```json
{
  "type": "groceries.updated",
  "entity": "groceries",
  "id": null,
  "operation_id": null,
  "actor_user_id": 3,
  "timestamp": "2026-10-12T13:20:41.000Z"
}
```

`type` is required; the rest are nullable as applicable. `timestamp` is an
ISO-8601 instant (UTC offset). `actor_user_id` is the acting user, or `null`
for the system actor.

### Event types
| Type | Emitted by |
|------|-----------|
| `calendar.updated` | calendar entry create/update/delete; single reroll apply + undo; batch apply + batch undo |
| `calendar.lock_changed` | lock/unlock of a date, and only that (a lock toggle does **not** also emit `calendar.updated`) |
| `groceries.updated` | manual item create/update/delete; staple create/update; add-to-list; check/uncheck (per-user preferences never broadcast) |
| `recipe.updated` | recipe create/update (name, ingredients, units/quantities, proteins, tags, diet, status); image set/replace/remove; delete |
| `settings.updated` | household settings; security settings; reference-data writes (departments incl. name/`sort_order`, units, ingredients + merges, proteins, tags) |
| `session.revoked` | logout, admin session revocation, user disable, session expiry |

An event is emitted only after a **successful, committed** write — never on a
failed/rolled-back operation or a validation error.

### Revocation
A valid session is required at connect **and** re-validated per broadcast.
Logout, an admin revocation, disabling the user, or session expiry sends
`session.revoked` to that session's connections and **aborts** them. If the
per-broadcast re-validation read itself fails, the broadcast **fails closed**
(that event is skipped for every connection) rather than delivering to
unvalidated sockets.

### Client behaviour
- One connection per tab; automatic reconnect with exponential backoff (1 s,
  2 s, 4 s … capped at 30 s).
- On **every** (re)connect the client runs one full live-sync
  query-invalidation pass, reconciling anything missed while offline. There is
  **no server-side event log, sequence numbers, or replay**.
- Each event invalidates the mapped TanStack Query keys; the client
  invalidates/refetches and never applies an entity body. `session.revoked`
  forces a sign-out and stops reconnecting.
- The connection closes on logout.

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
  - The same routes expose the ten randomizer scoring settings (docs/M6_PLAN.md §1.3). `GET` and the `PATCH` response return all ten; `PATCH` validates each against its range (default in parentheses): `randomizer_repeat_lookback_days` (0–60, 7), `randomizer_repeat_lookahead_days` (0–60, 14), `randomizer_repeat_multiplier` (0.01–1.00, 0.10), `randomizer_special_day_multiplier` (0.00–1.00, 0.02), `randomizer_protein_multiplier` (0.10–1.00, 0.70), `randomizer_protein_neighbor_distance` (1–7, 1), `randomizer_favorite_multiplier` (1.00–2.00, 1.10), `randomizer_spacing_near_days` (1–30, 3), `randomizer_spacing_far_days` (1–60, 8), and `randomizer_spacing_search_days` (7–365, 90). Integer fields reject non-integers; multiplier fields reject values outside the range. After per-field validation passes, the effective `randomizer_spacing_far_days` (patch value or the stored value when omitted) must be greater than or equal to the effective `randomizer_spacing_near_days`; a violation returns `400 VALIDATION_FAILED` with a field error on `randomizer_spacing_far_days`. The Household settings UI exposes all ten.
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
- `statistics` keys are always present. Each is null when the recipe has no such history (never a fake zero); otherwise M11 fills it — `last_eaten`/`next_planned` are household-local dates (`yyyy-MM-dd`) and `times_eaten` is the historical occurrence count. The same three values are filled on the list items and by `GET /statistics/recipes/{id}` (docs/API.md §14). **Write responses** (`POST /recipes`, `PATCH /recipes/{id}`) return the `statistics` object with **null** metrics (creation has no history and the write path does not recompute it); re-read the recipe or call §14 for live values.

### Routes

- `GET /recipes` — list envelope `{ items, total, limit, offset }`. Each item carries `id`, `name`, `status`, `is_favorite`, `is_vegan`, `is_vegetarian`, `image`, `last_eaten`, `next_planned`, `times_eaten`, `created_at`, `updated_at`; the three metrics are computed in one aggregate query keyed by the page's recipe ids (no per-card fetch, docs/M11_PLAN.md §1.27) and stay null when the recipe has no such history. Query: `search` (normalized name substring), `status` (`normal`, `manual_only`, `retired`), `favorite`, `vegan`, `vegetarian`, `protein_id`, `tag_id`, `sort`, `limit` (default 24, max 100), `offset`. `sort` is one of `recently_added` (default), `name`, `favorites`, `recently_eaten`, `least_recently_eaten`, `most_eaten`. The three history sorts are now live: `recently_eaten` orders by latest eaten date, `least_recently_eaten` by oldest eaten date, and `most_eaten` by eaten count; ties break on `id`, and recipes with no history sort last. Invalid values return `400 VALIDATION_FAILED`.
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
