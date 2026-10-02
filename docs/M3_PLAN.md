# Fate & Fridge — Milestone 3 Plan (Recipes)

Status: **approved, ready to execute**
Approved: 2026-10-01 (product owner)
Purpose: this is the authoritative M3 runbook. A fresh session should execute M3
from this file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product,
API, database, architecture, security, and testing specifications without asking
further product questions.

---

## 1. Locked product decisions

1. M3 delivers the complete recipe REST API and usable recipe management/browser UI.
2. Recipe names use `ReferenceDataNormalizer`; `normalized_name` is globally unique.
   Responses preserve the display form.
3. New recipes default to `status=normal`, `is_favorite=false`,
   `is_vegan=false`, and `is_vegetarian=false`.
4. Status transitions support `normal`, `manual_only`, and `retired` in both directions
   where the user has `recipes.edit`. Retired recipes remain browsable and resolvable
   for existing data but are excluded from randomization and new reference selection.
5. Source site, source URL, and author are optional editable recipe fields in M3.
   M3 stores them but never fetches a URL. M4 owns import and duplicate-import warnings.
6. M3 supports one active local recipe image: secure upload, replacement, removal,
   display, and alt text. Remote image downloading is M4 work. Image history is not kept.
7. Image policy: JPEG, PNG, and WebP only; inspect file signatures; maximum 5 MB;
   maximum 2048 pixels per dimension; reject SVG, HTML, animated formats, and unsafe
   content; random safe filenames under persistent `/data/images` storage.
8. Recipe quantities are numeric decimal and nullable. Units are optional. Preparation
   notes and optional ingredients are supported. Quantity-less values such as `to taste`
   are valid and are carried through without conversion math.
9. Ingredient groups are optional and ordered. Ungrouped ingredients are allowed.
   Groups may be titled or untitled; ingredients have an order within the recipe/group.
10. Instruction sections are optional and ordered. Sections may be titled or untitled;
    steps are ordered and editable. A recipe may have no instructions.
11. A recipe may have many active proteins and many active tags. Duplicate links are
    prevented. Retired references remain displayable on existing recipes but cannot be
    newly selected.
12. Recipe reads require `recipes.view`; recipe writes require `recipes.edit`.
    Admin always has both; the single-user system actor is treated as Admin.
13. M3 normal UI supports retirement/status changes only. Hard recipe deletion remains
    the M12 Admin-only destructive workflow with historical snapshots/tombstones.
14. Browser experience: default photo card grid, compact list toggle, search, filters,
    and sorts from PRODUCT_SPEC §27. Default sort is recently added, newest first, with
    stable id tie-breaking.
15. Basic statistics in M3 are explicit empty states until calendar/history data exists;
    do not show fake zeros. Real values arrive with later calendar/statistics milestones.
16. Recipe lists use `limit` + `offset`: default limit 24, maximum 100, response shape
    `{ items, total, limit, offset }`, stable sorting with id tie-breaking.
17. Recipe text limits: name maximum 200 characters; description, notes, instruction
    sections, and individual steps maximum 20,000 characters. Empty optional long text
    is stored as null/absent according to the API shape.
18. Prep and cook times are nullable, whole, nonnegative minutes, maximum 1,440.
19. Manually entered source URLs are optional absolute `http`/`https` URLs only. M3 does
    not request them; SSRF protections apply to the M4 fetcher.
20. Recipe editing selects existing active ingredients, units, proteins, and tags through
    autocomplete/reference queries. Inline creation and free-text fallback are not M3.

---

## 2. M3 schema and API decisions

M3 owns one additive EF migration for the recipe model and join tables. No M2 tables
are changed except where a real FK/index addition is required and explicitly reviewed.
The recipe model includes:

- `recipes`: id, name, normalized_name, description, source_site, source_url,
  normalized_source_url, author, prep_minutes, cook_minutes, notes, is_favorite,
  status, is_vegan, is_vegetarian, created_at, updated_at.
- `recipe_images`: one active image per recipe, storage_path, source_url nullable,
  alt_text, created_at. The active image is replaced/removed atomically.
- `recipe_ingredient_groups`: recipe_id, nullable title, sort_order.
- `recipe_ingredients`: recipe_id, nullable group_id, ingredient_id, nullable decimal
  quantity, nullable unit_id, nullable preparation_note, is_optional, sort_order.
- `recipe_instruction_sections`: recipe_id, nullable title, sort_order.
- `recipe_instruction_steps`: section_id, text, sort_order.
- `recipe_proteins` and `recipe_tags`: unique recipe/reference pairs.

API families:

- `GET /api/v1/recipes` with `search`, status, favorite, vegan/vegetarian, protein,
  tag, last-eaten placeholder, sort, limit, and offset.
- `POST /api/v1/recipes`, `GET /api/v1/recipes/{id}`, and `PATCH /api/v1/recipes/{id}`.
- Nested or equivalent atomic endpoints for ingredients/groups/instructions,
  preserving complete recipe edit transactions. The final route shapes must be recorded
  in `docs/API.md` before implementation is called complete.
- Image upload/replacement/removal endpoints with strict content validation and safe
  persistent storage.
- Recipe retirement/status and favorite/metadata edits through `recipes.edit`.

Every write is server-authorized and audited where it changes persistent recipe data.
No ordinary endpoint hard-deletes recipes. API errors use the existing error envelope.

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable |
|---|---|---|
| M3-01 | Recipe schema baseline | Recipe tables, joins, enum/text converters, one additive migration, model/constraint tests |
| M3-02 | Recipe status and lifecycle | Normal/manual-only/retired validation, transitions, retirement, authorization, audit |
| M3-03 | Favorite and dietary metadata | Favorite, vegan, vegetarian fields and API/UI behavior |
| M3-04 | Recipe images | Secure local upload, replacement/removal, alt text, persistent storage, security tests |
| M3-05 | Ingredient groups | Optional ordered groups and atomic reorder/edit behavior |
| M3-06 | Recipe ingredients | Decimal nullable quantities, units, preparation notes, optional flag, ordered editing |
| M3-07 | Instruction sections and steps | Optional ordered sections and ordered steps, validation and editing |
| M3-08 | Protein and tag associations | Many-to-many links, active-selection rules, retired-reference display |
| M3-09 | Recipe API and edit workflow | Complete authorized recipe CRUD and atomic nested editing contracts; docs/API.md update |
| M3-10 | Recipe management UI | Accessible create/edit form, nested ingredients/instructions, image workflow |
| M3-11 | Recipe browser | Photo cards, compact list, search, filters, sorts, empty statistics states |
| M3-12 | M3 verification sweep | Independent battery, docs polish, manifest complete, tag `m3-complete` |

One issue at a time, in order. Each issue gets a branch from current `main`, implementer
work, gates, independent reviewer, fixes on the same branch, fast-forward merge, issue
closure, and push. M3-10/M3-11 may share a UI branch only if the implementer reports the
split clearly; no unrelated feature expansion is permitted.

---

## 4. M3 verification battery

- Migration applies to empty and M2 databases; no pending model changes.
- Name normalization matrix, duplicate protection, long-field and control-character
  validation, status transitions, and timestamp behavior.
- Full permission matrix on every recipe route: Admin, member with `recipes.edit`,
  member without it, unauthenticated, and single-user.
- Recipe create/edit rollback: a nested ingredient, image, group, or instruction failure
  leaves no partial recipe or audit rows.
- Decimal nullable quantities, nullable units, `to taste`, optional ingredients, group
  and step ordering, and duplicate association prevention.
- Retired reference behavior: existing recipe displays remain valid; new selections omit
  retired ingredients/units/proteins/tags.
- Image security: signatures, MIME/content mismatch, SVG/HTML rejection, size and
  dimension limits, random paths, traversal resistance, replacement/removal atomicity.
- API list behavior: filters, stable sort, limit/offset, total count, empty statistics
  state, and no unbounded client-only filtering.
- UI: keyboard operation, focus traps, labels, mobile layout, touch targets, text-based
  statuses, image error states, and responsive card/list views.
- No ordinary hard-delete route; historical deletion remains deferred to M12.

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, M3 sections of `docs/MILESTONES.md`, and the issue before
   each issue. Decisions in §1 are final; contradictions stop execution.
2. Use Roslyn MCP first for every C# inspection/edit/diagnostic. Pass the `.csproj`
   directly; backend Roslyn paths resolve from `/mnt/y/backend`. Never use
   `String.Normalize`; this workstation runs invariant globalization.
3. Frontend gates run through `/tmp/opencode/fe-mirror.sh` because `/mnt/y` is a 9p share.
   Browser behavior is delegated to the reviewer/implementer; Playwright is required for
   M3 UI issues and the final sweep.
4. Backend gates: `dotnet format --verify-no-changes`, Release build, Release tests,
   and `dotnet ef migrations has-pending-model-changes` with
   `FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`. Frontend gates: format check,
   lint, typecheck, test, build, and `npx playwright test` from the local mirror.
5. Reviewer must independently execute the relevant tests and review authorization,
   historical integrity, image security, nested transactionality, date/time behavior,
   and mobile/accessibility behavior.
6. At M3 start, set M3 `in_progress` in `frontend/src/features/progress/milestones.ts`.
   At M3-12, set M3 `complete` with evidence, update `progressSummary`, update
   `CHANGELOG.md` and `docs/API.md`/`docs/DATABASE.md` as applicable, push `main`, tag
   `m3-complete`, and close the M3 tracking issue.

---

## 6. Explicitly deferred from M3

- URL fetching/import/parsing, SSRF handling, duplicate-import warnings, and image
  downloading: M4.
- Calendar history, last-eaten/next-planned/times-eaten calculations: M5/M11.
- Randomizer consumption of recipe status/proteins/favorites: M6.
- Grocery aggregation consumption of recipe ingredients/conversion: M8.
- Live sync: M9.
- Recipe hard deletion, historical correction, and full audit viewer: M12.
