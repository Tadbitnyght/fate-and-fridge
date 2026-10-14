# M10 Verification — Printing

Status: **complete** (2026-10-14, tag `m10-complete`). Tracking issue: #139
(M10-01…M10-08 are #132–#139). The milestone is marked `complete` in the progress
manifest and the live deployment was rebuilt, redeployed, and verified (see
**Live deployment verification** below).

M10 delivers **printing**: a dedicated, deep-linkable **calendar print view** at
`/calendar/print` (start month + 1–12 months, one month per page, long-name
wrapping with auto-shrink and spill) plus **verification and cross-app polish of
the grocery print view**. It is **not** a new API, a schema change, an
event/export format, or a server-side PDF.

## No schema, entity, API, or auth change

**M10 is frontend-only.** It adds **no migration**, no new table, no entity, no
endpoint, and no permission change. Printing stays **client-rendered** from the
existing reads: the calendar print view reuses the existing month-scoped
calendar range read (one request per printed month) and the grocery print view
reuses the existing `GET /api/v1/groceries` read. There is **no bulk/print
endpoint** (`docs/M10_PLAN.md` §1.12, §2.3).

`dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`
is expected to be **empty**, and is (see Evidence). `docs/API.md` records the
no-new-endpoint note and the print-route contract.

## Print contract (summary)

The full contract is in `docs/M10_PLAN.md` §2 and the content rules are
`docs/PRODUCT_SPEC.md` §26; the exhaustive coverage map is
`docs/M10_TEST_MATRIX.md`.

- **Routes (client-rendered, no new endpoint):** the calendar print view is the
  deep-linkable client route **`/calendar/print`** (mirroring `/groceries/print`)
  inside the session-guarded shell; the calendar toolbar's Print button
  **navigates** to it. Both print views carry a Back link. The grocery Print
  entry is unchanged.
- **Multi-month:** the view selects a **start month** (defaulting to the month
  being viewed, carried as `?month=YYYY-MM`) and a **month count of 1–12**,
  printing **one month per page** in chronological order, including jobs that
  cross a **year boundary**. Each page carries a **month/year heading** (e.g.
  "October 2026"); the household name is never printed. A **past** month prints
  and renders its stored data.
- **Wrap / shrink / spill:** long names **wrap fully** (never ellipsis-clipped in
  print). A busy month **auto-shrinks** — a deterministic, pure function of the
  measured content height and the fixed page geometry — but never below the
  **≈10pt legibility floor** (base 12pt × the 10/12 floor scale). A month that
  still overflows at the floor **continues onto a following page**
  (`data-spill="true"`) rather than shrinking further or clipping. Nothing is
  unreadable or hidden.
- **Per-view named `@page` ownership:** the calendar opts into
  **`@page calendar-print { size: A4 landscape; margin: 10mm }`** via
  `.calendar-print-page { page: calendar-print }`; the grocery list opts into
  **`@page grocery-list { size: A4 portrait; margin: 12mm }`** via
  `.grocery-print-page { page: grocery-list }`. The app-wide
  `@page { size: landscape }` (M5) and the global `@page { margin: 12mm }` (M8)
  are **removed**, so printing any other page keeps the browser defaults (the M8
  reviewer's finding is closed). The calendar `@page` margin is pinned to the
  shared `--calendar-print-page-margin-mm` geometry so CSS and the JS fit budget
  cannot drift.
- **Colour / legibility:** black on white, no background fills, no colour-only
  meaning, legible in greyscale, text ≥≈10pt equivalent — for both views.
- **Printed calendar content:** the day number and the recipe name or
  special-event text; the curated **special-event icons print in black**
  (`stroke: currentColor`, `aria-hidden`); **eat out** and **fend for yourself**
  print as fixed **text**. **No** meal photos, **no** lock icons, and **no**
  notes / diner count / restaurant / location.
- **Chrome hidden in print:** the app header/nav/footer, the skip link, the
  screen-only controls, the Print button, and the Back link are hidden; the
  printable content is not. Controls are labelled and keyboard-operable with a
  sane heading order.

## How to run the matrix

```bash
export FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts
cd backend
dotnet format --verify-no-changes
dotnet build -c Release FateAndFridge.slnx
dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api
ARTIFACTS=/tmp/fate-and-fridge-artifacts/bin/FateAndFridge.Api.Tests/release/FateAndFridge.Api.Tests
# Full suite (rebuild first; run the binary directly — do not use `dotnet test`):
$ARTIFACTS
```

M10 touches no backend code, so the backend gates are a **regression gate**
(recorded below) and add no M10 battery.

Frontend gates run from a local mirror under `/tmp/opencode/frontend` (the 9p
share times out Vitest/Playwright workers) with a real local `node_modules`
(`rsync --exclude node_modules`; never commit from the mirror):

```bash
rsync -a --exclude node_modules --exclude dist --exclude test-results \
  --exclude playwright-report /mnt/y/frontend/ /tmp/opencode/frontend/
cd /tmp/opencode/frontend
npm run lint && npm run format:check && npm run typecheck && npm test
npm run build
npx playwright test
```

The print assertions are **hermetic** (no stack): the specs mock `/api/v1/**`
with `page.route` and run against the local Vite dev server. `page.pdf({
preferCSSPageSize: true })` makes Chromium honour the CSS `@page` size, so the
PDF `/MediaBox` and `/Type /Page` counts prove the printed geometry. The
**real-stack print check** (deployed instance, PDF/screenshot evidence) is the
M10-10 gate, not a hermetic assertion (`docs/M10_PLAN.md` §1.14); it is recorded
below.

## §4 battery → coverage

The exhaustive per-area coverage map is **`docs/M10_TEST_MATRIX.md`** (M10-05 /
M10-07) — it lists every §4 battery bullet from `docs/M10_PLAN.md` §4 and its
primary automated test(s) (Vitest first, then the hermetic Playwright suite), and
records the gaps closed in the M10-05 / M10-07 sweeps. Reproduced compactly:

| §4 area | Primary coverage (see `docs/M10_TEST_MATRIX.md` for the full list) |
|---------|--------------------------------------------------------------------|
| Multi-month (start month + count 1–12; one month per page; PDF page count = month count; year boundary; per-page heading; 1-month = one page) | `printRange.test.ts`, `CalendarPrintPage.test.tsx`, `print.spec.ts` |
| Page-size ownership (calendar landscape; grocery portrait; named `@page` only; a non-print page stays portrait) | `print.spec.ts`, `printScale.test.ts` |
| Wrapping / shrink / spill (full wrap, no ellipsis; shrink never below ≈10pt; spill to a second page at the floor; deterministic) | `print.spec.ts`, `printScale.test.ts` |
| Content (day numbers; recipe/special-event text; black icons; eat-out/fend as text; no photos/lock/notes/diners/location) | `CalendarPrintPage.test.tsx`, `calendarPrintFormat.test.ts`, `print.spec.ts` |
| Colour / legibility (black on white; no background fills; greyscale-legible; ≥≈10pt) | `print.spec.ts`, `printScale.test.ts` |
| Chrome (header/nav/footer/skip link/controls/Print/Back hidden; content survives) | `print.spec.ts`, `groceries.spec.ts` |
| Grocery print regression (portrait; department order with Other last; paper checkbox; attribution; manual badge; checked-excluded default + include-checked; black on white; chrome hidden) | `GroceryPrintPage.test.tsx`, `groceryFormat.test.ts`, `print.spec.ts`, `groceries.spec.ts` |
| Entry / accessibility (toolbar Print navigates; Back returns; labelled, keyboard-operable controls; sane heading order; print-only content hidden from AT) | `CalendarPage.test.tsx`, `CalendarPrintPage.test.tsx`, `print.spec.ts`, `groceries.spec.ts` |
| Past months (a past month prints its stored data) | `print.spec.ts` |
| Permission gating (the print views respect the existing reads) | `CalendarPrintPage.test.tsx`, `GroceryPrintPage.test.tsx` |
| No API/schema change | Structural (`docs/M10_PLAN.md` §2.3); `has-pending-model-changes` empty; no backend test added or changed |

## Evidence at completion

Recorded on the M10-08 commit chain (base HEAD `4d3170c` plus the M10-08
docs/manifest/test changes). The milestone is marked `complete` in the progress
manifest; the `m10-complete` tag points at the final verification commit after
the live checks below.

| Gate | Result |
|------|--------|
| `dotnet format --verify-no-changes` | clean (exit 0) |
| Backend Release build (`FateAndFridge.slnx`) | **0 warnings / 0 errors** on the warm tree; a from-scratch restore emits 104 environmental `NETSDK1188` locale warnings from the transitive `Microsoft.EntityFrameworkCore.Design` → `Microsoft.CodeAnalysis.Workspaces.MSBuild 5.0.0` package (present since the .NET 10 rebuild, unchanged by M10) |
| `dotnet ef migrations has-pending-model-changes` | no pending model changes |
| Backend full suite (`FateAndFridge.Api.Tests`) | **Total 2114, Errors 0, Failed 0, Skipped 0, Not Run 0** |
| Frontend lint / format:check / typecheck (local mirror) | clean |
| Vitest | **378 passed** (41 files) |
| Playwright (hermetic, `npx playwright test`) | **65 passed / 3 pre-existing stack skips** |
| `npm run build` | succeeds (pre-existing >500 kB chunk warning only) |

No stale full-suite expectation failed: M10 adds no schema, so
`M2SchemaBaselineModelTests.All_expected_entities_are_mapped` is unaffected.

Independent review outcome: the M10 print view, page-size ownership change,
pagination, wrap/shrink/spill behaviour, content rules, colour/legibility, the
grocery print regression, and print accessibility were reviewed per issue on the
branch. The M10-05 grocery sweep found **no regression** (the only grocery CSS
change is the intended named `@page grocery-list` opt-in) and the M10-07 print
battery found **no §4 battery failure**; both sweeps added only tests
(`docs/M10_TEST_MATRIX.md` → Audit result).

## Live deployment verification

Verified on 2026-10-14 after rebuilding and redeploying via `docker-admin` using
`scripts/deploy.sh up` (`docker compose up -d --build` on the NAS).

- **Deploy-host checkout:** `/mnt/Data/calendarplanner` at `ee6729d` on `main`,
  working tree clean. `/mnt/y` is the same SMB/9p-exported tree, so there is one
  checkout. Operational note: the `CPlanner` deploy user cannot `git fetch`/`pull`
  because `.git` is not writable by that user; `HEAD` already matched
  `origin/main` at `ee6729d`, so no pull was needed.
- **Image `fate-and-fridge:0.1.0`:** `sha256:fd085e5d9d56…` →
  `sha256:1cfda8f702d94724ccca202538cc4f6b9adc3fe129602cebdbf99d492e26f922`,
  created `2026-10-14T13:36:34-05:00`. This was a **real frontend rebuild** — the
  `npm run build` layer re-ran and emitted new `dist/assets` hashes, not a cache
  hit. Only the app container was recreated; postgres was untouched.
- **No migration:** M10 adds no schema, so the app startup logged
  `No migrations were applied. The database is already up to date.`; this is
  consistent with `dotnet ef migrations has-pending-model-changes` being empty.
- **Health:** `GET /api/v1/health` →
  `200 {"status":"ok","app":"Fate & Fridge","version":"0.1.0","environment":"production"}`.
- **Served assets:** JS `/assets/index-BIqtPxq_.js` (636,773 B, sha256
  `fb8ffe0fccfabe810f845bce38367b423885440d38ad254f5ce948a3afb2ea9c`); CSS
  `/assets/index-C8jfaYXT.css` (43,830 B). The JS bundle contains the
  `calendar-print`, `print-scale`, and `Printing` strings and the M10 completion
  date `2026-10-14`.
- **Served CSS page-size ownership proof:** the deployed CSS carries exactly
  **two** `@page` rules —
  `@page calendar-print{size:A4 landscape;margin:10mm}` and
  `@page grocery-list{size:A4 portrait;margin:12mm}` — and **no unnamed `@page`**
  rule carrying a size or margin; `.calendar-print-page{page:calendar-print}` is
  present. So the M5 app-wide landscape rule and the M8 global margin rule are
  gone from the live bundle.
- **Live browser verification** (read-only Playwright, no mutations):
  - `/progress` renders `11 of 15 milestones complete (73%).` with the
    **M10 — Printing** card showing **Complete** / `Completed 2026-10-14`, and
    M11–M14 as Planned.
  - `/calendar/print?month=2026-10` renders the `October 2026` heading, the
    weekday headers, day numbers, real meal cells, and the screen-only controls
    (`Back to calendar`, `Start month` = `2026-10`, `Months` = `1`, `Print`);
    with no `?month` param it defaults to the current month (2026-10).
  - Under print-media emulation the app header/nav/footer, the skip link, the
    screen-only controls, the `Back` link, and the `h2` are hidden while the
    month heading and the grid remain visible.
  - Screenshots: `.playwright-mcp/m10-progress.png`,
    `.playwright-mcp/m10-print-screen.png`.
- **Real-stack PDF checks** (`page.pdf({ preferCSSPageSize: true })`):
  - calendar **1 month** → **1 page**, MediaBox **841.92 × 594.96 pt (A4
    landscape)**;
  - calendar **3 months** → **3 pages**, landscape each, with the DOM headings
    `October 2026` / `November 2026` / `December 2026`;
  - `/groceries/print` → **594.96 × 841.92 pt (A4 portrait)**;
  - non-print `/calendar` → **612 × 792 pt (US Letter portrait)**, proving there
    is **no app-wide landscape rule** left.
- **Expected observation (not a defect):** the live `/groceries/print` job spans
  **3 pages** because the household's current list is long (8 departments, 20+
  items); a printed grocery list flows naturally across pages. The hermetic
  one-page assertion applies to a small mocked list. The plan requirement is a
  **portrait** grocery PDF, which holds.
- **PostgreSQL** still publishes no host port; nothing persistent
  (volumes/datasets/data dirs) was deleted — only the app container was
  recreated.

## CI status after completion

`main`'s CI was **red** when this milestone was marked complete. Both failures
were diagnosed and fixed after the fact on `main`:

- **BUG-009** — the M9 test-only `Microsoft.AspNetCore.SignalR.Client 10.0.12`
  pulled `Microsoft.AspNetCore.SignalR.Common 10.0.12`, affected by
  CVE-2026-70159 / GHSA-334f-vvp4-7g6p (High). Because the repository treats
  NuGet audit warnings as errors, the Backend job failed at `Restore` (`NU1903`).
  Fixed in `d8b09ef` by bumping the test dependency to `10.0.13`. The production
  API project never referenced SignalR NuGet packages, so it was not affected.
- **BUG-010** — the M9-08 real-stack spec (`frontend/e2e/stack/live-sync.spec.ts`)
  requires and consumes the single-shot first-run setup, and ran before the
  pre-existing acceptance spec, so the "Docker stack acceptance + Playwright" job
  failed at `stack.spec.ts:45` (landing on `/login` instead of `/setup`). Fixed in
  `3913ad6` (gate the spec behind `E2E_STACK_LIVE_SYNC=1` and give it its own
  fresh stack in CI) and `8c9f994` (the reset must also clear the **bind-mounted**
  data directory, because `docker compose down -v` removes no named volumes in
  this stack).

Process lesson recorded in `docs/BUG_TODO.md`: **CI must be green before a
milestone is marked complete or tagged.** The verification above was performed on
the completion commits; these CI fixes landed on `main` afterwards and do not
change the milestone's product scope or contracts.

**Resolved:** all four CI jobs are green on `main` at `05a9ab8` — Frontend,
Backend (18m23s), Compose, and the Docker stack acceptance job (including the
newly gated real-stack live-sync flow, 1 passed) — and `BUG-009`/`BUG-010` are
closed in `docs/BUG_TODO.md`.
