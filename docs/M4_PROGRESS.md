# M4 Execution Progress

Last updated: 2026-10-04 (M4 in progress; paused mid-M4-11 by user request)

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

Notes: the Roslyn MCP workspace is stale and does not see M4 `Imports/*.cs` files; every
agent used `dotnet build`/`dotnet test` as the effective gate and reported the fallback.
`OPENCODE_MCP_SETUP.md` has a pre-existing working-tree deletion that must NOT be committed.

## In progress: M4-11 (#62) — Duplicate source URL warning

Branch: `feature/62-m4-11-duplicate-url-warning` (uncommitted working tree).
Backend + partial frontend are written; **not yet committed, not fully tested**.

### Already done and verified
- `Services/RecipeService.cs` — `BuildNormalizedSourceUrl` now additionally canonicalizes
  trailing slash (non-root) and query-parameter order (sorted), alongside existing
  scheme/host lowercase, default-port drop, fragment drop. Re-folding is idempotent.
- `Imports/RecipeImportService.cs` — `FindSourceUrlMatchAsync` hardened: exact canonical
  equality first, then a bounded same-origin in-memory re-fold fallback for legacy rows.
  Read-only. `BuildOriginPrefixes` helper added.
- `tests/.../ImportEndpointsIntegrationTests.cs` — added `StoredSourceUrl` const and:
  - `Import_check_matches_the_normalized_source_url_variants` (7-case theory; PASSES)
  - `Import_recipe_url_populates_the_normalized_source_url_match` (4-case theory; fixed a
    bad trailing-slash case that lacked the stored query; PASSES after fix)
  - `Import_check_source_url_match_creates_no_rows` (no-write; PASSES)
  Verified: `dotnet test -c Release --filter "FullyQualifiedName~ImportEndpointsIntegrationTests"`
  → 40 passed; `RecipeEndpointsIntegrationTests` → 24 passed (no normalizer regression).
- `frontend/src/features/import/ImportReview.tsx` — added `DuplicateSourceDialog`
  (role=alertdialog; Open existing recipe / Import anyway) and `acknowledgedDuplicate`
  state, rendered when `result.source_url_match !== null`; existing name-match warning kept.
  Frontend `npm run typecheck` passes.

### Remaining work for M4-11
1. Add CSS for `.import-review__duplicate` (+ `-actions`) in `frontend/src/index.css`
   near the `.import-review__warning` block (~line 1874); mobile touch targets.
2. Frontend tests:
   - `frontend/src/features/import/ImportPage.test.tsx`: duplicate dialog shows with the
     three actions; "Open existing recipe" links to `/recipes/{id}/edit`; "Import anyway"
     dismisses and allows save; "Cancel"/page cancel discards; no dialog when no match.
   - `frontend/e2e/import.spec.ts`: hermetic duplicate case (route-intercepted) covering
     the dialog actions.
3. Run gates:
   - Backend: `export FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`;
     `dotnet format --verify-no-changes`; Release build; Release tests (import suites +
     full); `dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`.
   - Frontend via `/tmp/opencode/fe-mirror.sh`: `npx prettier --check src`, `npm run lint`,
     `npm run typecheck`, `npm run test` (full), `npm run build`,
     `npx playwright test e2e/import.spec.ts`.
4. Commit backend + frontend on the branch (e.g. `feat(import): duplicate source URL warning (#62)`).
5. Independent reviewer pass (delegate to reviewer) — confirm no blocker/major, verify the
   legacy-fallback bound and no-write, then merge to `main`, push, close #62.

### After M4-11
- M4-12 (#63) Similar-name warning — `ImportDiagnostics` scoring (folded equality,
  word-boundary containment, share-token overlap, small edit distance) + rename recheck UI.
- M4-13 (#64) Import security tests + M4 verification sweep — full §4 battery, docs,
  manifest M4 `complete` with evidence, `CHANGELOG.md`, tag `m4-complete`, close #51.

## Final M4 completion checklist (for M4-13)
- Set M4 `status: 'complete'` + `completedOn` + evidence in
  `frontend/src/features/progress/milestones.ts`; update any affected
  `milestones.test.ts` expectations.
- Update `docs/API.md` (import contract already documented through §15) and `CHANGELOG.md`.
- Push `main`, tag `m4-complete`, close tracking issue #51.
