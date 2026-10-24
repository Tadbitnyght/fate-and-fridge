# Fate & Fridge — Testing Strategy

## 1. Philosophy
The application contains stateful household data and time-sensitive logic.

Testing is mandatory.

Tooling:
- Backend: xUnit v3 with FluentAssertions
- PostgreSQL integration tests: Testcontainers
- Frontend: Vitest with Testing Library
- Frontend lint/format/types: ESLint, Prettier, tsc
- End-to-end: Playwright against a running stack

Note: FluentAssertions v8+ is licensed by Xceed (free for non-commercial
use). If this project ever needs a permissive license, AwesomeAssertions is
a drop-in MIT-licensed fork of the v7 API.

The most dangerous areas are:
- randomization
- dates/timezones
- authorization
- grocery unit conversion
- historical immutability
- recipe import
- backup/restore
- live synchronization

---

## 2. Test Layers

### Unit Tests
For:
- randomizer scoring
- unit conversion
- normalization
- alias matching
- date-window logic
- permission evaluation

### Service Tests
For:
- planner operations
- grocery aggregation
- import pipeline
- audit emission
- backup/restore orchestration

### API Integration Tests
For:
- auth
- permissions
- historical rejection
- CRUD workflows
- reroll preview/apply
- transactional behavior

### Frontend Component Tests
For:
- calendar controls
- recipe autocomplete
- grocery checkbox UX
- permission-aware UI
- mobile behavior

### End-to-End Tests
For:
- setup
- login
- create/import recipe
- generate meals
- reroll
- grocery flow
- history/admin correction
- backup/restore

---

## 3. Permission Matrix
Every write endpoint should be tested as:
- Admin
- Member with permission
- Member without permission
- unauthenticated where applicable

Do not infer endpoint protection from UI tests.

---

## 4. Date Tests
Required:
- household midnight rollover
- today editable
- yesterday rejected
- future accepted
- month boundary
- year boundary
- leap day
- DST spring transition
- DST fall transition
- browser timezone differs from household timezone

---

## 5. Randomizer Tests
See `RANDOMIZER.md`.

Must include:
- status eligibility
- 7-day backward window
- 14-day forward window
- forced repeats
- nearest-distance preference
- direct-neighbor protein
- multiple proteins
- favorites
- locks
- future scheduled meals
- special-day reduced penalty
- small recipe pools
- atomic batch apply
- preview non-persistence

---

## 6. Grocery Tests
- exact canonical ingredient merge
- aliases resolve to same canonical ingredient
- lb + oz conversion
- tbsp + cup conversion
- incompatible dimensions separate
- count vs volume separate
- custom units remain nonconvertible unless explicitly configured
- manual items survive reroll
- staples add correctly
- attribution lists meal names/dates
- checked state persists
- checked state survives reasonable range expansion
- department sorting respected

---

## 7. Import Tests
Fixture coverage:
- valid JSON-LD recipe
- multiple recipes in JSON-LD
- malformed JSON-LD
- no JSON-LD + parseable HTML
- unparseable page
- missing image
- duplicate source URL
- similar recipe name
- unit aliases
- fractional quantities
- unknown ingredients
- unknown custom units
- sections/instruction groups

Security:
- localhost URL
- private IP
- redirect to private IP
- oversized response
- invalid content type
- huge image
- too many redirects

---

## 8. Historical Tests
- normal API rejects past-date write
- frontend disables normal edit
- Admin correction route works
- reason required
- audit created
- snapshot preserved after recipe rename
- snapshot preserved after recipe hard delete

---

## 9. Backup Tests
- create backup
- correct key restores
- wrong key rejected
- corrupted archive rejected
- unsupported future schema rejected gracefully
- safety backup created before restore
- images restored
- users/permissions restored as designed
- history restored
- config restored
- archive path traversal blocked

---

## 10. Live Sync Tests
Two-device behaviour is covered at three levels (M9-08/M9-09,
`docs/M9_TEST_MATRIX.md`):

- **Backend two-real-client matrix** — the M9 hub/event-matrix suites open two
  real `HubConnection`s over the in-process TestServer (LongPolling) and assert
  every broadcast type reaches both clients (the originator included) and that
  revocation closes only the target session. Run:
  `$ARTIFACTS -class "*M9LiveSync*" -class "*M9SessionRevocation*"`.
- **Hermetic frontend** — `frontend/e2e/live-sync.spec.ts` stubs the socket with
  Playwright's `page.routeWebSocket` (no backend) and asserts an inbound
  `groceries.updated` frame refetches the list, the status indicator shows
  `Live`, and REST keeps working while the socket is down. Runs in the default
  `npx playwright test`.
- **Real-stack two-browser-context flow** — `frontend/e2e/stack/live-sync.spec.ts`
  drives two real browser contexts (Admin A, view-only Member B) against a live,
  fresh/unconfigured multiuser stack and verifies a grocery check on A updates B,
  a manual grocery added on A updates B, a calendar meal change updates B, and a
  lock change updates B. Skipped unless a stack is provided:

  ```bash
  E2E_BASE_URL=http://127.0.0.1:50900 E2E_SETUP_MODE=multiuser \
    E2E_STACK_LIVE_SYNC=1 npx playwright test e2e/stack/live-sync.spec.ts
  ```

  Both stack suites (`stack.spec.ts` and `stack/live-sync.spec.ts`) consume the
  single-shot first-run setup, so they cannot share one stack. The live-sync spec
  is therefore gated behind `E2E_STACK_LIVE_SYNC=1` and excluded from the default
  `npm run test:e2e` run; CI resets the stack (`docker compose down -v`, remove
  the bind-mounted data directory, then `up -d`, wait for health) and runs it as
  its own step after the acceptance suite, so each suite gets a fresh install.
  Note the reset must also delete the bind-mounted data directory (from the
  configured `HOST_DATA_DIR`): the stack persists state in bind mounts rather
  than named volumes, so `docker compose down -v` alone leaves the configured
  database in place and the stack is **not** fresh.

A stale client's write is handled by the existing conflict paths
(`UNDO_CONFLICT`, `BATCH_CONFLICT`, `BATCH_DRAFT_EXPIRED`, historical
immutability); M9 adds no version/ETag token.

---

## 10.1 Print Tests
Printing is proven at two levels (M10-07, `docs/M10_TEST_MATRIX.md`):

- **Print-media computed styles** — the hermetic Playwright specs call
  `page.emulateMedia({ media: 'print' })` and assert computed styles: black on
  white with no background fills, the app chrome (header/nav/footer/skip link/
  controls/Print/Back) hidden while the printable content survives, long names
  wrapping fully with no ellipsis, and the ≥≈10pt legibility floor.
- **PDF geometry** — the same specs call
  `page.pdf({ preferCSSPageSize: true })` (Chromium) so the CSS `@page` size is
  honoured, then assert the PDF `/MediaBox` **page size** (calendar A4 landscape,
  grocery A4 portrait) and the `/Type /Page` **page count** (one month per page;
  a month that still overflows at the floor spills to a second page). The named
  `@page` rules are asserted to be the only page-size owners (no unnamed `@page`,
  no app-wide size or margin).

The print assertions run in the **default hermetic suite** (no stack; the specs
mock `/api/v1/**` against the Vite dev server). The **real-stack print check** —
a landscape calendar PDF with one month per page and a portrait grocery PDF on
the deployed instance — is the M10-10 gate (`docs/M10_PLAN.md` §1.14), not a
hermetic assertion.

---

## 10.2 Statistics and History Tests

The M11 coverage map (§4 battery bullet → primary test) is
`docs/M11_TEST_MATRIX.md`; the independent verdict and measured numbers are
`docs/M11_VERIFICATION.md`.

- **Occurrence and eaten rules:** today's entry is Next Planned and never eaten;
  `host_family_dinner` / `holiday` / `misc` carrying a recipe count;
  `eat_out` / `fend_for_yourself` / `go_to_family_dinner` / vacation never
  contribute to recipe metrics but still count as meals and events.
- **Historical integrity:** retired recipes keep their meals and the retired
  badge; a hard-deleted recipe's meals survive as a stored-snapshot tombstone and
  no total drops; a rename is never double-counted; `never_eaten` counts every
  live recipe of any status with no eaten occurrence.
- **Household-local windows:** the calendar month and year counts use the
  household timezone (boundary tests use a non-UTC household and an injected
  clock, so a UTC-naive implementation fails) and the month count is the
  inclusive `[month start, month end]` window — never "first of the month
  through December 31" (regression #148).
- **Rankings and distribution:** top-10 bounds with deterministic tie-breaks;
  least-recently-eaten excludes never-eaten recipes; a multi-protein occurrence
  counts toward each protein bar; `no_primary_protein` is explicit.
- **History contract:** one month per page, newest first, strictly past days with
  no filler; bracket-capped at 366 days; malformed input → `VALIDATION_FAILED`;
  AND-combined filters by entry type, recipe id **or** stored snapshot name, and
  primary protein; walking the `newer`/`older` cursors visits every in-range
  month exactly once.
- **Permissions:** `recipes.view` for the overview and per-recipe metrics,
  `calendar.view` for history; 401 / 403 / Admin / single-user system actor and
  the edit-implies-view rule are all asserted, and the suite proves no eighth
  permission key was introduced.
- **Performance:** on the §12 baseline (2,000 recipes, 10 years of meal history)
  the overview and a single history month each answer well under the 1-second bar
  with a small fixed command count (no N+1); the recipes list with embedded
  metrics stays bounded. No index migration was needed.

---

## 10.3 Audit, Historical Correction and Hard Delete Tests (M12)

Coverage map: `docs/M12_TEST_MATRIX.md`; independent verdict and live evidence:
`docs/M12_VERIFICATION.md`.

- **Audit coverage:** every M12 write path (calendar meal changes and locks,
  recipe lifecycle, users and permissions, settings, grocery manual items and
  staples, logins, historical corrections, hard delete) writes exactly one
  `audit_events` row with actor, target and before/after; a failed audit write
  aborts the change; **no plaintext or temporary password, reset secret, recovery
  key or session token can reach any audit column** (asserted by a scan over the
  persisted `before_json`/`after_json`/`metadata_json`); rerolls are **not**
  written to `audit_events`; no page views are logged.
- **Merged audit viewer:** the timeline merges `audit_events` with the reroll
  rows of `calendar_entry_history` (kind → event-type mapping), never duplicating
  ordinary edits; filters AND-combine; keyset paging walks the log exactly once;
  the read is a single bounded query (no N+1, page-size independent).
- **Export:** CSV quoting for commas/quotes/newlines, formula-injection
  neutralisation, JSON equality with the filtered view, and the row cap
  returning `VALIDATION_FAILED` instead of truncating.
- **Retention/prune:** `audit_retention_days` validation (`0` = keep forever,
  else `7..3650`); the prune keeps a row exactly at the cutoff, deletes only
  `audit_events`, records `audit.pruned` after the delete in the same
  transaction, refuses "keep forever", and enforces the reason and typed `PRUNE`
  confirmation **server-side**.
- **Historical correction:** past-only; locked dates require the acknowledgement
  and stay locked; the reason and typed date confirmation are server-enforced;
  the entry change, the `is_historical_correction` history row and the audit row
  commit atomically (a forced audit failure rolls all three back); the frozen
  snapshot is refreshed so a later rename or hard delete still renders; other
  dates are untouched.
- **Hard delete:** past meals survive as tombstones with their snapshots and
  `include_groceries` true, and the statistics/meal-history totals do not drop;
  entries dated today or later are removed (and the recipe leaves the grocery
  projection and the randomizer's eligibility); a **stale** snapshot is never
  overwritten; image rows **and files** are removed; the `recipe.hard_deleted`
  audit row keeps the before-image; child rows cascade with no orphans; `404`
  for an unknown recipe; Admin-only; irreversible.
- **Dangerous actions (a11y/authority):** the shared dialog is keyboard-operable
  with a focus trap and labelled controls, and a direct API call that omits the
  reason or the typed confirmation fails regardless of the dialog.
- **Permissions:** the audit routes, the correction route and the delete route
  are Admin-only (anonymous 401, member 403, Admin/single-user 200) and the
  seven-key permission model is unchanged — no eighth key.

---

## 10.4 Backup, Restore, Key Export and Update Tests (M13)

Coverage map: `docs/M13_TEST_MATRIX.md`; independent verdicts and live evidence:
`docs/M13_VERIFICATION.md`.

- **Archive crypto:** round-trip equality; wrong key → `BACKUP_KEY_MISMATCH`;
  flipped key-check/salt → `BACKUP_KEY_MISMATCH`; every other header/body/tag
  byte → `BACKUP_INTEGRITY_FAILED`; truncation at a chunk boundary and mid-chunk,
  chunk reordering, and trailing bytes all fail; unsafe tar names (`..`,
  absolute, backslash, NUL, duplicates) are rejected on read **and** write; two
  archives from the same key carry different key-checks; the recovery key and any
  plaintext marker never appear in the ciphertext; a 64 MiB round-trip streams
  with flat memory.
- **Backup:** `pg_dump` is invoked without a shell and with `PGPASSWORD` only;
  a client older than the server refuses the backup; temp files live on the data
  volume and the archive is written temp-then-renamed; the metadata row moves
  pending → completed/failed with the ciphertext checksum and size; concurrent
  creates are refused; deletion needs the typed `DELETE`; the last five **safety**
  archives are retained (reconciled from disk) and manual archives are never
  auto-pruned; `backup.created`/`backup.deleted` are audited.
- **Restore:** every validation failure (key, format, integrity, checksum, newer
  schema) leaves **no** mutation, no marker, no safety backup; success creates and
  verifies the safety backup, enters maintenance (`503` except health), restores
  in a single transaction, empties `user_sessions`, writes `backup.restored` into
  the restored database, swaps images/dp-keys, writes the marker and requests the
  restart; a pre-commit failure leaves the data intact, while a **post-commit**
  failure still restarts and records the error; restore working directories and
  uploads are reclaimed.
- **Clean-install restore:** only before setup (`409` after); typed `RESTORE`; the
  pasted key's shape is validated before anything, adopted after archive/schema
  validation and before the safety backup, and never overwrites an existing key;
  no actor is recorded.
- **Key export:** Admin-only; typed `EXPORT`; attachment-only with
  `Cache-Control: no-store`; the key never appears in any audit field, error, log
  or DOM; the key file is never created by the export.
- **Version/updates:** `GET /about` is authenticated and returns the real SemVer +
  applied migration (degrading to `null`); the update request is exactly one
  bodiless GET with only `User-Agent: FateAndFridge/<version>` and
  `Accept: application/vnd.github+json` (asserted at the handler, including the
  exact header set); SemVer comparison (`0.9.0 < 0.10.0`), drafts/prereleases
  ignored, 24 h TTL, forced check, disabled ⇒ zero HTTP, oversized/404/5xx/timeout/
  offline → a short `last_error` with a 200 and no throw; `GET /updates` never
  touches the network; the banner is `role="status"`, dismissible per device by
  version, and links only to an absolute https release URL.
- **Permissions/scope:** every admin route is `RequireAdmin()`, `GET /updates` and
  `GET /about` are authenticated, the pre-setup restore is the only
  unauthenticated route and only before setup, the seven-key model is unchanged,
  and there is exactly one new migration.

---

## 11. Accessibility Tests
- keyboard navigation
- focus trapping in dialogs
- labels
- checkbox semantics
- color-independent status
- responsive tap targets
- print output legibility

### axe WCAG 2.1 AA gate (M14-02)
`frontend/e2e/a11y.spec.ts` runs `@axe-core/playwright` over the key routes
(setup, login, calendar + date detail, recipe browser + editor, groceries,
statistics/history, every settings section, progress) against a hermetic
`/api/v1/**` mock. It fails on any violation of `wcag2a`, `wcag2aa`, `wcag21a`
or `wcag21aa`; `best-practice` is not part of the gate. Calendar, groceries and
the recipe browser are also checked at 375 px and in the dark colour scheme.
Run it with:

```
cd frontend && npx playwright test e2e/a11y.spec.ts
```

The `a11y` job in `.github/workflows/ci.yml` runs the same command on every
push/pull request. Manual keyboard/focus/label/contrast evidence is recorded in
`docs/M14_VERIFICATION.md` §1.4.

---

## 12. Performance Baselines
Test representative household:
- 2,000 recipes
- 10 years meal history
- thousands of audit events
- large grocery list
- month reroll

Focus on:
- acceptable API latency
- no N+1 explosions
- no frontend freezing

Do not optimize beyond demonstrated need.

---

## 13. Definition of Done Test Gate
No issue is complete until:
- relevant unit tests pass
- relevant integration tests pass
- permission coverage exists
- lint passes
- type checking passes
- migration tests pass if schema changed
- reviewer independently validates acceptance criteria

### Dependency and image gates
Three permanent CI gates run on every push/pull request
(`.github/workflows/ci.yml`; policy in `docs/SECURITY.md` §20):

- **Dependency scan (NuGet)** — `dotnet restore` in the `backend` job, where
  `TreatWarningsAsErrors=true` promotes every NuGet audit warning
  (`NU1901`–`NU1905`, Low/Moderate/High/Critical, direct and transitive) to an
  error. A `dotnet list <project> package --vulnerable --include-transitive
  --format json` step publishes the `nuget-vulnerability-report` artifact for
  the record; it is not the gate.
- **npm audit** — `npm audit --audit-level=high` in the `frontend` job fails on
  **any** High/Critical advisory (fix or no fix; npm has no per-advisory
  ignore); the all-severity JSON report is uploaded as `npm-audit-report`.
- **Image scan** — the `image-scan` job builds the compose `app` image (the
  `Dockerfile` runtime stage) on a GitHub-hosted runner and scans it with a
  **pinned-by-tag-and-digest** Trivy (`aquasec/trivy:0.76.1@sha256:790fd66d…`):
  `--severity HIGH,CRITICAL --ignore-unfixed --exit-code 1`. An unfiltered
  all-severity JSON report is collected too. Reports are uploaded as
  `trivy-runtime-image-report` (missing reports fail the step).

Any High/Critical advisory **without** a fix needs an exception row in
`docs/M14_SECURITY_REPORT.md` (the image gate excludes unfixed findings via
`--ignore-unfixed`; npm has no advisory-ignore mechanism, so it must be upgraded
or the gate decision recorded). Medium/Low advisories are reported and tracked
by the npm/image artifacts but do not fail those gates; the NuGet gate is
stricter and fails on Moderate/Low audit warnings too (see the first bullet).

Run the dependency gates locally with:

```
cd backend && dotnet restore --force-evaluate
dotnet list src/FateAndFridge.Api/FateAndFridge.Api.csproj package --vulnerable --include-transitive
dotnet list tests/FateAndFridge.Api.Tests/FateAndFridge.Api.Tests.csproj package --vulnerable --include-transitive
cd ../frontend && npm audit --audit-level=high
```

The image scan needs Docker; it is not run by the application test suite (the
`stack` job already proves the image builds, starts and passes health).

### Milestone completion gate
A milestone is not complete until the working project has been rebuilt and
redeployed from the completed commit and verified live (see `AGENTS.md` →
"Milestone completion gate"). Concretely, before the milestone is marked
`complete`, tagged `m*-complete`, or its tracking issue is closed:

1. The completed commit passes the full backend and frontend gates (format/lint,
   type check, Release build, full test suites, Playwright, and
   `ef migrations has-pending-model-changes`).
2. `docker-admin` rebuilds and redeploys the stack from that commit
   (`scripts/deploy.sh up`, i.e. `docker compose up -d --build`), applying any
   pending migrations.
3. The live instance is healthy, at least one new feature/endpoint of the
   milestone is reachable, and the `/progress` page shows the milestone complete.
4. The image id/digest, health response, and live proof are recorded in the
   milestone's verification document (e.g. `docs/M*_VERIFICATION.md`).
