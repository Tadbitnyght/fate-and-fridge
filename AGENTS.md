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

### Docker Infrastructure (docker-admin)
The Docker Infrastructure specialist owns containers, Docker Compose, Docker contexts, volumes, networks, images, and the Docker MCP Gateway.

- Every Docker operation must be delegated to `docker-admin`; other agents (Lead, Implementer, Reviewer) must not run `docker`/`docker compose` shell commands or `docker_*` MCP tools themselves.
- `docker-admin` prefers normal Docker CLI and Compose commands over MCP tooling, uses explicit contexts for remote hosts, never switches the default context to production TrueNAS, and never deletes persistent volumes, datasets, backups, or production containers without explicit authorization.
- Exception: Testcontainers-based integration tests (`dotnet test`) start disposable containers through the test library's Docker API. That is ordinary test execution and does not require delegation.
- Enforcement lives in `opencode.jsonc` (global deny rules) and `.opencode/agents/docker-admin.md` (re-allow).

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

## C# Tooling Rule (Mandatory)
For any C# work, the Roslyn MCP server (MadQ RoslynMcp, `roslyn_*` tools) is the **primary and default toolset for every agent** — Lead, Implementer, Reviewer, Docker-Admin, and any future agent.

Use it for:
- research/orientation: `roslyn_list_types`, `roslyn_get_file_outline`, `roslyn_get_member_body`, `roslyn_get_type_members`, `roslyn_search_files`, `roslyn_semantic_search`
- symbol navigation: `roslyn_get_symbol_definition`, `roslyn_find_references`, `roslyn_find_callers`, `roslyn_get_call_graph`, `roslyn_get_type_hierarchy`, `roslyn_get_usings`
- diagnostics/validation: `roslyn_get_diagnostics`, `roslyn_build_project`, `roslyn_check_syntax`
- edits/refactors: `roslyn_replace_in_code`, `roslyn_write_file`, `roslyn_preview_rename`, `roslyn_apply_rename`, `roslyn_change_signature`
- project facts: `roslyn_get_project_info`, `roslyn_restore_packages`

Rules:
- Never start C# inspection with grep/find/full-file reads — use Roslyn symbol and member tools first.
- Shell/generic file tools are a fallback only when Roslyn cannot serve the need (non-C# files, generated code, a broken workspace, exact byte-level diffing). State why when falling back.
- After C# edits, run `roslyn_get_diagnostics` (and/or `roslyn_build_project`) before calling the change done; `dotnet build` remains the release gate.
- Implementation reports and reviews must state which Roslyn tools were used for C# work.
- Workspace notes (corrected 2026-10-07): `roslyn_*` uses **two different path bases**; mixing them up silently degrades the workspace to `AdhocWorkspace` and produces false `CS0234` storms:
  1. **`projectPath`** (every tool) is resolved against the **MCP server's working directory**, which is the opencode session root `/mnt/y` — *not* the loaded workspace root. Use the repo-relative `backend/src/FateAndFridge.Api/FateAndFridge.Api.csproj` / `backend/tests/FateAndFridge.Api.Tests/FateAndFridge.Api.Tests.csproj`, or (preferred) the **absolute** `.csproj` path. Passing `src/FateAndFridge.Api/...` resolves to the non-existent `/mnt/y/src/...` and silently loads an `AdhocWorkspace` with no NuGet references (hundreds of bogus `CS0234` "namespace not found" errors).
  2. **`filePath`** (read **and** write) is resolved against the **workspace root** — the directory tree of the loaded `.csproj`. For the backend project that is `/mnt/y/backend`, so use `src/FateAndFridge.Api/...` and `tests/FateAndFridge.Api.Tests/...` (or absolute paths). Shell-style paths such as `backend/src/...` ahead of a file are wrong here.
  3. Verify the loaded workspace with `roslyn_get_project_info` on the `.csproj`: it must report `is_msbuild_workspace: true` and a non-empty `package_references`. If a tool answers `invalid_project_path`, fix `projectPath` instead of falling back to shell.
- The tool's `.slnx` discovery is unreliable, so always pass an explicit `.csproj`. This workstation's SDK/artifacts env is pinned in `opencode.jsonc` (`FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`, `DOTNET_ROOT`, `ROSLYNMCP_MSBUILD_PATH`); if diagnostics still look wrong, confirm it with `roslyn_info`.
- Runtime globalization: this workstation runs with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, where ICU-backed APIs are silent no-ops — `string.Normalize(...)` returns its input unchanged and culture names are empty. Never build normalization, casing, or collation logic on those APIs. Use explicit, table-driven logic that is deterministic in both modes, and cross-check tests with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` (a real ICU mode) when Unicode is involved.

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
- `docs/FUTURE_FEATURES.md` (the consolidated V2/future registry; supersedes `docs/V2_BACKLOG.md`)

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

Every non-V1 idea raised at any time must be recorded in
`docs/FUTURE_FEATURES.md` (with its provenance and status) at the moment it is
discussed, so the backlog stays complete. That file is the single registry for
V2 and future work and supersedes the former `docs/V2_BACKLOG.md`.

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
- the milestone progress manifest (`frontend/src/features/progress/milestones.ts`) is updated when a milestone completes
- **the working project is rebuilt and redeployed from the completed commit and the live app is verified to reflect the milestone** (see "Milestone completion gate" below)
- independent review is complete
- no unrelated scope is introduced

### Milestone completion gate
A milestone must not be marked `complete` (in
`frontend/src/features/progress/milestones.ts`), have its `m*-complete` tag cut,
or have its tracking issue closed until the working project is updated, rebuilt,
and verified live. In order:

1. All tests, lint/type checks, builds, and migration checks pass on the completed
   commit.
2. The working project is rebuilt and redeployed **from that commit**. Docker
   operations are delegated to `docker-admin` (`scripts/deploy.sh up`, i.e.
   `docker compose up -d --build` on the deploy host), which applies pending
   migrations.
3. The live deployment is verified: the app is healthy, at least one new
   feature/endpoint of the milestone is reachable on the running instance, and the
   `/progress` page shows the milestone as complete. Evidence (image id/digest,
   health response, and the live proof) is recorded in the milestone verification
   document.
4. Only after step 3 may the manifest change be committed/pushed and the tag be
   created.

If the build, deploy, or live verification fails, the milestone stays
`in_progress`; do not mark it complete or tag it.

---

## Do Not
- Do not silently broaden scope.
- Do not hard-code behavior that the spec marks configurable.
- Do not use frontend-only permission enforcement.
- Do not silently alter future meals outside the selected reroll range.
- Do not hard-delete historical references without preserving snapshot integrity.
- Do not execute third-party JavaScript during recipe import.
- Do not mark a milestone complete or cut its `m*-complete` tag without rebuilding/redeploying and verifying the live app reflects it (see "Milestone completion gate").
- Do not expose PostgreSQL publicly by default.
- Do not store plaintext passwords.
- Do not store critical persistent settings only inside the app container.
