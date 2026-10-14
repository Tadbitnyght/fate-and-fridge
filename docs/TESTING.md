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
    npx playwright test e2e/stack/live-sync.spec.ts
  ```

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

## 11. Accessibility Tests
- keyboard navigation
- focus trapping in dialogs
- labels
- checkbox semantics
- color-independent status
- responsive tap targets
- print output legibility

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
