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

- Spec: `frontend/e2e/a11y.spec.ts` (26 tests).
- Tool: `@axe-core/playwright` 4.14.x (`axe-core` 4.14.x), a **devDependency**;
  it never reaches the runtime bundle or the runtime image.
- Rule sets: `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`. `best-practice` is
  intentionally **not** in the failing set.
- Failure output names the rule id, the CSS target and the help URL.

Exact command:

```
cd frontend
npx playwright test e2e/a11y.spec.ts
```

Permanent CI job: `.github/workflows/ci.yml` → `a11y`
("Accessibility (axe WCAG 2.1 AA, hermetic Playwright)") runs `npm ci`,
`npx playwright install --with-deps chromium`, then the spec above, and uploads
`playwright-a11y-report` on failure.

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
| Settings security | `/settings/security` | desktop |
| Settings backups | `/settings/backups` | desktop |
| Settings audit log | `/settings/audit` | desktop |
| Settings about | `/settings/about` | desktop, member |
| Settings account | `/settings/account` | member |
| Progress | `/progress` | desktop |

"dark" means `colorScheme: 'dark'`, so the shared contrast tokens are checked in
both palettes.

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

### 1.5 Gate results (commit under review)

| Gate | Result |
|---|---|
| `npm run lint` | pass (exit 0) |
| `npm run format:check` | pass ("All matched files use Prettier code style!") |
| `npm run typecheck` | pass (exit 0) |
| `npm run test` | 544 passed / 544 (51 files) |
| `npm run build` | pass |
| `npx playwright test e2e/a11y.spec.ts` | **26 passed, 0 violations** |
| `npx playwright test` (full hermetic suite) | 103 passed, 3 skipped (pre-existing stack skips) |
| `python3 -c "yaml.safe_load(...)"` on `.github/workflows/ci.yml` | valid; jobs `backend, frontend, a11y, compose, image-scan, stack` |
| `npm ci` from `package-lock.json` | pass (lock and `package.json` in sync; `@axe-core/playwright` resolves) |

### 1.6 Known limitations / risks

- axe catches a subset of WCAG; the manual checklist above is the complement.
- Print stylesheets (`/calendar/print`, `/groceries/print`) and the import review
  screen are not in the axe matrix (print has its own e2e assertions and the
  import flow is covered by M14-06).
- Automated runs use Chromium only; Firefox/WebKit and OS-level assistive tech
  were not exercised.

---

## 2. Mobile — M14-03

Not yet run (owner: M14-03). Record: Pixel 7 and iPhone 13 project results, the
360/375/390/414 px overflow checks, tap-target measurements, focus order.

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
