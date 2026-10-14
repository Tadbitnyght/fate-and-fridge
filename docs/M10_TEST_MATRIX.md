# M10 Test Matrix — Printing

Status: **coverage map for M10-05 and M10-07** (2026-10-14, branch `feature/m10-printing`).
Feeds `docs/M10_VERIFICATION.md` at M10-08. Source of truth for the battery is
`docs/M10_PLAN.md` §4 plus the locked decisions §1.1–§1.16; the print CSS
contract is §2.2 and the content rules are `docs/PRODUCT_SPEC.md` §26.

This file records, for every §4 battery area, the primary automated test(s) that
cover it — Vitest component/unit tests, the hermetic Playwright suite
(`emulateMedia` computed-style assertions and `page.pdf` page-size/page-count
assertions), and the pure geometry guards. It is the "audit result plus added
gaps" map: the per-issue suites were checked first and only the missing bullets
were added in `frontend/e2e/print.spec.ts` **(extended M10-07)**,
`frontend/e2e/groceries.spec.ts` **(extended M10-05/M10-07)**, and
`frontend/src/features/groceries/GroceryPrintPage.test.tsx` **(extended M10-05)**.

## How to run the matrix

Frontend gates run from a local mirror under `/tmp/opencode/frontend` (the 9p
share times out Vitest/Playwright workers) with a real local `node_modules`
(`rsync --exclude node_modules`, copy formatter output back with `cp`; never
commit from the mirror):

```bash
rsync -a --exclude node_modules --exclude dist --exclude test-results \
  --exclude playwright-report /mnt/y/frontend/ /tmp/opencode/frontend/
cd /tmp/opencode/frontend
npm run lint && npm run format:check && npm run typecheck && npm test
npm run build
npx playwright test e2e/print.spec.ts e2e/groceries.spec.ts e2e/app.spec.ts e2e/calendar.spec.ts
```

The print assertions are hermetic (no stack): the specs mock `/api/v1/**` with
`page.route` and run against the local Vite dev server. `page.pdf({
preferCSSPageSize: true })` makes Chromium honour the CSS `@page` size, so the
PDF `/MediaBox` and `/Type /Page` counts prove the printed geometry. The
real-stack print check (deployed instance, PDF/screenshot evidence) is added at
the M10-10 gate (`docs/M10_PLAN.md` §1.14).

M10 adds **no schema**, so `dotnet ef migrations has-pending-model-changes` is
expected to be empty; the backend is untouched by M10.

## §4 battery → coverage

| §4 area | Primary coverage |
|---------|------------------|
| Multi-month — start month + count 1–12 resolves to the correct ordered months | `printRange.test.ts` "resolves a single month", "resolves an ordered range across a year boundary", "always resolves between 1 and 12 months for any count", "prefers the ?month= param and falls back to the current month"; `CalendarPrintPage.test.tsx` "defaults to the ?month= month and shows the screen-only controls", "reacts to a ?month= change instead of freezing the first value" |
| Multi-month — count bounded to 1–12 | `printRange.test.ts` "clamps the month count to 1–12"; `CalendarPrintPage.test.tsx` "clamps the month count control to 1–12"; `print.spec.ts` "print: the month count is bounded to twelve pages" (the control's `min`/`max` attributes, a `99` entry clamping to `12`, twelve headings, and a 12-page PDF) **(new M10-07)** |
| Multi-month — one month per page; PDF page count equals the month count | `print.spec.ts` "print: a 1-month calendar job is one landscape A4 PDF page", "print: a 3-month calendar job is three landscape pages" |
| Multi-month — a job crossing a year boundary | `printRange.test.ts` "resolves an ordered range across a year boundary"; `CalendarPrintPage.test.tsx` "renders one heading per month in order across a year boundary"; `print.spec.ts` "print: a year-boundary multi-month job prints one page per month" (Nov 2026 → Jan 2027 in order, 3 pages) **(new M10-07)** |
| Multi-month — each page carries its month/year heading; household name absent | `calendarPrintFormat.test.ts` "renders the month/year heading without the household name"; `CalendarPrintPage.test.tsx` "renders one heading per month in order across a year boundary"; every PDF test asserts the heading is visible before printing |
| Page size ownership — calendar PDF is landscape | `print.spec.ts` "print: a 1-month calendar job is one landscape A4 PDF page", "print: a 3-month calendar job is three landscape pages", "print: a year-boundary multi-month job prints one page per month", "print: the month count is bounded to twelve pages" (A4 landscape `/MediaBox`) |
| Page size ownership — grocery PDF is portrait | `print.spec.ts` "print: the grocery print job is an A4 portrait PDF page" (A4 portrait `/MediaBox`) |
| Page size ownership — named `@page` rules only; no app-wide size or margin | `print.spec.ts` "print: page-size ownership is named with no unnamed @page rule" (no unnamed `@page` rule; `calendar-print` = A4 landscape; `grocery-list` = A4 portrait; `@page calendar-print` margin matches `--calendar-print-page-margin-mm`; the interactive calendar page is `page: auto`); `printScale.test.ts` "keeps the @page calendar-print margin in sync with the geometry" |
| Page size ownership — a non-print page is not forced landscape | `print.spec.ts` "print: a non-print page keeps the default portrait paper" (the calendar month view prints portrait) |
| Wrapping — long names wrap fully, no ellipsis, no clipped overflow | `print.spec.ts` "print: a pathological month spills to more pages instead of clipping" (`white-space: normal`, no `text-overflow: ellipsis`, `overflow: visible`, the full text present), "print: a busy month shrinks to fit without dropping below the legibility floor" (`scrollWidth <= clientWidth`) |
| Shrink — a busy month auto-shrinks but never below the ≈10pt floor | `printScale.test.ts` "shrinks proportionally to fit the page", "never shrinks below the legibility floor", "expresses the floor as a pt-equivalent font size"; `print.spec.ts` "print: a busy month shrinks to fit without dropping below the legibility floor" (applied `--calendar-print-scale` is `< 1` and `>= floor/base`; the computed font size is `>= 10pt`), "print: a pathological month spills to more pages instead of clipping" (applied scale == the floor and the computed font size == 10pt) **(new M10-07)** |
| Shrink — the shrink is deterministic for the same content | `printScale.test.ts` "is deterministic for the same content" (a pure function of the measured height and the fixed geometry) |
| Spill — a month that still overflows at the floor continues onto a second page, nothing clipped | `printScale.test.ts` "spills only when the month cannot fit even at the floor"; `print.spec.ts` "print: a pathological month spills to more pages instead of clipping" (`data-spill="true"`, PDF pages `> 1`, full text) |
| Content — day numbers present | `CalendarPrintPage.test.tsx` "prints the day number and the recipe/special-event/eat-out/fend text"; `print.spec.ts` "print: the printed month shows allowed content and none of the excluded data" (the Lasagna cell's day number is `5`) **(new M10-07)** |
| Content — recipe names and special-event text present | `calendarPrintFormat.test.ts` "prints the recipe name for a recipe entry", "prints the special-event text for icon-bearing types"; `CalendarPrintPage.test.tsx` "prints the day number and the recipe/special-event/eat-out/fend text"; `print.spec.ts` "print: the printed month shows allowed content and none of the excluded data" **(new M10-07)** |
| Content — special-event icons print in black (no colour-only meaning) | `calendarPrintFormat.test.ts` "prints a curated icon only for the four icon-bearing types"; `CalendarPrintPage.test.tsx` "prints the curated special-event icon in black" (`stroke="currentColor"`, `aria-hidden`); `print.spec.ts` "print: the printed month shows allowed content and none of the excluded data" (the icon's computed colour is black in print), "print: dark mode still prints black on white" **(new M10-07)** |
| Content — eat out and fend-for-yourself print as text | `calendarPrintFormat.test.ts` "prints eat out and fend for yourself as fixed text, never restaurant/location"; `CalendarPrintPage.test.tsx` "prints the day number and the recipe/special-event/eat-out/fend text"; `print.spec.ts` "print: the printed month shows allowed content and none of the excluded data" **(new M10-07)** |
| Content — no meal photos, no lock icons, no notes/diner count/restaurant/location | `CalendarPrintPage.test.tsx` "excludes meal photos, lock icons, notes, diner count, and restaurant/location text"; `calendarPrintFormat.test.ts` "prints eat out and fend for yourself as fixed text, never restaurant/location"; `print.spec.ts` "print: the printed month shows allowed content and none of the excluded data" **(new M10-07)** |
| Colour/legibility — black on white, no background fills, ≥≈10pt | `print.spec.ts` "print: the calendar print view renders a month and hides chrome in print" (body black on white), "print: dark mode still prints black on white" (body/heading/cell/icon black; heading and cell backgrounds transparent), "print: a busy month … legibility floor" / "print: a pathological month …" (the ≥10pt floor) **(extended M10-07)**; `printScale.test.ts` "expresses the floor as a pt-equivalent font size" |
| Chrome — app header, nav, footer, skip link, controls, and Back link hidden in print; content not | `print.spec.ts` "print: the calendar print view renders a month and hides chrome in print" (header/nav/footer/skip link/controls/Print/Back hidden, the month and heading survive); `groceries.spec.ts` "groceries print: print media hides app chrome and screen-only controls" (header/nav/footer/skip link/Print/include-checked/Back hidden, the list survives) |
| Grocery print regression — still portrait after the `@page` refactor | `print.spec.ts` "print: the grocery print job is an A4 portrait PDF page", "print: page-size ownership is named with no unnamed @page rule" |
| Grocery print regression — department order with Other last | `groceryFormat.test.ts` "forces the Other group last and preserves server order otherwise"; `GroceryPrintPage.test.tsx` "renders the range heading, departments in order with Other last, and attribution"; `groceries.spec.ts` "groceries print: departments, attribution, manual badge, and the include-checked toggle" |
| Grocery print regression — a decorative checkbox before each item | `GroceryPrintPage.test.tsx` "draws a decorative, screen-reader-hidden checkbox before each item" (one per item, `aria-hidden`, first child of the line, not an interactive control) **(extended M10-05)**; `groceries.spec.ts` "groceries print: print media hides app chrome and screen-only controls" (one `aria-hidden` box per item, black border) **(extended M10-05)** |
| Grocery print regression — recipe/date attribution rows | `GroceryPrintPage.test.tsx` "renders the range heading, departments in order with Other last, and attribution"; `groceries.spec.ts` "groceries print: departments, attribution, manual badge, and the include-checked toggle" (the `Tacos` / `Tue 10/13` source row) |
| Grocery print regression — manual items included, marked without colour | `GroceryPrintPage.test.tsx` "includes manual items marked with the icon+text Manual badge"; `groceries.spec.ts` "groceries print: departments, attribution, manual badge, and the include-checked toggle" (visible `Manual` text); `groceries.spec.ts` "groceries print: print media hides app chrome and screen-only controls" (the badge has no background fill) **(extended M10-05)** |
| Grocery print regression — checked excluded by default, include-checked reveals them | `GroceryPrintPage.test.tsx` "excludes checked items by default and reveals them via the include toggle"; `groceries.spec.ts` "groceries print: departments, attribution, manual badge, and the include-checked toggle" |
| Grocery print regression — black on white, no background fills | `groceries.spec.ts` "groceries print: print media hides app chrome and screen-only controls" (body black on white; department/item/line/badge backgrounds transparent; the paper checkbox border is black) **(extended M10-05)** |
| Grocery print regression — chrome hidden in print | `groceries.spec.ts` "groceries print: print media hides app chrome and screen-only controls" |
| Entry — the calendar toolbar Print navigates to the print view; Back returns | `CalendarPage.test.tsx` "opens the print view from the toolbar (M10-06)"; `CalendarPrintPage.test.tsx` "links back to the calendar"; `print.spec.ts` "print: the Back link returns to the calendar"; `groceries.spec.ts` "groceries: the list offers a print entry point that opens the print view" |
| Accessibility — labelled, keyboard-operable controls and a sane heading order | `print.spec.ts` "print: the calendar print view is keyboard-operable with a sane heading order" (h2 before the month h3s; the `Months` control is labelled and changes with the keyboard; the decorative icon is `aria-hidden`) **(new M10-07)**; `groceries.spec.ts` "groceries print: the include-checked control is labelled and keyboard-operable" (Space toggles it; the paper checkbox is `aria-hidden`) **(new M10-07)**; `CalendarPrintPage.test.tsx` / `GroceryPrintPage.test.tsx` labelled-control lookups; `print.spec.ts` "print: the printed month shows allowed content …" / `CalendarPrintPage.test.tsx` "prints the curated special-event icon in black" (decorative icons hidden from AT) |
| Accessibility — print-only content does not confuse assistive tech | `CalendarPrintPage.test.tsx` "prints the curated special-event icon in black" (icon `aria-hidden`); `GroceryPrintPage.test.tsx` "draws a decorative, screen-reader-hidden checkbox before each item" (checkbox `aria-hidden`, no interactive checkbox in the print list); the decorative paper checkbox is not in the accessibility tree in both print-view e2e tests |
| Past months — a past month prints and renders its stored data | `print.spec.ts` "print: a past month prints and renders its stored data" (August 2026 heading and its stored recipe text, 1 landscape page) **(new M10-07)** |
| No API/schema change | Structural: M10 adds no endpoint, no migration, and no auth change (`docs/M10_PLAN.md` §2.3); `dotnet ef migrations has-pending-model-changes` is expected to be empty. No backend test is added or changed. |
| Permission gating — the print views respect the existing reads | `CalendarPrintPage.test.tsx` "shows a permission message without calendar.view and issues no calendar read"; `GroceryPrintPage.test.tsx` "shows a permission message without groceries.view" |

## Audit result

**M10-05 (grocery print verification) — existing coverage was strong; four gaps closed.**
The audit of `GroceryPrintPage.tsx`, `GroceryPrintPage.test.tsx`, and
`e2e/groceries.spec.ts` confirmed the M8-08 view already had automated proof for:
A4 portrait (`print.spec.ts` PDF + named `@page`), department order with "Other"
last (`groceryFormat.test.ts`, `GroceryPrintPage.test.tsx`,
`e2e/groceries.spec.ts`), the decorative `aria-hidden` checkbox, recipe/date
attribution rows, the manual icon+text badge, checked-excluded-by-default with
the include-checked reveal, and chrome hidden in print. The missing pieces added
were: the printed Back link being hidden, one `aria-hidden` paper checkbox per
printed item (count parity), **no background fills** on the printed
department/item/line/badge elements, and a keyboard-operability assertion for
the include-checked control. **No regression was found**: the only grocery CSS
change is the intended `@page grocery-list { size: A4 portrait; margin: 12mm }`
(the global M8 `@page { margin: 12mm }` is gone and the view opts in via
`.grocery-print-page { page: grocery-list }`), and every §26 requirement still
holds.

**M10-07 (print battery) — five gaps closed.**
The audit confirmed existing coverage for multi-month resolution/clamping
(`printRange.test.ts`, `CalendarPrintPage.test.tsx`), 1- and 3-month PDF page
counts, page-size ownership, wrapping/ellipsis, and the pure shrink/spill math.
The gaps added were: the e2e **content rules** (day number, recipe/special-event
text, the black icon, and the absence of photos/lock/notes/diners/restaurant/
location), the **applied shrink floor** (a busy month shrinks but stays ≥10pt; a
pathological month sits exactly at the floor), the **year-boundary page count**
and the **12-page bound**, a **past month** print, and **print-view
accessibility** (keyboard operability + heading order). No §4 battery bullet
failed, so no product defect was fixed.

## Deferred items (not covered here; recorded for M10-08/M14)

- The **real-stack print check** (deployed instance, PDF/screenshot evidence) is
  the M10-10 gate, not a hermetic assertion (`docs/M10_PLAN.md` §1.14).
- **List-style calendar print**, **weekly print**, **recipe packet printing**,
  **custom grocery print templates**, and **explicit PDF export/download** are
  explicitly deferred (`docs/M10_PLAN.md` §6, `docs/FUTURE_FEATURES.md`).
- Printing **notes, diner count, restaurant/location**, **meal photos**, or
  **lock icons** is out of scope by product decision (`docs/M10_PLAN.md` §1.7,
  `PRODUCT_SPEC` §26).
- **Multiple months on one page** / compact multi-month sheets are deferred
  (`docs/M10_PLAN.md` §6).
- **Greyscale rendering** is proven indirectly (black on white, no background
  fills, the ≥10pt floor); a pixel-level greyscale comparison is not automated.
