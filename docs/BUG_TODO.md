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
| BUG-001 | verifying | Import / UI | Recipe URL import crashes to a blank page when a parsed ingredient line has no unit | [#93](https://github.com/Tadbitnyght/fate-and-fridge/issues/93) |

---

## BUG-001 — Import crashes to a blank page on a unitless ingredient

- **Area:** Frontend import review (`features/import`, `features/recipes/IngredientFields.tsx`)
- **Severity:** High — the whole import flow is unusable; the SPA renders a blank page.
- **Reported:** 2026-10-08
- **Status:** verifying
- **Fix commit:** `4e0bc8d` on `fix/93-import-unitless-crash`; independent review + live redeploy pending.
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
