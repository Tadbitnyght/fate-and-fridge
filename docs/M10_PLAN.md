# Fate & Fridge — Milestone 10 Plan (Printing)

Status: **approved, ready to execute**
Approved: 2026-10-13 (product owner answered the full M10 decision set)
Purpose: authoritative M10 runbook. A fresh session should execute M10 from this
file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product, API,
architecture, database, security, and testing specifications without asking
further product questions. Where this file and an older spec disagree, escalate
to the Lead before implementing.

---

## 1. Locked product decisions

1. **Scope.** M10 completes V1 printing per `docs/PRODUCT_SPEC.md` §26:
   **landscape monthly calendar printing with multi-month page handling and
   long-name wrapping**, plus verification and cross-app polish of the grocery
   print view. **No new infrastructure, no new API, no schema change.**
2. **Grocery print is already delivered.** M8-08 shipped the print-optimized
   grocery view (department order, paper checkbox, per-meal attribution, manual
   items, include-checked default off, black-on-white). The MILESTONES M10 items
   "grocery print attribution" and "include-checked option" are therefore
   **delivered**; M10 only **verifies** them and applies the cross-app print-CSS
   polish. **No rework of the grocery print view.**
3. **Calendar print is a dedicated, deep-linkable view** at the client route
   **`/calendar/print`** (mirroring `/groceries/print`), with a screen-only
   controls bar and a Print button. The calendar toolbar's Print button
   **navigates to it**; the grocery Print entry is unchanged. Both print views
   carry a Back link.
4. **Multi-month.** The print view selects a **start month** and a **month count
   of 1–12**, defaulting to the month currently being viewed, and prints **one
   month per page** in chronological order. A print job crossing a year boundary
   is supported.
5. **Overflow vs one month per page.** Long names **wrap fully** (never
   ellipsis-clipped in print). A month that would exceed one page
   **auto-shrinks down to the ≈10pt legibility floor**; if it still overflows at
   that floor, the month **continues onto a following page** rather than
   shrinking further. Output is never unreadable and never clipped.
6. **Page size is owned per print view.** Calendar = **landscape**, grocery =
   **portrait**, via **named `@page` rules** applied by the respective print
   views. The app-wide `@page { size: landscape }` (M5) and the global
   `@page { margin: 12mm }` (M8) are **removed** so that printing any other page
   is unaffected. Margins are set per named page. (This closes the finding the
   M8 reviewer flagged for M10.)
7. **Printed calendar content.** Per `docs/PRODUCT_SPEC.md` §26, each day cell
   prints the **day number** and the **recipe name or special-event text**;
   **meal photos and lock icons are excluded**. In addition:
   - the curated **special-event icons** (M5's inline-SVG set) print next to the
     holiday / misc / host-family-dinner / go-to-family-dinner text, rendered in
     black (printer-safe, no colour dependence);
   - **eat out** and **fend for yourself** print as **text** (they carry no
     recipe);
   - **notes, diner count, and restaurant/location text are NOT printed**.
8. **Per-page heading.** Each printed page carries a **month/year heading** (e.g.
   "October 2026") — required so a multi-month job's pages are identifiable.
   The **household name is not printed**.
9. **Colour and legibility.** Print output is **black on white**, with **no
   background fills** and **no colour-only meaning**, and text is **≥≈10pt
   equivalent** (the auto-shrink floor). It must stay legible in greyscale. This
   applies to both the calendar and the grocery list.
10. **Grocery print styling stays black-on-white.** `PRODUCT_SPEC`'s "printed
    list uses normal styling" is read as "use the normal badge/typography
    treatment, not colour" — the manual badge is already icon+text. No colour is
    added to the printed grocery list.
11. **Past months are printable** (any month, including past ones). Historical
    data is read-only, so printing it is safe and useful for records.
12. **No new API.** Printing stays **client-rendered**: the print view reuses the
    existing calendar reads (one request per printed month if the read is
    month-scoped) and the existing grocery list read. No bulk/print endpoint.
13. **Print CSS testing bar.** Automated proof via Playwright
    `emulateMedia({ media: 'print' })` computed-style assertions **and**
    `page.pdf({ preferCSSPageSize: true })` assertions on **page size**
    (calendar landscape, grocery portrait) and **page count** (one month per
    page), plus Vitest component tests for the print views.
14. **Where print tests run.** The print assertions run in the **default hermetic
    Playwright suite** (no stack required), and the M10-10 gate adds a
    **real-stack print check** on the deployed instance (PDF/screenshot evidence).
15. **Explicitly deferred (V2 / backlog).** List-style calendar print, weekly
    print, recipe packet printing, custom grocery print templates, and
    **explicit PDF export/download** (the browser's Print → "Save as PDF" is the
    V1 answer). Also not printed: notes, diner count, restaurant/location.
16. **Verification.** The final issue mirrors the M4–M9 sweep: the §4 battery, an
    independent review, docs, the manifest M10 `complete` with evidence, tag
    `m10-complete`, and closing the tracking issue.

---

## 2. M10 architecture and contracts

### 2.1 Frontend layout (`frontend/src/`)

- `features/calendar/CalendarPrintPage.tsx` — the `/calendar/print` view:
  screen-only controls (start month, month count 1–12, Print, Back), one
  `CalendarPrintMonth` per month in chronological order, each with a month/year
  heading and the §1.7 content rules.
- `features/calendar/CalendarPrintMonth.tsx` — a single printable month grid
  (reusing the calendar's day-cell rendering semantics where practical, without
  the interactive affordances).
- `features/calendar/printRange.ts` — pure helpers: resolve the start month and
  count into an ordered list of `YYYY-MM` months (incl. year boundaries), and the
  per-month read parameters.
- `features/calendar/calendarPrintFormat.ts` — pure helpers for the printed cell
  text (recipe/event text, eat-out/fend as text, icon key selection) and the
  month/year heading.
- `App.tsx` — register `calendar/print` inside the session-guarded shell.
- `index.css` — named `@page` rules (`calendar-print` landscape, `grocery-list`
  portrait) and the print blocks; **remove** the app-wide
  `@page { size: landscape }` and the global `@page { margin: 12mm }`.
- `features/calendar/CalendarPage.tsx` — the toolbar Print button navigates to
  `/calendar/print`.

### 2.2 Print CSS contract

- `@page calendar-print { size: landscape; margin: … }` applied via
  `.calendar-print-page { page: calendar-print; }`.
- `@page grocery-list { size: portrait; margin: … }` applied via
  `.grocery-print-page { page: grocery-list; }` (already present; re-verify after
  removing the global rules).
- Each printed month is a block with `break-after: page` (except the last) so one
  month lands on one page; day cells use `break-inside: avoid`.
- Chrome (`.app-header`, `.app-nav`, `.app-footer`, skip link, and the print
  controls) is hidden in print; text is black on white with no background fills.
- The auto-shrink is implemented as a per-month print scale (e.g. a CSS variable
  / transform or font-size step computed from measured content height) bounded by
  the ≈10pt floor; when the floor is reached and content still overflows, the
  month is allowed to flow onto an additional page.

### 2.3 No API, database, or auth change

Printing is entirely client-rendered from existing reads. No endpoint, no
schema/migration, no permission change. The calendar print view respects the
existing `calendar.view`/`calendar.edit` UI gating (read-only view).

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable | Depends on |
|---|-------|-------------|-----------|
| M10-01 | Calendar print view + multi-month | `/calendar/print` route, screen-only controls (start month + count 1–12, default the viewed month), one month per page in order incl. year boundaries, per-page month/year heading, reusing existing reads | — |
| M10-02 | Print page ownership + calendar stylesheet | named `@page` rules (calendar landscape, grocery portrait), **remove** the app-wide landscape and global margin rules, chrome hidden, black-on-white, one-month-per-page break rules | M10-01 |
| M10-03 | Long-name wrapping + auto-shrink | full wrap (no ellipsis in print), auto-shrink to the ≈10pt floor, spill to a second page when still overflowing at the floor; pathological-content tests | M10-02 |
| M10-04 | Printed calendar content rules | day number, recipe/special-event text, special-event icons (black), eat-out/fend as text; no photos, no lock icons, no notes/diners/location | M10-02 |
| M10-05 | Grocery print verification + cross-app polish | verify the M8 grocery print still meets §26 after the `@page` refactor (portrait, black-on-white, department order, paper checkbox, attribution, manual badge, include-checked default off, chrome hidden); fix any regression | M10-02 |
| M10-06 | Print entry point + accessibility | toolbar Print navigates to the print view, Back link, labelled/keyboard-operable controls, print-only content not duplicated for AT, heading semantics | M10-01 |
| M10-07 | Print CSS test battery | Playwright `emulateMedia` computed-style assertions + `page.pdf` page-size/page-count assertions (landscape calendar, portrait grocery, one month per page) + Vitest component tests | M10-01…M10-06 |
| M10-08 | M10 verification sweep | independent §4 battery, docs (`PRODUCT_SPEC`/`API.md` print notes, `TESTING.md` print bar, `CHANGELOG.md`), manifest M10 `complete`, real-stack print check + rebuild/redeploy, tag `m10-complete`, close tracking issue | M10-01…M10-07 |

Milestone mapping: 1→M10-01/M10-02; 2→M10-01; 3→M10-03; 4→M10-05; 5→M10-05;
6→M10-07; verification→M10-08.

---

## 4. M10 verification battery

- **Multi-month:** start month + count 1–12 resolves to the correct ordered
  months, including a job that crosses a year boundary; **one month per page**;
  the PDF page count equals the month count; each page carries its month/year
  heading; a 1-month job is a single page.
- **Page size ownership:** the calendar PDF is **landscape**; the grocery PDF is
  **portrait**; printing a non-print page (e.g. the calendar month view or a
  recipe page) is **not** forced landscape and does not inherit a stray global
  margin (the M5/M8 app-wide rules are gone).
- **Wrapping / shrink / spill:** long names wrap fully with **no ellipsis** in
  print; a busy month **auto-shrinks** but never below the ≈10pt floor; a month
  that still overflows at the floor **continues onto a second page** (nothing is
  clipped or hidden); the shrink is deterministic for the same content.
- **Content:** day numbers present; recipe names and special-event text present;
  special-event icons print in black (no colour-only meaning); eat-out and
  fend-for-yourself days print as text; **no meal photos**, **no lock icons**, and
  **no notes/diner count/restaurant/location** appear.
- **Colour/legibility:** black on white; no background fills; legible in
  greyscale; ≥≈10pt equivalent.
- **Chrome:** app header, nav, footer, skip link, the print controls, and the Back
  link are hidden in print; the printable content is not.
- **Grocery print regression:** after the `@page` refactor the grocery print is
  still portrait, still black-on-white, and still shows department order, the
  paper checkbox, per-meal attribution, the manual badge, and the
  include-checked control (default off).
- **Entry/accessibility:** the calendar toolbar Print navigates to the print view
  and Back returns; controls are labelled and keyboard-operable; heading order is
  sane; print-only content does not confuse assistive tech.
- **Past months:** a past month is printable and renders its stored data.
- **No API/schema change:** no new endpoint, no migration
  (`has-pending-model-changes` clean).

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, the M10 section of `docs/MILESTONES.md`, and the
   issue before each issue. Decisions in §1 are final; contradictions stop
   execution and escalate to the Lead.
2. Use the Roslyn MCP first for every C# inspection/edit/diagnostic. Pass the
   **absolute** `.csproj` to `projectPath`
   (`/mnt/y/backend/src/FateAndFridge.Api/FateAndFridge.Api.csproj`); `filePath`
   resolves against the workspace root `/mnt/y/backend`. Confirm with
   `roslyn_get_project_info` (`is_msbuild_workspace: true`). Never use
   `String.Normalize` (invariant globalization). M10 is expected to be
   frontend-only; any C# touch must still follow this rule.
3. Frontend gates: Vitest/Playwright workers time out on the 9p share `/mnt/y`,
   so run them from a local mirror under `/tmp/opencode/frontend` with a **real
   local `node_modules`** (`npm ci`; never symlink it to the 9p share; sync with
   `rsync --exclude node_modules`, copy formatter output back with `cp`). Never
   commit from the mirror.
4. Backend gates (unchanged code): `dotnet format --verify-no-changes`, Release
   build, Release tests with `FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`,
   and `dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`
   (expected: no pending changes — M10 adds no schema). Do not use `dotnet test`;
   run the test binary with `-class` filters.
5. The reviewer must independently execute the relevant tests and review the
   page-size ownership change (no leakage to other pages), one-month-per-page
   pagination, wrap/shrink/spill behaviour, content rules, colour/legibility, the
   grocery print regression, and print accessibility.
6. At M10 start, set M10 `in_progress` in
   `frontend/src/features/progress/milestones.ts` (and its feature list) and
   create issues. At M10-10, all gates must pass, then delegate a
   rebuild/redeploy to `docker-admin` (`scripts/deploy.sh up`) and verify the
   live instance (healthy; the calendar and grocery print views reachable; a
   real-stack print check producing a landscape calendar PDF with one month per
   page and a portrait grocery PDF; and `/progress` shows M10 complete) before
   setting M10 `complete` with evidence, updating the progress tests and the
   Playwright progress assertion, `CHANGELOG.md`, and the docs, pushing `main`,
   tagging `m10-complete`, and closing the M10 tracking issue. See `AGENTS.md` →
   "Milestone completion gate".

### 5.1 Execution cadence

M10 is executed in **deliverable-sized sessions, not issue-sized ones**, with a
**checkpoint commit after every verified deliverable** (see `docs/M6_PLAN.md`
§5.1). One session owns one authorable *and* independently verifiable unit; commit
as soon as its gates pass; never leave more than one unverified unit in the
working tree. `implementer` runs at `steps: 80`, `reviewer` 28, `docker-admin`
16. The Lead orchestrates and does not author milestone code.

---

## 6. Explicitly deferred from M10

- **List-style calendar print** (`docs/PRODUCT_SPEC.md` §26 explicitly defers it).
- **Weekly print**, **recipe packet printing**, and **custom grocery print
  templates** (`docs/FUTURE_FEATURES.md` → Printing enhancements).
- **Explicit PDF export/download** — the browser's Print → "Save as PDF" is the
  V1 answer; a server-side or client-side PDF export stays on the backlog.
- Printing **notes, diner count, or restaurant/location** text.
- Printing **meal photos** or **lock icons** (excluded by `PRODUCT_SPEC` §26).
- Calendar print for **multiple months on one page** / compact multi-month sheets.
- Any change to the grocery print view's layout or content (verified, not
  reworked).
