# Fate & Fridge — Bug TODO

Registry of confirmed defects. Every bug gets a stable `BUG-###` id, a root
cause, and a GitHub issue link. Add new bugs at the bottom; update `Status`
(`open` → `fixing` → `verifying` → `closed`) as work progresses. This file
complements the GitHub issue tracker (one issue per bug) and is the durable
list referenced by `AGENTS.md` workflow.

Status legend: `open` (confirmed, not started) · `fixing` · `verifying` ·
`closed` · `wontfix`.

| ID | Status | Area | Summary | GitHub |
|----|--------|------|---------|--------|
| BUG-001 | closed | Import / UI | Recipe URL import crashes to a blank page when a parsed ingredient line has no unit | [#93](https://github.com/Tadbitnyght/fate-and-fridge/issues/93) |
| BUG-002 | open | Import / UI | Nested ingredient-group rows never receive unknown-unit metadata (latent; unreachable with current drafts) | — |
| BUG-004 | open | Calendar / UI | Replacing a recipe entry with Eat out fails with a misleading "Choose a recipe" error (stale `recipe_id`) | [#105](https://github.com/Tadbitnyght/fate-and-fridge/issues/105) |
| BUG-005 | open | Recipes / UI | Saving a recipe stays on the recipe form instead of returning to the recipes list | [#118](https://github.com/Tadbitnyght/fate-and-fridge/issues/118) |
| BUG-006 | open | Recipes / UI | Recipe cards only open from the name; the whole card should open the editor | [#119](https://github.com/Tadbitnyght/fate-and-fridge/issues/119) |
| BUG-003 | open | Calendar / Concurrency | Concurrent single-date `PUT`/reroll racing a batch Apply on a blank target can raise an unhandled unique-violation 500 | — |
| BUG-007 | open | Auth / Query cache | The TanStack Query cache is not cleared on logout, so a second user can briefly see the first user's cached data in the same tab | — |
| BUG-008 | open | Groceries / Data | A live grocery line renders the ingredient label `Onion {owder`; likely stored/seeded data, needs a check that no code path emits a stray `{` | — |
| BUG-009 | verifying | CI / Dependencies | CI red on `main` since the M9 completion commits because the M9 test-only `Microsoft.AspNetCore.SignalR.Client 10.0.12` pulled the vulnerable `Microsoft.AspNetCore.SignalR.Common 10.0.12` (CVE-2026-70159) | [#139](https://github.com/Tadbitnyght/fate-and-fridge/issues/139) |

---

## BUG-001 — Import crashes to a blank page on a unitless ingredient

- **Area:** Frontend import review (`features/import`, `features/recipes/IngredientFields.tsx`)
- **Severity:** High — the whole import flow is unusable; the SPA renders a blank page.
- **Reported:** 2026-10-08
- **Status:** closed (2026-10-09)
- **Fix commit:** `4e0bc8d`, merged to `main` as `72c553c`.
- **Verified:** independent review verdict approve-with-findings (frontend 254/254 tests; the reviewer independently reproduced the crash by reverting the guards, then confirmed the fix). Live on image `sha256:8935d83f…` (bundle `index-WYIly1Sa.js`): `Recipes → Import from URL` now renders the review screen.
- **Follow-up nit:** the committed regression test asserts `raw: null` only; `raw: ""`/whitespace with `is_unknown: true` are fixed and reviewer-verified but not asserted in the suite.
- **GitHub:** [#93](https://github.com/Tadbitnyght/fate-and-fridge/issues/93)
- **Reproduction:**
  1. Open `Recipes → Import from URL`.
  2. Paste any recipe URL whose ingredient list contains a line with no unit
     (e.g. "1 onion", "Salt to taste") — i.e. almost every recipe.
  3. Click **Import**. The server parses successfully, then the app crashes to a
     blank page.
- **Observed:** `TypeError: Cannot read properties of null (reading 'trim')` at
  `IngredientFields.tsx:389` (`unknown.unit.raw.trim()`); React unmounts and the
  page is blank. Reproduced against the live deployment (`9b83d05`).
- **Root cause:** The server legitimately returns `unit: { "raw": null,
  "is_unknown": true }` for a line with no unit
  (`ImportNormalizer.ResolveUnit` returns `IsUnknown = true, Raw = null` for a
  blank unit; `ImportDraftUnit.Raw` is `string?`). The frontend
  (`api/importRecipes.ts`) types `raw` as `string`, and `importForm.ts`
  `unknownUnitLine()` builds an `UnknownUnitLine` with `raw: null`. The shared
  `IngredientFields` ingredient-row render then calls `unknown.unit.raw.trim()`,
  which throws on `null`.
- **Affected:** M4 import review; the bug predates M6 (the M6 work did not touch
  this path) but was only noticed now.
- **Fix direction:** treat `unit.raw` as nullable (mirror the server), do not
  create an "unknown unit" affordance when the unit text is blank (a blank unit
  is always allowed and needs no resolution), and guard the remaining `.trim()`
  call sites (`IngredientFields.tsx`, `importForm.validateUnknownUnits`).
- **Tests:** add an import-review render test with a unitless ingredient
  (`unit.raw: null, is_unknown: true`) asserting the review renders and saves
  without throwing.

---

## BUG-002 — Nested ingredient-group rows never receive unknown-unit metadata

- **Area:** Frontend import review (`features/import/ImportReview.tsx`, `features/recipes/IngredientFields.tsx`)
- **Severity:** Low — latent; not reachable with current server drafts.
- **Reported:** 2026-10-09 (found during the BUG-001 independent review)
- **Status:** open
- **Details:** `ImportReview` passes `unknownByRowKey` only to the top-level
  ingredient list; group `IngredientRowList` rows do not receive it, and
  `seedUnknownLines` seeds only `draft.ingredients` (the server draft has no
  `ingredient_groups`). The nested group branches of `validateUnknownUnits` and
  `IngredientFields` are therefore unreachable in the import flow today.
- **Impact:** none currently (import drafts are flat); becomes a real gap if
  grouped import drafts are ever produced.
- **Fix direction:** thread `unknownByRowKey` (and the create controls) into the
  group row lists and seed group lines, or remove the dead nested branches.

---

## BUG-003 — Concurrent single-date write racing a batch Apply can 500 on a unique violation

- **Area:** Calendar concurrency (`CalendarService.UpsertAsync`,
  `BatchRerollService.ApplyAsync`)
- **Severity:** Low–Medium — requires a precise concurrent race on the same blank
  target date; no data corruption.
- **Reported:** 2026-10-09 (found during the M7 backend independent review)
- **Status:** open
- **Details:** `calendar_entries.meal_date` is unique. A single-date
  `PUT`/`DELETE`/reroll path does not take the per-date
  `pg_advisory_xact_lock` (only `CalendarRandomizer` and the M7 batch
  apply/undo do), so it can interleave with a batch Apply that is inserting a
  new entry for the same blank date. If the single-date writer commits first,
  the batch Apply's insert raises an unhandled Postgres unique violation →
  HTTP 500 instead of a clean `BATCH_CONFLICT` (409).
- **Root cause:** pre-existing M6/V1 last-write-wins behavior —
  `CalendarService.UpsertAsync` takes no advisory lock and the unique index on
  `calendar_entries.meal_date` is enforced only by the database. M7 serializes
  its own target dates but cannot serialize against a lockless single-date
  writer.
- **Impact:** a rare 500 on a blank date; the batch Apply transaction rolls back
  so nothing is partially written.
- **Fix direction:** out of M7 scope. Either make every single-date calendar
  write take the same per-date advisory lock, or translate the unique-violation
  into a clean conflict at the API boundary.

---

## BUG-004 — Replacing a recipe entry with Eat out fails with a misleading "Choose a recipe" error

- **Area:** Calendar date editor (`features/calendar/DateDetailDialog.tsx`,
  `Services/CalendarService.cs`)
- **Severity:** Medium — blocks a normal edit; workaround is delete-then-recreate.
- **Reported:** 2026-10-09
- **Status:** open
- **GitHub:** [#105](https://github.com/Tadbitnyght/fate-and-fridge/issues/105)
- **Reproduction:** open a date that has a `recipe` entry → change **Type** to
  **Eat out** → fill the optional Restaurant (e.g. "Little Ceasers") → **Save**.
  The save fails with **"Choose a recipe for this date."** Deleting the day and
  then adding a new Eat out entry works.
- **Root cause (two parts):**
  1. `DateDetailDialog.tsx:203-208` — the Type `<select>` `onChange` clears
     `iconKey` when the new type has no icon, but does **not** clear
     `recipeId`/`recipeName`. Changing an existing recipe entry to `eat_out`
     therefore still sends `recipe_id` in the `PUT /calendar/{date}` full-replace
     body.
  2. The server rejects that with a `recipe_id` field error
     ("This entry type does not support a recipe."), and
     `CalendarService.RequiredCode` maps **any** `recipe_id` error — including the
     "does not support a recipe" case — to `CALENDAR_RECIPE_REQUIRED`. The UI maps
     that code to "Choose a recipe for this date.", so the user is told to add a
     recipe to an Eat out entry.
  The workaround (delete the day, then add) works because the fresh dialog starts
  with `recipeId = null`.
- **Affected:** Calendar date editor for any type change away from a
  recipe-carrying type (`recipe` → `eat_out`/`fend_for_yourself`/
  `go_to_family_dinner`/`vacation`), and potentially the reverse (stale
  restaurant/location/custom fields are sent for other types — they are stored
  but ignored).
- **Fix direction:**
  1. Frontend: when the entry type changes, clear `recipeId`/`recipeName`
     whenever the new type does not carry a recipe, and clear the other
     type-specific fields that no longer apply (restaurant/location/custom), so
     the full-replace body only contains applicable fields.
  2. Backend: make `RequiredCode` return `CALENDAR_RECIPE_REQUIRED` only for the
     genuinely-missing-recipe case; the "does not support a recipe" case should
     stay a generic `VALIDATION_FAILED`.
- **Tests:** a date-editor test that changes a recipe entry to `eat_out` and saves
  successfully; a backend test that an unsupported `recipe_id` returns
  `VALIDATION_FAILED` (not `CALENDAR_RECIPE_REQUIRED`).

---

## BUG-005 — Saving a recipe stays on the form instead of returning to the recipes list

- **Area:** Recipe editor (`features/recipes/RecipeFormPage.tsx`)
- **Severity:** Low–Medium (UX) — every save leaves the user on the editor.
- **Reported:** 2026-10-09
- **Status:** open
- **GitHub:** [#118](https://github.com/Tadbitnyght/fate-and-fridge/issues/118)
- **Reproduction:** open `/recipes/new` or an existing recipe, fill it in, press
  **Save** — the page stays on the recipe editor instead of the recipes list.
- **Root cause:** `handleSubmit` (edit path ~line 119-121) saves and shows a local
  "Recipe saved." notice but never navigates; the create path (~line 122-124)
  navigates to `/recipes/${created.id}/edit`, re-entering the editor. Nothing
  returns to `/recipes`.
- **Fix direction:** navigate to `/recipes` after a successful create or update
  (optionally pass a success notice via router state). The M4 import review flow
  navigates itself and is unaffected.
- **Tests:** a form test asserting save (create and edit) navigates to `/recipes`.

---

## BUG-006 — Recipe cards only open from the name

- **Area:** Recipe browser (`features/recipes/RecipesBrowser.tsx`)
- **Severity:** Low–Medium (UX) — the large photo/white box look clickable but are not.
- **Reported:** 2026-10-09
- **Status:** open
- **GitHub:** [#119](https://github.com/Tadbitnyght/fate-and-fridge/issues/119)
- **Reproduction:** on `/recipes`, click the recipe photo or the card's white box —
  nothing happens; only the recipe name link opens the editor.
- **Root cause:** each item is an `<li className="recipe-card">` with the image and
  body as plain elements; only the name is a `Link` (`recipe-card__name-link`, ~line 82).
- **Fix direction:** make the entire card activate the editor — wrap the card content
  in a single `Link` to `/recipes/{id}/edit`, or use a stretched-link overlay — while
  keeping keyboard access, preserving alt text, and avoiding nested interactive elements.
- **Tests:** a card test asserting a click on the image/body (not just the name)
  navigates to `/recipes/{id}/edit`, plus keyboard activation.

---

## BUG-007 — Query cache is not cleared on logout, leaking the previous user's data

- **Area:** Auth / TanStack Query cache (`features/auth/AuthProvider.tsx`,
  `main.tsx`)
- **Severity:** Medium — a privacy/correctness defect in multi-user mode
  (cross-user data exposure after re-login in the same browser tab).
- **Reported:** 2026-10-12 (found during the M8-07 Grocery UI independent
  review)
- **Status:** open
- **Details:** `main.tsx` creates one app-wide `QueryClient` with
  `defaultOptions.queries.staleTime = 30_000`. On logout, `AuthProvider`'s
  `logoutMutation.onSuccess` only calls
  `queryClient.setQueryData(sessionQueryKey, null)` and
  `invalidateQueries({ queryKey: sessionQueryKey })` — the rest of the cache
  (including per-user reads such as `['groceries','preferences']` and the
  grocery list) is left in place and is not reloaded. Because the cached
  entries are still "fresh" for up to 30s, the next user to log in in the same
  tab can be shown the previous user's cached groceries (and any other cached
  user-scoped data) until a refetch happens.
- **Reproduction:** log in as user A, open `/groceries` (populating the cache),
  log out, then log in as user B within ~30s. The grocery preferences/list can
  briefly render user A's values before the refetch replaces them.
- **Root cause:** logout invalidates only the session key; it never clears the
  `QueryClient`, and the app never reloads (`window.location.reload()`), so
  non-session queries survive the user change.
- **Impact:** multi-user household deployments can transiently expose one
  user's cached data to another; read-only and self-correcting on refetch, but
  still a confidentiality leak.
- **Fix direction:** out of M8-07 scope. Clear the cache on logout
  (`queryClient.clear()` / `queryClient.removeQueries()`), or perform a full
  SPA reload after logout; if the cache must persist, scope user-specific keys
  by session/user id and/or set `staleTime: 0` for user-scoped queries.
- **Tests:** a test that logs out and asserts no prior user's query data remains
  in the client cache (and that a subsequent login does not render the previous
  user's groceries before refetch).

## BUG-008 — Live grocery line renders an ingredient label containing `{`

- **Area:** Groceries / recipe ingredient data (`GET /api/v1/groceries` output;
  source data in recipes/ingredients)
- **Severity:** Low — cosmetic/display integrity of one ingredient name; no
  functional impact on aggregation or check state.
- **Reported:** 2026-10-12 (found during the M8-10 live browser verification)
- **Status:** open
- **Details:** On the live instance the grocery list (and the print view) show an
  item labelled `Onion {owder` (attributed to `Hamburger — Mon 10/12`). The
  literal stored string appears to be `Onion {owder` (a `{` replacing a `p`),
  which is characteristic of a stray composite-format/brace substitution rather
  than an HTML-encoding artifact.
- **Reproduction:** open `/groceries` (or `/groceries/print`) on the live
  instance and look for the `Onion {owder` line; the same label is returned by
  `GET /api/v1/groceries`.
- **Impact:** one misrendered ingredient name; the milestone's aggregation,
  ordering, attribution, and check-state behaviour are unaffected.
- **Fix direction:** check whether the stored recipe/ingredient name is simply
  user-typed data (then correct the data), and audit any code that could use a
  user-supplied name as a composite-format string (e.g. `string.Format`/`$""`
  composition) so no code path can inject a stray `{`.
- **Tests:** a regression test that an ingredient/recipe name containing `{`/`}`
  round-trips unchanged through import, recipe save, and the grocery projection.

---

## BUG-009 — CI red on `main` since the M9 completion commits (vulnerable SignalR test dependency)

- **Area:** CI / Dependencies
  (`backend/tests/FateAndFridge.Api.Tests/FateAndFridge.Api.Tests.csproj`)
- **Severity:** High — `main` cannot build; every push and PR fails the backend
  job (no production/data impact).
- **Reported:** 2026-10-14 (CI job "Backend (format, build, migrations, tests)")
- **Status:** verifying (fix committed; awaiting a green CI run on `main`)
- **Fix commit:** `fix(deps): bump SignalR client test dependency to 10.0.13 (CVE-2026-70159) (#139)`, on `main`.
- **GitHub:** [#139](https://github.com/Tadbitnyght/fate-and-fridge/issues/139)
- **Reproduction:** push any commit to `main` (or open a PR) and watch the
  "Backend (format, build, migrations, tests)" job fail at the **Restore** step.
- **Observed:**
  `error NU1903: Warning As Error: Package 'Microsoft.AspNetCore.SignalR.Common' 10.0.12 has a known high severity vulnerability, https://github.com/advisories/GHSA-334f-vvp4-7g6p`
- **Root cause:** the M9-01 work added `Microsoft.AspNetCore.SignalR.Client`
  **10.0.12** to the test project to drive the live-sync integration suites. Its
  transitive `Microsoft.AspNetCore.SignalR.Common` 10.0.12 is affected by
  **CVE-2026-70159 / GHSA-334f-vvp4-7g6p** (High, CVSS 7.5; SignalR
  stateful-reconnect `MessageBuffer.ResendAsync` backpressure DoS), affected range
  `>= 10.0.0 <= 10.0.12`, patched in 10.0.13. The repository sets
  `TreatWarningsAsErrors=true`, so the NuGet audit warning (NU1903) is promoted to
  an error and fails restore in CI.
- **Impact:** CI has been red on `main` since the M9 completion commits. The
  API/production project (`backend/src/FateAndFridge.Api`) was **never affected** —
  it carries no SignalR NuGet reference (server-side SignalR comes from the
  ASP.NET Core shared framework), and
  `dotnet list src/FateAndFridge.Api/... --vulnerable --include-transitive`
  reports it clean.
- **Fix direction:** bump the test-only `Microsoft.AspNetCore.SignalR.Client`
  reference from 10.0.12 to **10.0.13** so the transitive
  `Microsoft.AspNetCore.SignalR.Common` resolves to the patched version. Do not add
  SignalR packages to the API project.
- **Verification:** both
  `dotnet list ... --vulnerable --include-transitive` invocations report no
  vulnerable packages; `dotnet restore --force-evaluate` (the CI restore path)
  exits 0 with no NU1903; the M9 live-sync suites (`M9LiveSyncHubTests`,
  `M9LiveSyncEventMatrixTests`, `M9SessionRevocation*`) pass 76/76 against the
  bumped client.
- **Process lesson:** **CI must be green before a milestone is marked complete.**
  The M9 completion gate verified tests/build/deploy locally, but the red `main`
  CI job went unnoticed; a green CI run on the completion commit must be part of
  the milestone completion gate.



