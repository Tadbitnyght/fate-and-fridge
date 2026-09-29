# AGENTS.md — Fate & Fridge / Operation Dinner Plate

## Project Identity
- Product: **Fate & Fridge**
- Codename: **Operation Dinner Plate**
- Type: Self-hosted Dockerized web application
- Primary use: Household dinner planning, recipe management, meal randomization, and grocery generation
- Deployment target: TrueNAS SCALE first, generic Docker hosts also supported

## Prime Directive
Fate & Fridge must feel like a **simple household meal planner hiding a sophisticated database**, not a database application pretending to be a meal planner.

Primary user questions:
1. What's for dinner?
2. What's coming up?
3. What do we need from the store?

All implementation choices should support those outcomes.

---

## AI Team Roles

### Lead Developer
The Lead Developer owns architecture and scope.

Responsibilities:
- Read all project specifications before assigning work.
- Break milestones into focused issues.
- Define and preserve stable interfaces.
- Approve schema changes and migrations.
- Approve API contract changes.
- Prevent scope creep.
- Resolve specification conflicts.
- Review implementation architecture before merge.

The Lead should not casually rewrite requirements.

### Implementer
The Implementer works one issue at a time.

Required workflow:
1. Read the relevant spec files.
2. Inspect the existing implementation before modifying code.
3. Identify affected tests.
4. Write or update tests first when practical.
5. Implement the smallest correct change.
6. Run unit tests.
7. Run integration tests where relevant.
8. Run linting and type checking.
9. Document migration/API impact.
10. Report exactly what changed and what remains.

The Implementer must not redefine product behavior.

If the specification appears contradictory, stop that part of implementation and escalate to the Lead.

### Independent Reviewer
The Reviewer must assume the implementation may be subtly wrong.

Review for:
- Requirement compliance
- Authorization bypass
- Historical-data integrity
- Migration safety
- Race conditions
- Date/timezone bugs
- Randomizer correctness
- Grocery math/conversion errors
- Accessibility
- Mobile usability
- Import security / SSRF
- Error handling
- Concurrency issues
- Scope creep
- Missing tests

The Reviewer must independently run relevant tests and not approve work solely because Implementer-written tests pass.

---

## Required Repository Documents
Before major implementation, agents must consult:
- `docs/PRODUCT_SPEC.md`
- `docs/ARCHITECTURE.md`
- `docs/DATABASE.md`
- `docs/API.md`
- `docs/RANDOMIZER.md`
- `docs/SECURITY.md`
- `docs/TESTING.md`
- `docs/MILESTONES.md`
- `docs/V2_BACKLOG.md`

---

## Tech Stack
Preferred V1 stack:
- Backend: .NET 10 (LTS) + ASP.NET Core, C# 14, minimal APIs
- ORM: Entity Framework Core (Npgsql provider)
- Migrations: EF Core migrations
- Database: PostgreSQL
- Frontend: React + TypeScript + Vite
- Query/state: TanStack Query
- Live sync: SignalR over WebSockets
- Testing: xUnit, FluentAssertions, Testcontainers, Playwright
- Deployment: Docker Compose
- Password hashing: Argon2id via a vetted library

Do not add infrastructure such as Redis unless a demonstrated need exists.

---

## Architecture Rules
1. Frontend communicates through backend API only.
2. No frontend component talks directly to database concerns.
3. Randomization lives in a dedicated domain/service layer.
4. Grocery aggregation and unit conversion live in dedicated services.
5. Recipe import is isolated behind import/parsing services.
6. Authorization must be enforced server-side for every write endpoint.
7. Historical date immutability must be enforced server-side.
8. Persistent data must never depend on the replaceable application container filesystem.
9. Database schema changes require migrations.
10. Destructive migrations require a pre-migration backup.
11. Core app functionality must work without internet.
12. No telemetry by default.
13. Do not introduce mandatory external AI dependencies.

---

## Scope Rules
New ideas are classified as one of:
- V1 blocker
- V1.x enhancement
- V2 candidate
- Rejected

Do not add a new feature to V1 merely because it is easy.

---

## Historical Data Rules
Past dates are immutable in normal use.

Only Admin may alter past calendar data, and only through the specialized historical-correction workflow with:
- explicit warnings
- reason entry
- confirmation checkbox
- final confirmation
- audit log entry

Historical display data must survive recipe renames, retirement, or admin deletion.

---

## Randomizer Rules
The randomizer must:
- select only `Normal` recipes
- look 7 days backward and 14 days forward for exact-recipe repeats
- heavily suppress exact repeats in that window
- inspect direct previous/next days for shared primary protein
- prefer greater spacing from prior/future occurrences
- use favorite weighting only as a weak tie-breaker
- respect locked dates
- never alter dates outside the active reroll range
- use secure randomness
- support reproducible debugging metadata without predictable user-facing seeds

See `docs/RANDOMIZER.md`.

---

## Grocery Rules
- Grocery list is a live projection of planned recipe ingredients plus manual items.
- Manual items survive meal-plan recalculation.
- Compatible unit families may combine.
- Incompatible measurements must remain separate.
- Never guess package-size conversions.
- Printed grocery output follows user-defined department order.
- Checked items persist.

See `docs/PRODUCT_SPEC.md` and `docs/DATABASE.md`.

---

## Import Rules
Recipe URL import must:
1. Try schema.org Recipe JSON-LD first.
2. Fall back to best-effort HTML parsing.
3. Normalize only obvious formatting/units/fractions/aliases.
4. Never invent uncertain values.
5. Always require user review before permanent save.
6. Protect against SSRF and unsafe network targets.
7. Enforce timeouts, size limits, content-type checks, and redirect limits.

See `docs/SECURITY.md`.

---

## Git Workflow
Recommended branches:
- `main`
- `develop`
- `feature/<issue>`
- `fix/<issue>`

Each substantive change should map to an issue.

Pull requests should include:
- linked requirement/issue
- implementation summary
- tests added
- migration impact
- API impact
- screenshots for UI changes
- known limitations

---

## Definition of Done
A task is complete only when:
- requirement is implemented
- authorization is tested
- automated tests pass
- lint/type checks pass
- database migrations are validated when applicable
- error states are handled
- mobile behavior is tested when relevant
- accessibility is considered
- docs are updated
- independent review is complete
- no unrelated scope is introduced

---

## Do Not
- Do not silently broaden scope.
- Do not hard-code behavior that the spec marks configurable.
- Do not use frontend-only permission enforcement.
- Do not silently alter future meals outside the selected reroll range.
- Do not hard-delete historical references without preserving snapshot integrity.
- Do not execute third-party JavaScript during recipe import.
- Do not expose PostgreSQL publicly by default.
- Do not store plaintext passwords.
- Do not store critical persistent settings only inside the app container.
