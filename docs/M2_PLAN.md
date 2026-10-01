# Fate & Fridge — Milestone 2 Plan (Reference Data)

Status: **approved, ready to execute**
Approved: 2026-09-30 (product owner)
Purpose: this file is the authoritative runbook for M2. A fresh session must be able to
execute M2 end-to-end from this document plus `AGENTS.md` and `docs/MILESTONES.md`
without asking further product questions.

---

## 1. Locked decisions (do not re-litigate)

1. **Defaults seeding:** idempotent **startup seeder**. On startup, if
   `application_settings.reference_data_seeded` is absent, insert the seed data below
   (departments, units + aliases, proteins), then set the flag. Works for existing
   installs (e.g. the live NAS) and fresh databases. Seeder never overwrites user edits
   and never runs twice.
2. **Conversion factors are configurable:** `units.base_factor` is editable via
   `PATCH /api/v1/units/{id}` for **mass/volume** units only (admin UI: "Conversion
   factors"). Count/nonconvertible units have no factor. Seed values are the US
   customary defaults in §4 (also used by M8 grocery aggregation).
3. **Unit dimensions** (fixed at creation, immutable afterwards):
   - mass: `oz`, `lb`, `g`, `kg`
   - volume: `tsp`, `tbsp`, `fl oz`, `cup`, `pint`, `quart`, `gallon`
   - count: `each`, `package`, `can`, `bottle`, `jar`, `bunch`, `clove`, `slice`, `pinch`
     (same-unit sums only; never convert between count units)
   - nonconvertible/quantity-less: `to taste` (quantity may be null)
   - Changing a unit's dimension is rejected (`400`); admins retire and create a new unit.
4. **Normalization** for names and aliases: lowercase → trim → collapse internal
   whitespace → Unicode diacritic folding (`jalapeño` = `jalapeno`). **No automatic
   pluralization** (plurals are explicit aliases). This normalized form drives
   uniqueness and matching.
5. **Merge traceability:** `ingredients.merged_into_ingredient_id` (nullable self-FK,
   restrict) records the target on the retired source; M3+ can follow it.
6. **Retire/delete rules:**
   - Departments: `DELETE` only when no ingredient references them, else `409` offering
     retire. `POST /{id}/retire` sets `is_active=false`.
   - Units/proteins: retire when referenced; admin hard delete only when unused.
   - Tags: hard delete only when unused (no retire flag).
   - Retired entities remain resolvable for existing data but are excluded from
     autocomplete and new selection (unless `include_retired=true`).
7. **Audit pulled forward:** M2 creates `audit_events` (DATABASE §10) and a minimal
   `AuditService`; merge/retire/delete/factor changes write events with actor and
   before/after. The admin viewer stays in M12.
8. **Departments API:** full REST family plus `PUT /api/v1/departments/order`
   (complete permutation) — see `docs/API.md` §21.
9. **Autocomplete/conflicts:** `GET /{resource}?search=&limit=20&include_retired=false`
   on ingredients/units/proteins/tags; prefix matches before substring matches; aliases
   included; limit max 100. Inline-create conflicts return `409` with
   `details.existing_id` (`INGREDIENT_EXISTS`, `UNIT_EXISTS`, `PROTEIN_EXISTS`,
   `TAG_EXISTS`); alias conflicts return `409 ALIAS_EXISTS`.
10. **M2 includes the management UI:** a Reference Data section in the admin console
    (tabs: Departments, Units, Ingredients, Proteins, Tags) with list/create/rename/
    retire/delete/merge, department reordering, and conversion-factor editing.
    Autocomplete dropdowns for recipe entry land with M3.
11. **Ingredient usage seam:** `IReferenceUsageChecker` returns recipe usage counts
    (always `0` until M3 adds recipe tables); M2 tests inject fakes. M3 replaces it with
    the real query. Documented in the issues.
12. **Unit conversion service is built in M2** (pure, testable) so configurable factors
    are actually exercised; M8 grocery aggregation consumes it.

---

## 2. Seed data (authoritative)

Normalized uniqueness applies to names and aliases. Every listed alias is inserted on
seed.

### Departments (in order)
1. Produce
2. Meat & Seafood
3. Dairy & Eggs
4. Bakery
5. Deli
6. Frozen
7. Canned Goods
8. Dry Goods / Pantry
9. Spices & Seasonings
10. Condiments & Sauces
11. Beverages
12. Household
13. Personal Care
14. Other

### Units (symbol | display name | dimension | base_factor | aliases)
| symbol | display | dimension | factor (base: g / mL) | aliases |
|---|---|---|---|---|
| tsp | teaspoon | volume | 4.92892159375 | teaspoon, teaspoons, t |
| tbsp | tablespoon | volume | 14.78676478125 | tablespoon, tablespoons, Tbsp, T |
| fl oz | fluid ounce | volume | 29.5735295625 | floz, fl. oz., fluid ounce, fluid ounces |
| cup | cup | volume | 236.5882365 | cups |
| pint | pint | volume | 473.176473 | pints, pt |
| quart | quart | volume | 946.352946 | quarts, qt |
| gallon | gallon | volume | 3785.411784 | gallons, gal |
| oz | ounce | mass | 28.349523125 | ounce, ounces |
| lb | pound | mass | 453.59237 | pound, pounds, lbs |
| g | gram | mass | 1 | gram, grams |
| kg | kilogram | mass | 1000 | kilogram, kilograms |
| each | each | count | — | ea |
| package | package | count | — | packages, pkg |
| can | can | count | — | cans |
| bottle | bottle | count | — | bottles |
| jar | jar | count | — | jars |
| bunch | bunch | count | — | bunches |
| clove | clove | count | — | cloves |
| slice | slice | count | — | slices |
| pinch | pinch | count | — | pinches |
| to taste | to taste | nonconvertible | — | as needed |

### Proteins
Beef, Chicken, Pork, Turkey, Seafood, Lamb, Venison, Duck, Other.

---

## 3. Issue breakdown (execution order)

| # | Issue | Depends on | Deliverable |
|---|-------|-----------|-------------|
| M2-01 | Schema + audit baseline | — | `ingredients.merged_into_ingredient_id` (self-FK restrict), `audit_events` table + indexes, migration, constraint tests |
| M2-02 | Minimal audit service | M2-01 | `AuditService` writing actor/event/entity/before-after/reason/ip; unit + integration tests |
| M2-03 | Normalization utilities | — | `ReferenceDataNormalizer` (case/trim/collapse/diacritic fold) + adversarial unit matrix; used by every M2 write |
| M2-04 | Reference-data seeder | M2-01, M2-03 | Idempotent startup seeder + `reference_data_seeded` flag; seed data §2; backfill test on a pre-existing database; no-op on user edits |
| M2-05 | Departments API | M2-02, M2-03, M2-04 | GET/POST/PATCH, retire, delete-if-unused, `PUT /order`, audit, permissions, `DEPARTMENT_IN_USE` |
| M2-06 | Units API + aliases | M2-02, M2-03, M2-04 | GET(search)/POST/PATCH (name; factor for mass/volume with validation + audit), dimension immutable, retire, delete-if-unused, alias add/remove, conflict codes |
| M2-07 | Unit conversion service | M2-06 | Pure `UnitConversionService` (families, configurable factors, same-unit count rule, null-quantity units) + exhaustive tests; M8 consumer |
| M2-08 | Ingredients API + aliases | M2-02, M2-03, M2-05 | GET(search incl. aliases)/POST/PATCH, alias add/remove, retire, delete-if-unused (seam), `INGREDIENT_EXISTS`, `ALIAS_EXISTS` |
| M2-09 | Ingredient merge | M2-08 | `POST /ingredients/merge`: transaction, refs via seam, copy name/aliases (skip conflicts, report), retire + `merged_into`, audit before/after |
| M2-10 | Proteins + tags APIs | M2-02, M2-03 | proteins: GET(search)/POST/PATCH/retire/delete-if-unused; tags: GET(search)/POST/PATCH/delete-if-unused; conflicts; audit |
| M2-11 | Reference-data admin UI | M2-05…M2-10 | Settings → Reference Data tabs (departments/units/ingredients/proteins/tags), CRUD, merge, reorder, factor editor; Vitest + hermetic Playwright |
| M2-12 | M2 verification sweep | M2-01…M2-11 | Reviewer battery (below), progress manifest → M2 complete (evidence), `m2-complete` tag, docs polish |

## 4. M2 verification battery (M2-12)

- Seeding backfill: an M1-era database (no reference data) receives defaults exactly once;
  a database with user edits is untouched.
- Normalization adversarial matrix (diacritics, whitespace, case, punctuation, long strings).
- Conversion math: seeded factors, overridden factors, same-unit counts, incompatible
  families stay separate, null-quantity units.
- Merge: references reassigned (fake checker), aliases copied with conflicts skipped and
  reported, source retired with `merged_into`, audit before/after, transaction rollback.
- Delete safety: in-use → `409` with usage details; unused → deletes; retire paths.
- Permissions matrix on every M2 route (admin / member with `reference_data.edit` /
  member without / unauthenticated / single-user).
- Audit rows for every write (actor, before/after, no secrets).
- Autocomplete: prefix-before-substring ordering, aliases, retired exclusion,
  `include_retired`, limit clamp.
- UI: keyboard/a11y, mobile layout, factor editor validation.
- Optional: redeploy to TrueNAS and repeat the reference-data happy path (docker-admin).

---

## 5. Automation protocol (for the executing session)

1. **Read first:** `AGENTS.md`, `docs/M2_PLAN.md` (this file), `docs/MILESTONES.md` M2,
   the issue you are implementing. Decisions in §1 are final; if something contradicts
   them, stop and report instead of improvising.
2. **One issue at a time**, in the order above. Branch `feature/<issue>-m2-xx-slug` from
   `main`. Implementer follows AGENTS.md (Roslyn MCP mandatory for C#; frontend uses the
   local-disk mirror for Vitest/Playwright because `/mnt/y` is a 9p share).
3. **Gates before review:** backend `dotnet format --verify-no-changes`,
   `dotnet build --configuration Release`, `dotnet test --configuration Release`,
   `dotnet ef migrations has-pending-model-changes` (with
   `FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`); frontend `format:check`,
   `lint`, `typecheck`, `test`, `build`, `npx playwright test`.
4. **Independent review** by the reviewer subagent; fix findings on the same branch and
   re-verify; then fast-forward merge to `main`, delete the branch, close the issue with
   a summary, and **push** (`git push origin main`) so checkpoints stay remote.
5. **Escalate to the product owner only for:** a contradiction with §1, a new schema
   change not listed in M2-01, a security blocker the reviewer and implementer disagree
   on, or a real-stack action needing Docker (delegate to docker-admin).
6. **Milestone lifecycle:** when M2 starts, set M2 `in_progress` in
   `frontend/src/features/progress/milestones.ts`. When M2-12 passes, set M2 `complete`
   with evidence (test counts, dates, issue references), update `progressSummary`,
   commit/push, tag `m2-complete`, and close the M2 tracking issue.
7. **Definition of Done:** per `AGENTS.md`, including the progress-manifest update and
   docs updates (`docs/API.md`, `docs/DATABASE.md`, `CHANGELOG.md` as applicable).
