# M4 Execution Progress

Last updated: 2026-10-05 — **M4 complete** (tag `m4-complete`).

Final verification report: `docs/M4_VERIFICATION.md`.

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
| M4-12 (#63) | Similar-name scoring + rename recheck | e7512e9, 332a64a |
| M4-13 (#64) | Import security tests + M4 verification sweep | _this milestone-completion commit_ |

Implementation notes:
- The Roslyn MCP workspace resolves the M4 `Imports/*.cs` files (diagnostics clean);
  `dotnet build` / `dotnet test` remain the release gate.
- Frontend Vitest/Playwright workers time out on the 9p share `/mnt/y`, so frontend
  gates run from the local mirror `/tmp/opencode/fe-mirror` (a copy of `frontend/`).
- `OPENCODE_MCP_SETUP.md` had a pre-existing working-tree deletion that was never
  committed.

## Completion checklist (all done)

- M4 `status: 'complete'` + `completedOn` + `tag` + evidence in
  `frontend/src/features/progress/milestones.ts`; `milestones.test.ts` updated.
- `docs/API.md §15` reflects the M4-11 canonical folding and M4-12 scoring.
- `CHANGELOG.md` records Milestone 4.
- `docs/M4_VERIFICATION.md` maps the §4 battery to its tests and records evidence.
- `main` pushed; annotated tag `m4-complete` cut; tracking issue #51 closed.
