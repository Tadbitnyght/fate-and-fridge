# Fate & Fridge — M14 Verification Evidence

Status: **in progress** (filled issue by issue; M14-10 completes the sweep)
Owner: Implementer (evidence) / Reviewer (independent re-run)
Scope: evidence for the M14 hardening battery in `docs/M14_PLAN.md` §4. Each
section states what was run, on which commit, and what was observed. Sections are
deliberately independent so M14-10 can extend this file without rewriting it.

Conventions used below:
- "hermetic" means the Playwright spec renders the real Vite bundle with
  `/api/v1/**` mocked via `page.route`; no backend or database is involved.
- Test counts are from the local repo-shaped mirror (`/tmp/opencode/frontend`),
  because the 9p share cannot host `node_modules` (`docs/M14_PLAN.md` §5.3).

---

## 1. Accessibility — M14-02 (axe WCAG 2.1 AA)

Bar: **WCAG 2.1 AA**, zero axe violations on the key routes, plus a manual
keyboard/focus/label/contrast checklist (`docs/M14_PLAN.md` §1.6, §4).

### 1.1 Automated axe gate

- Spec: `frontend/e2e/a11y.spec.ts` (**29 tests**; 26 in M14-02, plus the three
  reference-data screens added by M14-03).
- Tool: `@axe-core/playwright` 4.14.x (`axe-core` 4.14.x), a **devDependency**;
  it never reaches the runtime bundle or the runtime image.
- Rule sets: `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`. `best-practice` is
  intentionally **not** in the failing set.
- Failure output names the rule id, the CSS target and the help URL.
- Fixtures are deterministic: the app clock is pinned to `2026-10-15` with
  `page.clock.setFixedTime` and the calendar mock returns the same `today`, so
  the outside-month cells (a contrast target) always render. Details and the
  negative controls that prove the gate bites are in §1.7 and §1.8.

Exact command:

```
cd frontend
npx playwright test e2e/a11y.spec.ts --project=chromium
```

Permanent CI job: `.github/workflows/ci.yml` → `a11y`
("Accessibility (axe WCAG 2.1 AA, hermetic Playwright)") runs `npm ci`,
`npx playwright install --with-deps chromium`, the command above, then the
mobile audit spec (§2), and uploads `playwright-a11y-report` on failure. The
job is pinned to the desktop project because the spec manages its own 375 px and
dark viewports; a full local `npx playwright test` also runs it in the `Pixel 7`
and `iPhone 13` projects, where it passes unchanged (29 tests each, §1.5).

### 1.2 Route matrix

| Surface | Route / state | Viewport |
|---|---|---|
| Setup wizard | `/setup` | desktop |
| Login | `/login` | desktop |
| Calendar month | `/calendar` (entry on today) | desktop, 375 px, dark |
| Calendar date detail | `/calendar` + dialog open (edit form) | desktop, 375 px |
| Recipe browser | `/recipes` (grid, filters, badges) | desktop, 375 px, dark |
| Recipe editor (create) | `/recipes/new` | desktop |
| Recipe editor (edit) | `/recipes/1/edit` (ingredients, groups, sections) | desktop |
| Groceries | `/groceries` (departments, checked item, manual item) | desktop, 375 px, dark |
| Groceries staples + manual item | disclosure open + `Add grocery item` dialog | desktop |
| Statistics | `/statistics` (overview cards, bars, event list) | desktop |
| Meal history | `/statistics?tab=history` | desktop |
| Settings household | `/settings` | desktop, member |
| Settings users | `/settings/users` (admin table + actions) | desktop |
| Settings reference data — departments | `/settings/reference-data` (table + `Move up Bakery` reorder row) | desktop |
| Settings reference data — ingredients | `/settings/reference-data/ingredients` (row `Chicken`, alias editor) | desktop |
| Settings reference data — units | `/settings/reference-data/units` (row `g`, factor editor) | desktop |
| Settings security | `/settings/security` | desktop |
| Settings backups | `/settings/backups` | desktop |
| Settings audit log | `/settings/audit` | desktop |
| Settings about | `/settings/about` | desktop, member |
| Settings account | `/settings/account` | member |
| Progress | `/progress` | desktop |

"dark" means `colorScheme: 'dark'`, so the shared contrast tokens are checked in
both palettes.

The three reference-data routes were added by M14-03 so the M14-02 label fixes
are actually audited: the department reorder buttons (`Move up Produce`, finding
5 in §1.3) and the ingredient/unit row editors only exist on those screens. The
departments route asserts a reorder row (`Move up Bakery`) and an ingredient
route asserts a populated ingredient row (`Edit Chicken`), so the tabs cannot
regress to an empty state and still pass.

### 1.3 Violations found → fixes

The first run found 8 failing routes (15 passing). Every finding below was fixed
in `frontend/src`; **no rule was disabled, no node was excluded, and no content
was hidden from the accessibility tree**.

| # | Rule | Where | Fix |
|---|---|---|---|
| 1 | `label-content-name-mismatch` (SC 2.5.3) | Month grid day cells (`frontend/src/features/calendar/MonthGrid.tsx:204`) | Accessible name now starts with the cell's visible text (`<day> <entry>`), then the spoken date/state. `longDateLabel()` (`calendarDates.ts:120`) spells the date with the day number as its own word; a real space text node separates the day number from the entry text (`MonthGrid.tsx:151`) so the rendered text is not concatenated (`24Alpha Chicken`). |
| 2 | `color-contrast` (SC 1.4.3) | Outside-month day numbers (`.calendar-grid__cell--outside`) | Removed `opacity: 0.5` (blended #8b8d8a on #fbfaf9 = 3.21:1) and used the `--muted` token (#5c6660 light / #a3ada8 dark) instead (`index.css:2319`). |
| 3 | `color-contrast` (SC 1.4.3) | Checked grocery row (`.grocery-item--checked`) | Removed `opacity: 0.55` (#80827f on #f6f5f2 = 3.55:1); the row now uses `--muted`, and the strike-through plus the `Manual`/attribution text carry the meaning (`index.css:2853`). |
| 4 | `label-content-name-mismatch` (SC 2.5.3) | Recipe editor move buttons (`IngredientFields.tsx:617,625,770,778`, `InstructionFields.tsx:61,69`) | Labels reordered from `Move ingredient 1 up` to `Move up ingredient 1` so the visible "Move up"/"Move down" is contained in the accessible name. |
| 5 | same class, pre-empted | Reference data department reorder buttons (`DepartmentsTab.tsx:230,238`) | Same reorder (`Move up Produce`) for consistency across the app. |

No narrow exclusions were needed, so there are none to justify.

### 1.4 Manual WCAG 2.1 AA checklist

Verified with the hermetic bundle in Chromium (Playwright keyboard harness on
commit under review), plus the permanent tests named in the "Evidence" column.

| Item | Result | Evidence |
|---|---|---|
| Keyboard order is logical | **Pass** — on `/calendar` route focus lands on the page heading (`h2#calendar-heading`), then Tab walks toolbar → month grid → footer → skip link → account/nav. No focus traps outside dialogs. | instrumented tab-order run (12 stops logged); `useFocusOnRouteChange` |
| Skip link | **Pass** — first element in DOM order; activating it moves focus to `<main id="main-content">`. Note: route-change focus starts at the page heading, so the skip link is bypassed on navigation (intentional SPA behaviour; WCAG 2.4.1 is met by the link **and** the `main` landmark). | `e2e/app.spec.ts` → "skip link moves keyboard focus to the main content" |
| Visible focus | **Pass** — nav links and toolbar buttons render `outline: solid 2px` on focus; calendar cells use `:focus-visible` (`index.css:2332`). | computed-style probe |
| Labels and names | **Pass** — axe `label`, `button-name`, `link-name`, `select-name`, `label-content-name-mismatch`, `aria-*` rules report zero violations across the matrix. | `e2e/a11y.spec.ts` |
| Colour contrast | **Pass** — axe `color-contrast` reports zero violations in light **and** dark schemes, including the muted/outside/checked states fixed above. | `e2e/a11y.spec.ts` |
| No colour-only meaning | **Pass** — checked groceries additionally use strike-through and text ("Manual", attribution); statistics bars print label + value beside the decorative bar (`aria-hidden`); calendar states (today/locked/historical) are text in the cell's accessible name; status chips are text. | code inspection + axe run |
| Dialog focus trap | **Pass** — opening the date detail moves focus inside; 15 consecutive Tabs stayed inside; `Escape` closed it and returned focus to the originating `gridcell`. | instrumented run; `components/Modal.test.tsx` |
| Form errors announced | **Pass** — submitting an empty recipe form focuses a `role="alert"` summary ("Fix these problems before saving / Enter a recipe name.") and marks the field `aria-invalid="true"` with `aria-describedby` pointing at the error text. | instrumented run; `RecipeFormPage` |
| Landmarks / headings | **Pass** — one `h1`, page `h2`, section `h3` order, `banner`/`navigation`/`main`/`footer` landmarks; axe `heading-order`/`landmark-*` rules report zero violations. | `e2e/a11y.spec.ts` |

### 1.5 Gate results (M14-03 working tree on top of `4d2108c`)

| Gate | Result |
|---|---|
| `npm run lint` | pass (exit 0) |
| `npm run format:check` | pass ("All matched files use Prettier code style!") |
| `npm run typecheck` | pass (exit 0) |
| `npm run test` | 544 passed / 544 (51 files) |
| `npm run build` | pass (vite built in 348 ms) |
| `npx playwright test e2e/a11y.spec.ts --project=chromium` | **29 passed, 0 violations** |
| axe in a full `npx playwright test` | 29 passed per project (chromium, Pixel 7, iPhone 13) → **87 passed, 0 violations, 0 failures** |
| `npx playwright test` (full hermetic suite, all three projects) | **179 passed, 9 skipped, 0 failed** (chromium 107 passed / 9 skipped; Pixel 7 36 passed; iPhone 13 36 passed) |
| `python3 -c "yaml.safe_load(...)"` on `.github/workflows/ci.yml` | valid; jobs `backend, frontend, a11y, compose, image-scan, stack`; `a11y.timeout-minutes: 15` |
| `npm ci` from `package-lock.json` | pass (lock and `package.json` in sync; `@axe-core/playwright` resolves) |

The nine skips are expected: three pre-existing `e2e/stack` specs that need a
real `E2E_BASE_URL`, and the six touch-only mobile flows in the desktop project
(`test.skip(({ isMobile }) => !isMobile)`).

### 1.6 Known limitations / risks

- axe catches a subset of WCAG; the manual checklist above is the complement.
- Print stylesheets (`/calendar/print`, `/groceries/print`) and the import review
  screen are not in the axe matrix (print has its own e2e assertions and the
  import flow is covered by M14-06).
- Automated runs use Chromium only; Firefox/WebKit and OS-level assistive tech
  were not exercised.

### 1.7 Deterministic fixtures

Before M14-03 the calendar fixtures derived "today" from `new Date()`
(`isoDate(new Date())`) while the app also used the real clock for the visible
month. The outside-month cells are the target of the M14-02 contrast fix
(finding 2 in §1.3), so on a run whose real date fell in a month without
outside-month cells the assertion would have silently lost its subject.

M14-03 pins both sides:

- `frontend/e2e/a11y.spec.ts` declares `const TODAY = '2026-10-15'` and the
  calendar range/day mocks return it as `today` and as the entry date.
- `mockAppApi` calls `await page.clock.setFixedTime('2026-10-15T12:00:00')`
  before the route handler is installed. `setFixedTime` fakes `Date` only —
  timers, animations and SignalR keep running — so TanStack Query and the
  live-sync connection behave normally.
- The `calendar month` route additionally asserts
  `.calendar-grid__cell--outside` is visible, so a render without outside-month
  cells fails instead of passing an empty contrast check.

2026-10-15 is a Thursday. With `week_starts_on: 0` the October 2026 grid opens
on Sunday 27 September (`buildMonthGrid` pads with `offset = 4`), so it always
renders four leading outside-month cells (27–30 September) that the contrast
rule targets. October 2026 ends on a Saturday, so this particular month has no
trailing padding cells; the leading ones are what the assertion needs, and the
`calendar month` route guard fails loudly if they ever disappear. The other
fixtures (statistics `today: 2026-10-17`, meal-history month `2026-10`) were
already fixed constants and were left alone.

### 1.8 Negative controls

The gate was proved to bite by breaking the app on purpose in the throwaway
mirror (`/tmp/opencode/frontend`), running the spec, and restoring the file from
the repo. **Both experiments were reverted**; `diff -rq` against
`frontend/src` and `frontend/e2e` showed no residue afterwards. Logs:
`/tmp/opencode/neg-control-opacity.log`, `/tmp/opencode/neg-control-imgalt.log`.

| # | Injected defect | Command | Observed failure | Reverted |
|---|---|---|---|---|
| 1 | Restored `opacity: 0.5` on `.calendar-grid__cell--outside` (`src/index.css`) | `npx playwright test e2e/a11y.spec.ts --project=chromium --grep "calendar month"` | **3 failed** (desktop, 375 px, dark). Rule **`color-contrast`** (serious) on `.calendar-grid__cell--outside … > .calendar-grid__daynumber`: **2.16:1** light (`#a9aea9` on `#fbfaf9`) and **2.73:1** dark (`#5c6361` on `#191e21`), against the 4.5:1 floor | yes — `cp frontend/src/index.css` back over the mirror copy |
| 2 | Injected an `<img src="data:image/gif;base64,…">` without `alt` into `AppShell`'s footer | `npx playwright test e2e/a11y.spec.ts --project=chromium --grep "routes \(desktop\).*calendar month"` | **1 failed**. Rule **`image-alt`** (critical): "Element does not have an alt attribute" | yes — `cp` of the original `AppShell.tsx` back over the mirror copy |

Both failures named the rule id, the CSS target and the help URL, which is what
makes the gate actionable rather than just red.

### 1.9 Non-colour cue for checked groceries

Finding 3 in §1.3 removed the opacity dimming that had made a checked grocery
line the only sub-4.5:1 text on the page. The "done" state is still carried by
**more than colour**: the item name keeps
`text-decoration: line-through` (`frontend/src/index.css:2859-2861`), and the
row also prints its `Manual` badge and recipe attribution. The mobile spec
asserts the computed `text-decoration-line` is `line-through` after a check
toggle (`e2e/mobile.spec.ts` → "groceries: list, check toggle and manual item
work on a phone"), so the non-colour cue is now regression-tested, not just
inspected.

---

## 2. Mobile — M14-03

Bar (`docs/M14_PLAN.md` §1.7, §4): a Playwright mobile project for **Pixel 7**
and **iPhone 13** over the main flows, no horizontal overflow at 360/375/390/414
px, usable tap targets, and sane focus order. Fixed in M14 where the audit found
a defect.

### 2.1 Projects

| Project | Device descriptor | Viewport (CSS px) | DPR | Touch | Engine |
|---|---|---|---|---|---|
| `chromium` (unchanged) | `Desktop Chrome` | 1280×720 | 1 | no | Chromium |
| `Pixel 7` | `devices['Pixel 7']` | 412×839 | 2.625 | yes | **Chromium** |
| `iPhone 13` | `devices['iPhone 13']` | 390×664 | 3 | yes | **Chromium** |

`frontend/playwright.config.ts` keeps the desktop project and the hermetic
`webServer` (Vite on `:4173`, `page.route` mocking `/api/v1/**`) and adds the two
mobile projects. Both mobile projects set `browserName: 'chromium'` on purpose:
CI installs only Chromium, and the properties under test (layout, CSS, focus)
are engine-independent here. `iPhone 13` keeps the iOS viewport, DPR, touch
support and user agent, but **not** WebKit — see §2.7.

The mobile projects run `e2e/mobile.spec.ts` and `e2e/a11y.spec.ts`
(`testMatch: /(?:mobile|a11y)\.spec\.ts$/`). The older specs stay in the desktop
project because several assert desktop presentation — `reference-data.spec.ts`
requires a visible `columnheader`, which the ≤ 40rem card layout hides by
design — so running them under phone emulation would test the CSS breakpoint,
not the product. Making that whole suite mobile-aware is not M14-03 scope; it is
recorded as a follow-up risk in §2.7.

Exact commands:

```
cd frontend
npx playwright test e2e/mobile.spec.ts --project="Pixel 7" --project="iPhone 13"
npx playwright test            # full run: desktop + both mobile projects
```

### 2.2 Flows covered per device (7 tests, Pixel 7 and iPhone 13)

Hermetic: the real Vite bundle, real CSS and real routing against mocked
`/api/v1/**`. "Today" is pinned to 2026-10-15 for both the app clock
(`page.clock.setFixedTime`) and the calendar mock, so the month grid, the today
cell and the agenda are identical on every run.

| # | Flow | Asserted |
|---|---|---|
| 1 | Calendar month + agenda | grid renders with the entry; **Primary** navigation links ≥44×44; Month/Agenda toggles ≥44×44; today `gridcell` ≥44×44; Agenda list opens, is reachable, agenda rows ≥44×44; no horizontal overflow in either view |
| 2 | Calendar date detail | dialog opens inside the viewport, focus moves inside it, Save/Cancel ≥44×44, no horizontal overflow, `Escape` closes it **and returns focus to the originating gridcell** |
| 3 | Groceries | department list renders; `Add grocery item` and `Staples` ≥44×44; check toggle flips `Mark Onion as bought` → `not bought` and keeps `text-decoration-line: line-through`; row label hit area ≥44×44; manual-item dialog opens/focuses/submits (`Add item` ≥44×44) and the new item appears under `Other`; no overflow before/after the writes |
| 4 | Recipe browse | heading + card link render; `New recipe`, `Import from URL` and the Grid/List toggle ≥44×44; List view renders; no overflow in both views |
| 5 | Statistics + history | overview renders `Most eaten`; `Overview`/`History` tabs ≥44×44; History shows the `October 2026` region; no overflow in both tabs |
| 6 | Settings sections | 8 sections render (`/settings`, reference-data departments/ingredients/units, users, security, backups, audit, about) with no overflow; Settings nav links ≥44×44; reference-data tab strip ≥44×44; the `Move up Bakery` reorder button ≥44×44 |
| 7 | 360/375/390/414 px sweep | 6 routes × 4 widths, `document.documentElement.scrollWidth <= clientWidth + 1`. Runs in all three projects (it drives its own viewport); the touch flows above are skipped where `isMobile` is false |

The recipe **card link** is deliberately not asserted against the 44 px bar: its
small hit area is `BUG-006`, deferred to M15 (`docs/M14_PLAN.md` §6), and M14-03
must not silently re-open that work.

### 2.3 Overflow measurements (scrollWidth / clientWidth)

Every combination reported `scrollWidth == clientWidth`, i.e. **no horizontal
overflow anywhere**, with `clientWidth` equal to the requested width:

| Width | `/calendar` | `/groceries` | `/recipes` | `/statistics` | `/settings` | `/settings/reference-data` |
|---|---|---|---|---|---|---|
| 360 | 360/360 | 360/360 | 360/360 | 360/360 | 360/360 | 360/360 |
| 375 | 375/375 | 375/375 | 375/375 | 375/375 | 375/375 | 375/375 |
| 390 | 390/390 | 390/390 | 390/390 | 390/390 | 390/390 | 390/390 |
| 414 | 414/414 | 414/414 | 414/414 | 414/414 | 414/414 | 414/414 |

The same figures are printed by the sweep test (`mobile overflow measurements
(scrollWidth/clientWidth) …`) so a future run can diff them. No layout overflow
defect was found, so no overflow fix was needed.

### 2.4 Tap-target defect found and fixed

A census of every visible interactive element at 360/375/390/414 px over the
main routes found the primary chrome still text-sized. Measured before → after
(CSS px, `getBoundingClientRect`):

| Control | Before | After (measured at 360 px) |
|---|---|---|
| Primary nav links (`.app-nav__link`) | 84–109 × **35.2** | 102.8 × **44** |
| Settings nav links (`.settings-nav__link`) | 70–165 × **35.2** | 121.1 × **44** |
| Reference-data tab links | 59–129 × **36.8** | 142 × **44** |
| Month / Agenda toggle buttons | 76–87 × **35.2** | 75.8 × **44** |
| Statistics tabs (`.stats-tab`) | 96–115 × **41** | 115.1 × **44** |
| Primary buttons (`button--primary`, incl. New recipe, Add ingredient) | 110–256 × **42** | 146.6–190.1 × **44** |
| `Import from URL` link | 173–188 × **42** | 173.4 × **44** |
| Header **Sign out** (previously UA-default, no class) | 69 × **21** | 69 × **44** |
| Calendar toolbar arrows (Previous/Next month) | **36 × 44** | **44 × 44** |
| Data-table action buttons (reorder/retire/delete) | 54–144 × **36** | 89.2 × **44** |
| Modal action buttons (Save/Cancel/Add item/Lock/Delete) | 79 × **37.2** | 78.6 × **44** |
| Audit `Details` / history `Correct` buttons | 62–114 × **21** | 62 × **44** / 63 × **44** |
| Skip link (focused) | 197 × **42** | 197.4 × **44** |
| Form `select`s (household settings, audit filters) | 294–328 × **39** | 294 / 328 × **44** |
| Grocery row checkbox **input** | 28 × 28 | 28 × 28 (unchanged — see note) |
| Grocery row label (`.grocery-item__label`, the actual hit area) | 328 × 44 | 328 × 44 (asserted) |

Fix: appended the `touch targets (M14-03)` block at
`frontend/src/index.css:4031-4073`. Inside `@media (max-width: 40rem)` it sets
`min-height: 2.75rem` (44 px) — plus `min-width` for the icon-only calendar
arrows — on the primary navigation, view toggles, action/modal/data-table
buttons, the skip link, and `select`. It reuses the existing convention (the
grocery, calendar-toolbar and reference-data blocks already did this) and
changes **nothing above 40rem**, so the desktop presentation is untouched. The
checkbox stays 28 px on purpose: its wrapping label is the hit area and is
already ≥44 px tall, which is what the spec asserts. No markup change was
required.

### 2.5 Focus and dialog behaviour

Asserted in the mobile spec, not just inspected: the date-detail and
manual-item dialogs open fully inside the viewport (`x >= 0`,
`x + width <= viewport width`), move keyboard focus from the trigger to inside
the `role="dialog"` panel, and `Escape` closes the date-detail dialog and
returns focus to the originating `gridcell`. This is the mobile counterpart of
the M14-02 dialog evidence in §1.4 and runs at both device viewports.

### 2.6 CI wiring

`.github/workflows/ci.yml` → the existing `a11y` job (job id and display name
unchanged, so branch protection is unaffected):

- `timeout-minutes: 15`;
- `Run the axe accessibility spec` → `npx playwright test e2e/a11y.spec.ts --project=chromium`;
- `Run the mobile audit spec (Pixel 7 + iPhone 13)` → `npx playwright test e2e/mobile.spec.ts --project="Pixel 7" --project="iPhone 13"`, failing the job on any assertion;
- the existing `playwright-a11y-report` artifact upload covers both steps.

`npx playwright install --with-deps chromium` already installs the only engine
both mobile projects use, so no extra CI download is needed. `ci.yml` still
parses (jobs `backend, frontend, a11y, compose, image-scan, stack`).

### 2.7 Results and residual risks

| Project | Passed | Skipped | Failed |
|---|---|---|---|
| `chromium` (desktop) | 107 | 9 | 0 |
| `Pixel 7` | 36 | 0 | 0 |
| `iPhone 13` | 36 | 0 | 0 |
| **Full run** | **179** | **9** | **0** |

`Pixel 7`/`iPhone 13` = 29 axe tests + 6 touch flows + 1 width sweep. The nine
desktop skips are the six `isMobile`-gated touch flows plus the three
pre-existing `e2e/stack` specs that need a real `E2E_BASE_URL`.

Known limitations:

- **iPhone 13 runs Chromium, not WebKit.** The project emulates the iOS
  viewport, DPR, touch and UA, but the engine is Chromium because CI installs
  only Chromium and M14 added no new browser download. Safari/WebKit-specific
  CSS (`-webkit-*`, iOS safe areas, rubber-band scrolling) and real iOS input
  behaviours are therefore **not** covered; a WebKit project would be a
  follow-up if a Safari-only defect appears.
- The mobile projects run the two M14 specs only (see §2.1). The older hermetic
  specs still assume desktop presentation, so a mobile-only regression in, say,
  the import or print flows would not be caught by this gate. Extending them is
  a follow-up, not M14-03 scope.
- The 360 px runs are wider than the smallest current phones (320–360 px); 320 px
  was not part of the agreed matrix.
- No physical device or OS-level screen reader was used; everything above is
  emulated Chromium.

---

## 3. Timezone / DST — M14-04

Not yet run (owner: M14-04). Record: the three-zone matrix, the 23:59/00:01
"today" boundary, month/year windows, history ranges, randomizer windows,
audit/backup timestamps, and the frozen-boundary behaviour.

---

## 4. Authorization / past-date / permissions — M14-05

Not yet run (owner: M14-05). Record: route × role matrix (401/403/200 including
single-user), past-date mutation attempts, CSRF, IP allowlist, setup races, rate
limits — with the command used and the pass/fail counts.

---

## 5. Import/SSRF + upload/archive — M14-06

Not yet run (owner: M14-06).

---

## 6. WebSocket auth — M14-07

Not yet run (owner: M14-07).

---

## 7. Concurrency — M14-08

Not yet run (owner: M14-08).

---

## 8. Security report + hands-on pen pass — M14-09

Not yet run (owner: M14-09). Cross-reference `docs/M14_SECURITY_REPORT.md`.

---

## 9. M14 close-out, rebuild/redeploy and live gate — M14-10

Not yet run (owner: M14-10). Record here: the completed commit, CI run URL,
`docker-admin` rebuild evidence (image id/digest), health response, a live
feature check, the `/progress` page showing M14 complete, and the tag.
