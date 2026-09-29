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
/api/v1/auth
/api/v1/users
/api/v1/calendar
/api/v1/meals
/api/v1/recipes
/api/v1/ingredients
/api/v1/units
/api/v1/proteins
/api/v1/tags
/api/v1/groceries
/api/v1/statistics
/api/v1/settings
/api/v1/import
/api/v1/admin/audit
/api/v1/admin/history
/api/v1/admin/backup
```

---

## 3. General Rules
- JSON request/response
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
