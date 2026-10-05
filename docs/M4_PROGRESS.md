# M4 Execution Progress

Last updated: 2026-10-05 (M4 in progress; M4-11 merged, M4-12 next)

## Completed and merged to `main`

| Issue | Deliverable | Final commit |
|-------|-------------|--------------|
| M4 plan | `docs/M4_PLAN.md` locked; manifest M4 `in_progress`; issues #51–#64 created | 821dba5 |
| M4-01 (#52) | SSRF-hardened `SafeUrlFetcher` + `ImportUrlPolicy` | ed41425 |
| M4-02 (#53) | schema.org JSON-LD parser + sanitizer + `ParsedRecipe` | 52d0aaf |
| M4-03 (#54) | AngleSharp HTML fallback parser (depth-safe) | 41dcfe5 |
| M4-04 (#55) | Draft-only import endpoint + `RecipeImportService` + throttle | 3b35ded |
| M4-05 (#56) | Fraction/unit normalization (`IngredientLineParser`/`ImportNormalizer`) | f27640a |
| M4-06 (#57) | `IngredientMatcher` + wiring | 8816b76 (+ f03062e doc) |
| M4-07 (#58) | Image downloader `POST /recipes/{id}/image/from-url` | 2d1ffe5 |
| M4-08 (#59) | Import review screen + typed client + route | 7665a5b |
| M4-09 (#60) | Unknown ingredient flow | 02271b9 |
| M4-10 (#61) | Unknown custom unit flow | b0ed4ee |
| M4-11 (#62) | Duplicate source-URL warning + canonical folding + legacy fallback | a83c749, fd4a0c0 |

Notes: the Roslyn MCP workspace was refreshed and now resolves the M4 `Imports/*.cs`
files (diagnostics clean); `dotnet build`/`dotnet test` remain the release gate.
Frontend Vitest/Playwright workers time out on the 9p share `/mnt/y`, so frontend
gates run from the local mirror `/tmp/opencode/fe-mirror` (rsync/tar copy of `frontend/`).
`OPENCODE_MCP_SETUP.md` has a pre-existing working-tree deletion that must NOT be committed.

M4-11 evidence: backend full suite 1575 passed; import suites 40 + service 15; frontend
Vitest 214, build/lint/typecheck clean, Playwright `e2e/import.spec.ts` 8 passed (incl.
mobile). Independent review: no blocker/major; minors fixed on-branch.

## Next: M4-12 (#63) — Similar-name warning

- Replace the bounded `NormalizedName.Contains` query in
  `RecipeImportService.FindNameMatchesAsync` with deterministic scoring
  (`docs/M4_PLAN.md §1.9`): folded equality, word-boundary containment, shared-token
  overlap, and a small edit distance on folded forms; bounded output (max 5).
- Add a pure, ICU-independent scorer (new `Imports/RecipeNameSimilarity.cs`) + unit tests.
- Wire a rename recheck in the review screen via `POST /import/check` (`useCheckImport`
  already exists) so editing the name refreshes the similar-name warning.
- Update `docs/API.md §15` and add tests for the scoring matrix and rename recheck.

## Then: M4-13 (#64) — Import security tests + M4 verification

Full §4 battery, docs, manifest M4 `complete` with evidence, `CHANGELOG.md`,
tag `m4-complete`, close #51.

## Final M4 completion checklist (for M4-13)

- Set M4 `status: 'complete'` + `completedOn` + evidence in
  `frontend/src/features/progress/milestones.ts`; update any affected
  `milestones.test.ts` expectations.
- Update `docs/API.md` (import contract already documented through §15) and `CHANGELOG.md`.
- Push `main`, tag `m4-complete`, close tracking issue #51.
