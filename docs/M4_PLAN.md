# Fate & Fridge — Milestone 4 Plan (URL Import)

Status: **approved, ready to execute**
Approved: 2026-10-03 (product owner)
Purpose: this is the authoritative M4 runbook. A fresh session should execute M4
from this file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product,
API, database, architecture, security, and testing specifications without asking
further product questions.

---

## 1. Locked product decisions

1. M4 delivers recipe import from a user-pasted URL: SSRF-hardened fetching,
   schema.org Recipe JSON-LD first, best-effort HTML fallback, deterministic
   normalization, ingredient/unit matching, image download, a mandatory review
   screen, duplicate warnings, and import security tests.
2. Import is **stateless and draft-only**. `POST /import/recipe-url` performs the
   fetch and returns a draft. No recipe, ingredient, unit, tag, protein, image,
   or audit row is written before the user reviews and saves. Saving reuses the
   existing `POST /recipes` contract (docs/API.md §23).
3. Import requires `recipes.edit`. Reads of import-only helper routes require
   `recipes.view` or `recipes.edit`. Creating a missing canonical ingredient or
   custom unit during review requires the separate `reference_data.edit` key and
   is enforced by the existing M2 endpoints; the UI hides create affordances the
   actor cannot perform.
4. Parsers never invent uncertain data. A value that cannot be parsed
   unambiguously is carried through as raw text rather than guessed.
5. Parsing order: JSON-LD `Recipe` (including `@graph`) first; if no JSON-LD
   `Recipe` exists, the HTML fallback (microdata first, then headings/lists).
   Both are best-effort and totally guarded against malformed input.
6. Multi-recipe pages return **all** parsed candidates and let the user pick one.
   A page with zero parseable recipes is `422 IMPORT_NO_RECIPE`.
7. All imported text (name, description, author, instructions, ingredient lines,
   notes) is treated as untrusted plain text. Markup is stripped, not rendered;
   control characters (except tab/newline) are rejected or removed; length
   limits match M3 (`name` 200; long text 20,000; preparation note 300).
8. Images are **downloaded after review**, not during parsing: the draft carries
   a resolved `image_source_url` for optional client-side preview, and after the
   recipe is saved the client calls `POST /recipes/{id}/image/from-url`, which
   re-validates the URL through the same SSRF guard and the M3 image content
   policy, then stores it locally. A failed image download never loses the saved
   recipe; it is a non-fatal, surfaceable warning.
9. Duplicate detection is two-tiered and deterministic:
   - **Exact source URL**: compare `ReferenceDataNormalizer`-folded absolute
     `source_url` against `recipes.normalized_source_url`. A match is a strong
     warning offering Open Existing / Import Anyway / Cancel.
   - **Similar name**: folded-name equality (exact is the existing
     `RECIPE_EXISTS` 409), word-boundary containment, shared-token overlap, or a
     small edit distance on the folded forms. Warn and allow rename. Advanced
     duplicate detection stays deferred (PRODUCT_SPEC §29).
10. Ingredient matching is explicit: canonical name (folded) exact, then alias
    (folded) exact, then suggestion list (prefix/substring, max 5). A line with
    no confident match is flagged `is_unknown`; the review screen maps it to an
    existing ingredient or creates one (when permitted). No free-text ingredient
    id is ever sent to `POST /recipes`.
11. Unit normalization maps the extracted unit text against unit symbols and
    aliases (folded). Unknown units are flagged with a suggested dimension when
    obvious (mass vs volume vs count) and otherwise require the user to choose.
    Package-size conversion is never guessed (AGENTS.md Grocery Rules).
12. Quantity parsing accepts integers, decimals, unicode vulgar fractions,
    `a/b`, and mixed `n a/b`. Ranges (`1-2`, `1 to 2`), multipliers (`2x`), and
    word quantities (`a pinch`, `some`) stay raw: `quantity` is `null` and the
    original token is preserved in the ingredient line or preparation note.
13. Fetch limits are enforced on every outbound request: HTTP/HTTPS only,
    connection timeout 5 s, total read timeout 15 s, max 5 redirects (each
    re-validated), max 2 MiB HTML, max 5 MiB image, allowed content types, an
    identifying user agent, and a per-import fetch budget (1 page + 1 image).
14. SSRF defenses: block loopback, RFC1918, link-local, ULA, CGNAT, multicast,
    reserved, and cloud-metadata ranges for IPv4 and IPv6; block `localhost`,
    `.local`, `.internal`, and common container hostnames; resolve DNS before
    connecting and connect to the validated address to defeat rebinding; never
    send credentials or cookies; follow redirects manually and re-validate each
    hop.
15. Per-user import throttling protects the outbound surface (default 30 page
    fetches/hour and 60 image fetches/hour); excess returns `429
    IMPORT_RATE_LIMITED`. Loopback and the single-user actor are still
    throttled.
16. The import UI is a review screen reachable from the recipe browser ("Import
    from URL"): paste URL → parse → pick candidate when multiple → edit every
    field with the existing recipe form controls → resolve unknown
    ingredients/units → save. Cancel always discards the draft.
17. No third-party JavaScript is executed and no browser automation is used to
    parse recipes (SECURITY §7). The HTML parser is a static DOM parse.
18. M4 adds no database tables. It reuses `recipes`, `recipe_images`,
    `ingredients`, `units`, and the M3 write paths. No migration is expected; if
    an implementer believes one is required, stop and escalate to the Lead.

---

## 2. M4 architecture and contracts

### 2.1 Backend layout (`src/FateAndFridge.Api/Imports/`)

Per `docs/ARCHITECTURE.md` §5/§6, import logic is isolated behind an import
subsystem:

- `SafeUrlFetcher` / `IImportFetcher` — validated DNS + connect, manual
  redirects, size/time/content limits, structured failure reasons.
- `ImportUrlPolicy` — scheme/host/IP classification shared by the fetcher and
  the image endpoint (pure, heavily unit-tested).
- `StructuredRecipeParser` — JSON-LD extraction.
- `HtmlFallbackParser` — static HTML/microdata fallback.
- `IngredientLineParser` — quantity/fraction/unit/name/note split.
- `ImportNormalizer` — whitespace, fraction, unit-alias, and text sanitization
  helpers built on `ReferenceDataNormalizer` (explicit table-driven logic; never
  `String.Normalize`).
- `IngredientMatcher` — canonical/alias matching and suggestions.
- `RecipeImportService` — orchestrates fetch → parse → normalize → match →
  draft, and duplicate diagnostics.

HTML parsing uses **AngleSharp** (static DOM, no script execution). If the
package cannot be restored, stop and escalate rather than hand-rolling a
permissive parser.

### 2.2 Routes (final shapes recorded in `docs/API.md` §15 before completion)

- `POST /api/v1/import/recipe-url` — body `{ "url": "https://…" }`;
  `recipes.edit`. Returns:
  ```json
  {
    "source": {
      "url": "https://example.com/recipe",
      "site": "example.com",
      "page_title": "Best Lasagna",
      "author": "Jane Cook",
      "fetched_at": "2026-10-03T…Z",
      "parse_method": "json_ld"
    },
    "source_url_match": { "id": 5, "name": "Lasagna" },
    "capabilities": { "can_create_reference_data": false },
    "recipes": [
      {
        "index": 0,
        "name": "Lasagna",
        "description": null,
        "author": "Jane Cook",
        "source_url": "https://example.com/recipe",
        "source_site": "example.com",
        "prep_minutes": 20,
        "cook_minutes": 45,
        "notes": null,
        "image_source_url": "https://example.com/lasagna.jpg",
        "name_matches": [{ "id": 6, "name": "Lasagna Bolognese" }],
        "ingredients": [
          {
            "raw": "1 1/2 cups all-purpose flour, sifted",
            "quantity": 1.5,
            "name": "all-purpose flour",
            "preparation_note": "sifted",
            "matched_ingredient_id": 12,
            "match_kind": "exact",
            "is_unknown": false,
            "suggestions": [{ "id": 12, "name": "Flour" }],
            "unit": {
              "raw": "cups",
              "matched_unit_id": 3,
              "matched_symbol": "cup",
              "is_unknown": false,
              "suggested_dimension": null,
              "suggestions": [{ "id": 3, "symbol": "cup", "display_name": "cup" }]
            }
          }
        ],
        "instruction_sections": [
          { "title": null, "steps": ["Boil the noodles."] }
        ],
        "proteins": [],
        "tags": [],
        "warnings": ["ingredient_unknown"]
      }
    ]
  }
  ```
  `recipes` always has ≥ 1 element or the request fails. `parse_method` is
  `json_ld` or `html`. `capabilities.can_create_reference_data` reflects the
  actor so the UI can hide inline create.
- `POST /api/v1/import/check` — body `{ "source_url"?: string, "name"?: string }`;
  `recipes.view`. Returns `{ "source_url_match": {…}|null, "name_matches": [ … ] }`
  for re-checking after the user renames in review.
- `POST /api/v1/recipes/{id}/image/from-url` — body `{ "source_url": "https://…" }`;
  `recipes.edit`. Safe-fetches, validates JPEG/PNG/WebP content, enforces the M3
  size/dimension policy, replaces the active image, audits, returns the M3
  image document. Errors: `IMPORT_URL_BLOCKED`, `IMPORT_FETCH_FAILED`,
  `IMPORT_UNSUPPORTED_CONTENT`, `IMPORT_TOO_LARGE`.

All errors use the shared envelope. New stable codes: `IMPORT_URL_INVALID`,
`IMPORT_URL_BLOCKED`, `IMPORT_FETCH_FAILED`, `IMPORT_UNSUPPORTED_CONTENT`,
`IMPORT_TOO_LARGE`, `IMPORT_NO_RECIPE`, `IMPORT_RATE_LIMITED`.

### 2.3 Frontend

- `src/features/import/` — `ImportPage.tsx` (paste + parse), `ImportReview.tsx`
  (candidate picker + editable draft), `useImport.ts` (TanStack Query mutation),
  `importTypes.ts`.
- Route `recipes/import` under the authenticated shell; entry point button on
  the recipe browser and/or the "new recipe" affordance.
- The review form mirrors `RecipeFormPage` field controls and reuses
  `quantity.ts` fraction display, `ReferenceCheckboxes`, and autocomplete hooks.
- Unknown ingredient/unit resolution is inline with explicit, labelled actions.
  Create actions are permission-gated in the UI and server-enforced by M2.

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable | Depends on |
|---|-------|-------------|-----------|
| M4-01 | Safe URL fetcher | `SafeUrlFetcher` + `ImportUrlPolicy`, DNS/connect validation, redirects, timeouts/size/content limits, structured failures, unit + integration tests | — |
| M4-02 | JSON-LD Recipe parser | `StructuredRecipeParser` for schema.org Recipe including `@graph`/arrays/HowTo, sanitization, unit tests | M4-01 |
| M4-03 | HTML fallback parser | `HtmlFallbackParser` (AngleSharp, microdata + heuristics), unit tests | M4-02 |
| M4-04 | Import draft contract + endpoint | `RecipeImportService`, `POST /import/recipe-url` returning all candidates, `POST /import/check`, permission gate, throttling, API/docs/tests | M4-03 |
| M4-05 | Fraction/unit normalization | `IngredientLineParser` + `ImportNormalizer`; deterministic quantities/units/notes; tests | M4-04 |
| M4-06 | Ingredient matcher | `IngredientMatcher` canonical/alias exact + suggestions; draft wiring; tests | M4-05 |
| M4-07 | Image downloader | `POST /recipes/{id}/image/from-url` with SSRF + M3 image policy, audit, tests | M4-06 |
| M4-08 | Import review screen | Frontend paste → pick candidate → edit → save flow; route + entry; Vitest | M4-07 |
| M4-09 | Unknown ingredient flow | Draft/UI mapping or inline creation (permission-gated), required-before-save validation; tests | M4-08 |
| M4-10 | Unknown custom unit flow | Draft/UI unit resolution or creation with explicit dimension; tests | M4-09 |
| M4-11 | Duplicate source URL warning | Exact normalized-URL diagnostics + Open Existing/Import Anyway/Cancel UI; tests | M4-10 |
| M4-12 | Similar-name warning | Similar-name diagnostics on import + rename recheck; warn/rename UI; tests | M4-11 |
| M4-13 | Import security tests + M4 verification | SSRF/redirect/rebinding/content-type/size/malformed-markup battery, docs, manifest, tag `m4-complete` | M4-12 |

One issue at a time, in order. Each issue gets a branch from current `main`,
implementer work, gates, independent reviewer, fixes on the same branch,
fast-forward merge, issue closure, and push. No unrelated feature expansion is
permitted.

---

## 4. M4 verification battery

- URL policy matrix: schemes, credentials in URL, IP literals, IPv4/IPv6
  loopback/private/link-local/ULA/CGNAT/multicast/reserved, metadata addresses,
  `localhost`/`.local`/`.internal`, decimal/octal/hex IP obfuscation, userinfo
  tricks, trailing dots.
- Redirect behavior: max hops, cross-host redirect re-validation, redirect to a
  blocked address, redirect loop, relative/absolute Location.
- DNS rebinding: resolver returning public then private addresses; connection
  must use the validated address.
- Fetch limits: connection/read timeouts, HTML and image size ceilings, content
  type enforcement, missing/lying `Content-Type`, chunked/no-`Content-Length`.
- Parser robustness: malformed/truncated JSON-LD, arrays, `@graph`, multiple
  `Recipe` nodes, HTML entity encodings, huge attribute values, deeply nested
  markup, no-recipe pages, microdata-only pages.
- Normalization table: vulgar fractions, mixed numbers, ranges retained as raw,
  word quantities, unit aliases/symbols/case, prep notes after commas,
  parenthetical sizes (`1 (14 oz) can`).
- Matching: canonical exact, alias exact, suggestions bounded to 5, retired
  references never suggested, ambiguity flagged as unknown.
- Draft integrity: no database writes before save, no audit rows, no image files
  created during parse; save via `POST /recipes` plus image-from-URL is atomic
  per endpoint.
- Duplicate checks: normalized URL equality (trailing slash, query order,
  fragment, case, default port), similar-name scoring, rename recheck.
- Permission matrix on every import route: Admin, `recipes.edit`, `recipes.view`
  only, `reference_data.edit` only, unauthenticated, single-user. Reference-data
  creation during review still requires `reference_data.edit`.
- Throttling: per-user page/image limits, 429 shape, recovery after window.
- Untrusted content: markup stripped, control characters rejected, length limits,
  no HTML/JS/svg/data URLs rendered.
- Image download: signature/content mismatch rejection, SVG/HTML rejection, size
  and dimension limits, replacement atomicity, traversal resistance.
- UI: keyboard operation, focus management, labels, error states, mobile layout,
  touch targets, multi-candidate selection, unknown-resolution blocking,
  duplicate dialogs.

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, the M4 sections of `docs/MILESTONES.md`, and the
   issue before each issue. Decisions in §1 are final; contradictions stop
   execution and escalate to the Lead.
2. Use Roslyn MCP first for every C# inspection/edit/diagnostic. Pass the
   `.csproj` directly; backend Roslyn paths resolve from `/mnt/y/backend`. Never
   use `String.Normalize`; this workstation runs invariant globalization, so all
   folding/casing logic must be explicit and table-driven.
3. Frontend gates run through `/tmp/opencode/fe-mirror.sh` because `/mnt/y` is a
   9p share. Browser behavior is delegated to the reviewer/implementer;
   Playwright is required for M4 UI issues and the final sweep.
4. Backend gates: `dotnet format --verify-no-changes`, Release build, Release
   tests, and `dotnet ef migrations has-pending-model-changes` with
   `FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`. Frontend gates: format
   check, lint, typecheck, test, build, and `npx playwright test` from the local
   mirror.
5. Reviewer must independently execute the relevant tests and review SSRF
   handling, redirect/rebinding defenses, untrusted-content sanitization,
   permission enforcement, draft-only guarantees, and mobile/accessibility
   behavior. Tests must not hit the public internet; use a local loopback test
   server plus the blocked-address matrix, or an injectable fetch seam.
6. At M4 start, set M4 `in_progress` in
   `frontend/src/features/progress/milestones.ts`. At M4-13, set M4 `complete`
   with evidence, update `progressSummary`, update `CHANGELOG.md` and
   `docs/API.md` (and `docs/DATABASE.md` only if a schema fact changed), push
   `main`, tag `m4-complete`, and close the M4 tracking issue.

---

## 6. Explicitly deferred from M4

- Calendar planning, randomization, and grocery consumption of imported data:
  M5–M8.
- Hard recipe deletion, historical correction, and audit viewer: M12.
- Encrypted backup/restore of imported images: M13.
- Browser automation, JS execution, headless rendering, and CAPTCHA handling:
  never in V1.
- Package-size conversion, nutrition parsing, and advanced duplicate detection:
  V2 backlog.
