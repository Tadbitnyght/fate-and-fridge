# Fate & Fridge — Testing Strategy

## 1. Philosophy
The application contains stateful household data and time-sensitive logic.

Testing is mandatory.

Tooling:
- Backend: xUnit v3 with FluentAssertions
- PostgreSQL integration tests: Testcontainers
- Frontend: Vitest with Testing Library
- End-to-end: Playwright against a running stack

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
At least two browser sessions.

Verify:
- grocery checked on A updates B
- manual grocery added on A updates B
- calendar meal change updates B
- lock state updates B
- stale client cannot overwrite without expected conflict handling

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
