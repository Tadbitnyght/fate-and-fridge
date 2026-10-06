# M4 Verification — URL Import

Status: **complete** (2026-10-05, tag `m4-complete`)
Runbook: `docs/M4_PLAN.md` (locked decisions and §4 verification battery)
Tracking issue: #51 (M4-01…M4-13: #52–#64)

This report records how the M4 §4 verification battery is covered and the
evidence collected at completion. M4 adds no database tables or migrations; it
reuses `recipes`, `recipe_images`, `ingredients`, and `units`.

## How to run the battery

Backend (Testcontainers-backed; no test touches the public internet):

```bash
export DOTNET_ROOT=/home/tadbit/.dotnet FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts
dotnet build -c Release
dotnet test -c Release --no-build
dotnet format --verify-no-changes
dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api
```

Frontend (Vitest and Playwright workers time out on the 9p share `/mnt/y`, so
run from the local mirror `/tmp/opencode/fe-mirror`, a copy of `frontend/`):

```bash
rsync -a --delete --exclude dist /mnt/y/frontend/ /tmp/opencode/fe-mirror/
cd /tmp/opencode/fe-mirror
npx prettier --check . && npm run lint && npm run typecheck && npx vitest run && npm run build
npx playwright test
```

## §4 battery → coverage

| §4 area | Primary coverage |
|---------|------------------|
| URL policy matrix (schemes, credentials, IP literals, blocked ranges, host names, obfuscated literals, trailing dots) | `ImportUrlPolicyTests` |
| Redirect behavior (max hops, cross-host re-validation, redirect to blocked host/name/scheme, missing `Location`, relative `Location`, loop) | `SafeUrlFetcherTests`, `ImportUrlPolicyTests.EvaluateLocation_*` |
| DNS rebinding (public→private, mixed answers, connect-time pinning) | `SafeUrlFetcherTests` |
| Fetch limits (connect/read timeouts, HTML/image ceilings with and without/lying `Content-Length`, content-type enforcement, no credentials/cookies) | `SafeUrlFetcherTests`, `ImportFetchOptionsTests` |
| Parser robustness (malformed/truncated JSON-LD, arrays, `@graph`, multiple recipes, entities, huge values, deep nesting, no-recipe, microdata) | `StructuredRecipeParserTests`, `HtmlFallbackParserTests`, `HtmlDepthGuardTests` |
| Normalization table (vulgar/mixed fractions, ranges raw, word quantities, unit aliases/symbols/case, prep notes, parenthetical sizes) | `ImportNormalizerTests`, `IngredientLineParserTests`, `RecipeImportServiceTests` |
| Matching (canonical/alias exact, bounded suggestions, retired never suggested, ambiguity unknown) | `IngredientMatcherTests`, `RecipeImportServiceTests` |
| Draft integrity (no writes before save, no audit rows, no image files during parse) | `RecipeImportServiceTests`, `ImportEndpointsIntegrationTests` (image-file absence follows from the parse path never downloading images; there is no dedicated file-count assertion) |
| Duplicate checks (normalized URL equality variants; similar-name scoring: exact/containment/overlap/edit distance; rename recheck) | `RecipeSourceUrlNormalizationTests`, `RecipeNameSimilarityTests`, `RecipeImportServiceTests`, `ImportEndpointsIntegrationTests` |
| Permission matrix on every import route (Admin, `recipes.edit`, `recipes.view` only, `reference_data.edit` only, unauthenticated, single-user) | `ImportEndpointsIntegrationTests`, `ImportImageEndpointIntegrationTests` |
| Throttling (per-user page/image budgets, 429 shape, window reset, isolated budgets, single-user actor) | `ImportThrottleTests`, `ImportEndpointsIntegrationTests`, `ImportImageEndpointIntegrationTests` |
| Untrusted content (markup stripped, control/format chars removed, length limits, no HTML/JS/svg/data rendering) | `ImportTextSanitizerTests`, `StructuredRecipeParserTests`, `ImportReview.tsx` (React-escaped text) |
| Image download (signature/content mismatch, SVG/HTML/GIF/truncated rejection, size and dimension limits, replacement atomicity, no partial files) | `ImportImageEndpointIntegrationTests`, `RecipeImageFormatDetectorTests` |
| UI (labels, focus management/trap, error states, mobile layout/touch targets, multi-candidate selection, unknown-resolution blocking, duplicate dialogs) | `ImportPage.test.tsx`, `Modal.test.tsx` (shared focus trap/Escape/Tab), `e2e/import.spec.ts` (incl. mobile viewport and inert background). Import-form keyboard operation is not separately scripted beyond labelled controls; keyboard-only form flow is covered for recipes in `e2e/recipes.spec.ts`. |

## Evidence at completion

- Backend tests: **1615 passed** (all passing; import + SSRF battery included).
- Frontend Vitest: **220 passed**.
- Playwright (hermetic): **33 passed / 2 pre-existing stack skips**.
- Backend gates: `dotnet format --verify-no-changes` clean, Release build clean,
  `ef migrations has-pending-model-changes` → none.
- Independent review: M4-11, M4-12, and M4-13 were each reviewed separately.
  The M4-13 sweep found one blocker — a stale Playwright progress-summary
  assertion (`e2e/app.spec.ts`) left by the manifest completion — which was
  fixed and the full Playwright suite re-run green before tagging. All other
  findings were minor or nits, fixed or documented in this report.

## Known limitations (deferred, per `docs/M4_PLAN.md §6`)

- Package-size conversion, nutrition parsing, and advanced duplicate detection
  are tracked in `docs/FUTURE_FEATURES.md`.
- Source-URL canonicalization is deliberately narrow: an empty query, a trailing
  `&`, percent-encoding case, and repeated-key value order are not folded (these
  only ever cause a warning to be missed, never a false warning).
- Similar-name scoring scans a bounded, id-ordered window of 1000 recipes; a
  catalog beyond it could miss a warning for a newer, higher-id recipe.
- `source.page_title` enrichment remains deferred.
- Browser automation, JavaScript execution, and CAPTCHA handling are never used.

## Live deployment verification (Milestone completion gate)

Recorded retroactively after the M4/M5 milestone-completion gate was added
(`AGENTS.md`). Rebuilt and redeployed via `docker-admin` using
`scripts/deploy.sh up` (`docker compose up -d --build` on the NAS).

- Image `fate-and-fridge:0.1.0`: before `sha256:6d155f76…` (created 2026-10-03,
  pre-M4/M5) → after `sha256:20ba2a53…` (created 2026-10-06T18:00:10-05:00).
- Migrations applied on startup: `Applying migration '20261006022730_M5CalendarCore'`
  then `Database migrations applied`.
- Health: `{"status":"ok","app":"Fate & Fridge","version":"0.1.0","environment":"production"}`.
- Live route proof: `POST /api/v1/import/recipe-url` returns `400 VALIDATION_FAILED`
  (route present, not 404); the served bundle contains `URL Import`.
- Live `/progress`: **"6 of 15 milestones complete (40%)"**, with M4 — URL Import
  shown Complete.
- The household is in **single-user** mode (`authenticated: true`, system actor
  with all permissions), so requests without a session are authorized by design.
