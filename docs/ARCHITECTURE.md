# Fate & Fridge — Architecture

## 1. Architectural Goals
- Simple household UX
- Strong data integrity
- Safe historical records
- Modular planner logic
- Easy Docker deployment
- Offline-first core behavior
- Future-ready API surface
- Future-ready service-account model
- Easy migration from LAN-only to externally reachable setups

---

## 2. High-Level Components

```text
Browser / Mobile Browser
        |
        v
 React + TypeScript Frontend
        |
        v
      REST API
 ASP.NET Core Backend
        |
        +---------------------+
        |                     |
        v                     v
 Domain Services         SignalR Hub
        |
        +-------------------------------+
        |        |        |             |
        v        v        v             v
 Planner    Grocery    Import       Admin/Auth
 Service    Service    Service       Services
        |
        v
    EF Core ORM
        |
        v
    PostgreSQL
```

---

## 3. Containers

### Application
Contains:
- ASP.NET Core backend (minimal APIs)
- frontend static assets/build
- SignalR hub (live sync)
- migration runner
- update checker
- import services

### PostgreSQL
Private Docker network only.

No host-exposed database port by default.

---

## 4. Persistent Storage

Required persistent locations:
- PostgreSQL data
- `/data/config`
- `/data/images`
- `/data/backups`

Important:
Never rely on ephemeral application-container storage for required state.

Persistent application configuration can be split between:
- database configuration
- mounted config volume

Secrets required before DB access or during restore belong in mounted persistent config.

---

## 5. Backend Module Boundaries

Suggested:

```text
backend/
  src/FateAndFridge.Api/
    Program.cs
    Endpoints/
    Auth/
    Domain/
    Data/
    Repositories/
    Services/
    Imports/
    Hubs/
    Admin/
  tests/FateAndFridge.Api.Tests/
```

### Endpoints/
Minimal API route groups and HTTP concerns only.

### Auth/
Authentication, session/token validation, rate limiting hooks.

### Domain/
Business concepts and enums.

### Data/
DbContext, EF Core migrations, and persistence plumbing.

### Repositories/
Database access abstraction.

### Services/
Application business logic.

### Imports/
Website recipe parsing and normalization.

### Hubs/
SignalR hubs and event broadcasting.

### Admin/
Historical correction, destructive workflows, backups, audit query.

---

## 6. Service Boundaries

### PlannerService
Coordinates meal-generation and reroll requests.

### RandomizationEngine
Scores candidate recipes.

### CandidateScorer
Pure/testable candidate scoring.

### GroceryAggregationService
Builds live grocery list.

### UnitConversionService
Performs explicit compatible-unit conversion.

### RecipeImportService
Coordinates URL import workflow.

### StructuredRecipeParser
Handles JSON-LD Recipe data.

### HtmlFallbackParser
Handles non-JSON-LD fallback.

### IngredientMatcher
Maps imported names to canonical ingredients/aliases.

### BackupService
Creates and restores encrypted archives.

### AuditService
Writes meaningful audit events.

---

## 7. Frontend Structure

Suggested:

```text
frontend/src/
  api/
  components/
  features/
    calendar/
    recipes/
    groceries/
    statistics/
    settings/
    auth/
  pages/
  hooks/
  types/
  utils/
```

Frontend must not replicate business rules that belong on the server.

Frontend may present warnings/previews, but backend is authoritative.

Implemented foundation (M1-12):

```text
frontend/src/
  api/          client.ts (JSON + error envelope + CSRF), auth.ts, setup.ts, errorCodes.ts
  components/   AppShell.tsx (header/nav/outlet/footer), RequireSession.tsx, StatusPanel.tsx
  features/
    auth/       AuthProvider (TanStack Query session state), AuthContext/useAuth, messages
    setup/      useSetupStatus
  pages/        SetupPage, LoginPage, ChangePasswordPage, Calendar/Dashboard/… placeholders
  test/         mockFetch + render helpers
```

Routing rules: incomplete setup → `/setup`; unauthenticated multiuser → `/login`;
`must_change_password` → `/change-password`; single-user households enter the shell
directly. The API client stores the latest `X-CSRF-TOKEN` response header in memory
and echoes it on unsafe requests. Routing and session state are UI conveniences;
server-side authorization remains the only boundary.

---

## 8. State Strategy
Use:
- TanStack Query for server state
- local component state for transient UI
- WebSocket events to invalidate/update server queries

Do not introduce a global state library unless concrete needs appear.

---

## 9. API-First Rule
Every primary web UI action must map to an API operation.

This prepares for future:
- Alexa
- Google Home
- native mobile app
- external automations
- service accounts

Do not expose external API credentials in V1.

---

## 10. Authorization
Server-side authorization required for every protected endpoint.

Permission examples:
- `calendar.view`
- `calendar.edit`
- `groceries.view`
- `groceries.edit`
- `recipes.view`
- `recipes.edit`
- `reference_data.edit`

Admin-only operations are checked independently from these permissions.

---

## 11. Dates and Timezones
Store timestamps in UTC.

Store household timezone in settings.

Calendar date semantics must be evaluated in household timezone.

Past-date immutability must use household-local date, not server-local time.

Critical tests:
- midnight rollover
- DST transitions
- UTC/offset mismatch
- users in different client timezones

---

## 12. Historical Snapshot Strategy
Calendar records must preserve enough display data to remain meaningful if referenced recipes are later:
- renamed
- retired
- deleted by Admin

Recommended:
- live `recipe_id` nullable/reference
- snapshot recipe name
- snapshot special label
- snapshot display metadata as needed

For Admin hard deletion:
- preserve calendar snapshot
- replace live reference with tombstone/null-safe state

---

## 13. Randomization
Randomization belongs in service/domain code only.

Never implement scoring in:
- React
- route handlers
- ORM event hooks

The scorer should accept a fully defined context and return:
- chosen recipe
- score breakdown
- generation metadata

See `RANDOMIZER.md`.

---

## 14. Grocery Aggregation
Grocery list should be generated from source-of-truth meal plan + manual items.

Avoid persisting a second independent calculated grocery list unless required for caching.

Persist:
- manual items
- reusable items/staples
- checkbox state
- range preferences

Calculate recipe-derived totals from current meals.

---

## 15. Unit Conversion
Model conversion by dimension/family.

Example families:
- mass
- volume
- count
- custom/nonconvertible

Only convert within a defined family.

Never infer:
- onion -> cups
- can -> ounces
- jar -> ounces

unless future package metadata explicitly enables it.

---

## 16. Recipe Import
Recipe import is an untrusted-input subsystem.

Flow:
1. Validate URL.
2. Fetch safely.
3. Parse structured Recipe JSON-LD.
4. If unavailable, fallback parse HTML.
5. Normalize safe obvious values.
6. Match ingredients/units.
7. Return draft.
8. User reviews.
9. User saves.

Imported content is never auto-persisted before review.

---

## 17. WebSockets
Use WebSockets for household live sync.

Events should be coarse and stable, e.g.:
- `calendar.updated`
- `groceries.updated`
- `recipe.updated`
- `settings.updated`

Prefer client query invalidation/refetch over duplicating entire application state through socket payloads.

---

## 18. Backups
Backup service produces encrypted portable archive.

Persistent recovery key:
- created during setup
- stored outside ephemeral container
- exportable separately

Restore should support a clean installation when correct recovery key is available.

Before destructive restore:
- create safety backup

---

## 19. Migrations
Use EF Core migrations (Npgsql provider).

Every schema change:
- migration required
- migration tested against realistic prior schema
- rollback or recovery path documented

Before destructive migrations:
- automatic DB backup

---

## 20. Update Checking
Update check should call a known release metadata endpoint.

No installation fingerprinting.

Send at most:
- current version
- ordinary HTTP metadata

Do not send:
- household name
- recipe counts
- user counts
- meal history
- unique telemetry ID

---

## 21. Scalability Expectations
V1 target:
- one household
- small number of concurrent users
- thousands of recipes feasible
- years of meal history feasible

Do not prematurely optimize for millions of users.

Optimize correctness and maintainability first.
