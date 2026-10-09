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
| BUG-003 | open | Calendar / Concurrency | Concurrent single-date `PUT`/reroll racing a batch Apply on a blank target can raise an unhandled unique-violation 500 | — |

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

