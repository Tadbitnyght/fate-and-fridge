# Fate & Fridge — Milestone 14 Plan (Hardening)

Status: **approved, ready to execute**
Approved: 2026-10-22 (product owner answered the full M14 decision set)
Purpose: authoritative M14 runbook. A fresh session should execute M14 from this
file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product,
architecture, API, database, security and testing specifications without asking
further product questions. Where this file and an older spec disagree, escalate
to the Lead before implementing.

---

## 1. Locked product decisions

1. **Scope: audit *and* fix.** M14 verifies the whole security/quality surface and
   fixes what it finds: **blockers and majors are fixed before the milestone
   completes**, minors are filed as `BUG-###` entries with provenance. It also
   clears the open **security / data-integrity** bugs: `BUG-002` (latent import
   group/unknown-unit metadata), `BUG-003` (concurrent PUT/reroll vs batch apply
   500), `BUG-007` (query cache not cleared on logout) and `BUG-011` (reroll vs
   hard-delete FK race). UI-polish bugs (`BUG-004/005/006/008`) belong to **M15**.
2. **Development/test-only tooling is allowed** for the audits: `axe-core` (via
   Playwright), `dotnet list package --vulnerable`, `npm audit`, and an image
   scanner (Trivy or Grype, pinned). **Nothing is added to the runtime image or
   the shipped app**; the runtime dependency list and the `Dockerfile` final stage
   stay product-only except for the already-approved `postgresql-client-17`.
3. **Vulnerability policy.** CI fails on any **High/Critical** advisory **that has
   a fix** (NuGet, npm, and the container image). Medium/Low are reported and
   tracked; an advisory with **no available fix** may be accepted only with a
   documented exception (package, advisory id, why, review date) in the M14
   security report.
4. **Image scanning runs in CI** as a permanent job (pinned scanner, High/Critical
   with a fix fails). Base-image/library updates needed to clear findings are part
   of this milestone.
5. **Penetration depth.** Automated adversarial suites **plus** a focused
   **hands-on pass on a disposable stack** (authz, past-date bypass, live SSRF
   import, upload/archive bombs). The production instance is **never** targeted
   destructively; read-only production checks only. Evidence goes in the M14
   security report.
6. **Accessibility bar: WCAG 2.1 AA.** `axe-core` runs over the key routes
   (setup, login, calendar + date detail, recipes browser + editor, groceries,
   statistics/history, settings sections, progress) with **zero violations**, plus
   a manual keyboard/focus/label/contrast checklist. Findings are fixed in M14.
7. **Mobile audit.** A Playwright **mobile project** (`Pixel 7` and `iPhone 13`)
   covering the main flows (calendar/agenda, date detail, groceries, recipe
   browse, statistics/history, settings), plus a manual 360/375/390/414 px check.
   No horizontal overflow, tap targets usable, focus order sane. Fixes in M14.
8. **Timezone/DST matrix.** `America/Chicago` (deployment default),
   `Pacific/Auckland` (Southern Hemisphere) and `Asia/Kolkata` (half-hour offset),
   covering spring-forward and fall-back days and the frozen-timezone boundary,
   across every timezone-dependent surface: the "today" boundary, calendar
   past/future rules, statistics month/year windows, meal-history ranges,
   randomizer windows, and audit/backup timestamps.
9. **Concurrency battery.** Two users editing the same date; batch apply vs single
   edit; grocery check toggles; session revocation mid-request; backup/restore vs
   writes; duplicate-submit races. Blockers/majors found are fixed; minors are
   filed. `BUG-003` and `BUG-011` are closed here.
10. **Documentation polish targets only the docs that survive M15**:
    `PRODUCT_SPEC`, `ARCHITECTURE`, `API`, `DATABASE`, `SECURITY`, `TESTING`,
    `DEPLOYMENT`, plus the M14 security/a11y/mobile reports. `README`,
    `CHANGELOG` and the AI-scaffolding removal remain **M15**.
11. **Permanent CI additions:** vulnerable-package scan, `npm audit`, the image
    scan, the axe checks and the mobile Playwright project. The hands-on pen pass
    stays a documented one-off gate (do not make it a CI job).
12. **No schema change and no new runtime dependency** are expected. No new
    infrastructure, no telemetry, no external services.

---

## 2. Architecture and deliverables

M14 adds **tests, scanners and reports**, not product features. Expected code
touch points are limited to genuine fixes:

- `backend/tests/FateAndFridge.Api.Tests/` — new adversarial suites (authz,
  DST, concurrency, WebSocket, archive/upload) and regression tests for the fixed
  bugs.
- `frontend/` — `axe-core` checks, a Playwright mobile project, and the UI fixes
  those audits require.
- `.github/workflows/ci.yml` — the new gates.
- `backend/src/` / `frontend/src/` — only where an audit finds a real defect.
- New docs: `docs/M14_SECURITY_REPORT.md`, `docs/M14_TEST_MATRIX.md`,
  `docs/M14_VERIFICATION.md` (a11y/mobile evidence lives in the verification doc).

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable | Depends on |
|---|-------|-------------|-----------|
| M14-01 | Dependency + image scan gate | `dotnet list package --vulnerable` + `npm audit` + a pinned image scan in CI with the High/Critical-with-fix policy; bump/upgrade to clear findings; record no-fix exceptions | — |
| M14-02 | Accessibility gate + fixes | `axe-core` over the key routes (zero WCAG 2.1 AA violations), a manual keyboard/focus/label checklist, CI job, and the resulting UI fixes | — |
| M14-03 | Mobile project + fixes | Playwright `Pixel 7` + `iPhone 13` projects over the key flows, the 360–414 px manual check, and the layout/tap-target fixes found | — |
| M14-04 | Timezone/DST suite | Three-zone + transition coverage across today/calendar/statistics/history/randomizer/audit/backup timestamps, with fixes for any defects | — |
| M14-05 | Authz / past-date / permission battery | Every route × role (incl. single-user), past-date mutation bypass attempts, CSRF, IP allowlist, setup races, rate limits; fixes | — |
| M14-06 | Import/SSRF + upload/archive battery | SSRF matrix (loopback/private/link-local/metadata/IPv6/decimal/octal/DNS rebinding/redirects), size/time/content limits, image bombs/polyglots/SVG, archive traversal/corruption/bombs; fixes | — |
| M14-07 | WebSocket auth battery | Unauthenticated/pre-auth rejection, revoked/expired session disconnect, no cross-session or cross-household leakage, teardown on logout/disable; fixes | — |
| M14-08 | Concurrency battery | The six scenarios in §1.9 with deterministic orchestration; fix blockers/majors; file minors | M14-05 |
| M14-09 | Security fixes + hands-on pen pass | Fix `BUG-002`, `BUG-003`, `BUG-007`, `BUG-011` with regression tests; run the hands-on pass on a disposable stack; write `docs/M14_SECURITY_REPORT.md` (findings, fixes, exceptions) | M14-01…M14-08 |
| M14-10 | M14 verification sweep | Independent §4 battery, product docs polish (SECURITY/TESTING/etc.), coverage matrix, manifest M14 `complete`, rebuild/redeploy + live gate, tag `m14-complete`, close the tracking issue | M14-01…M14-09 |

Milestone mapping: 1→M14-05; 2→M14-05; 3→M14-06; 4→M14-06; 5→M14-06; 6→M14-07;
7→M14-04; 8→M14-08; 9→M14-02; 10→M14-03; 11→M14-01; 12→M14-10.

---

## 4. M14 verification battery

- **Dependencies/image:** `dotnet list package --vulnerable` and `npm audit` report
  no High/Critical advisory with a fix; the pinned image scan reports none either
  (or each exception is documented with package/advisory/reason/review date).
  The runtime dependency list is unchanged except approved bumps, and no test-only
  package reaches the runtime image.
- **M14-01 image/build:** the scanner is pinned and fails CI on a High/Critical
  fix; the app still builds, starts and passes health.
- **M14-03 mobile:** Pixel 7 and iPhone 13 runs of the key flows pass; no
  horizontal overflow at 360/375/390/414 px; tap targets and focus order are
  usable; the calendar agenda, date detail, groceries, recipe browse,
  statistics/history and settings sections are all covered.
- **M14-04 timezone:** the "today" boundary is correct at 23:59/00:01 local in all
  three zones; month/year statistics and history ranges use the household month;
  randomizer windows honour the local date; audit/backup timestamps are
  unambiguous; spring-forward/fall-back days behave; the frozen boundary cannot be
  crossed by a timezone change.
- **M14-05 authz:** every route × role matrix is asserted (401/403/200 incl.
  single-user); a member cannot perform an Admin action by any verb; past-date
  writes are rejected on every path (PUT/DELETE/lock/reroll/batch/import/correction
  without Admin); CSRF is enforced on cookie-authenticated unsafe methods; the
  allowlist blocks non-covered clients but never health/loopback; setup races
  produce exactly one winner; rate limits return 429 with `Retry-After`.
- **M14-05/06 upload/SSRF:** the SSRF matrix rejects loopback, private,
  link-local, metadata, IPv6/embedded, decimal/octal hosts, DNS rebinding and
  disallowed redirects; size/time/content-type limits hold; image bombs
  (dimensions/size), polyglots and SVG/script payloads are rejected; archive
  traversal (`../`, absolute, symlink), corruption and bomb inputs are rejected
  without unbounded resource use.
- **M14-07 WebSocket:** no session → rejected; revoked/expired/disabled session →
  disconnected; pre-auth events are not delivered; no other session's data is
  observable; logout/disable tears the socket down.
- **M14-08 concurrency:** the six scenarios produce no 500s, lost updates or
  corrupted rows; `BUG-003`/`BUG-011` have regression tests and no longer
  reproduce; a second concurrent backup/restore is still refused.
- **M14-09 bugs:** `BUG-002`, `BUG-003`, `BUG-007`, `BUG-011` are closed with
  regression tests; the hands-on pass evidence is recorded; no production data was
  altered.
- **M14-02 a11y:** axe reports zero violations on the key routes; the manual
  checklist (keyboard order, focus visibility, labels, contrast, no colour-only
  meaning) passes; modal focus traps still work.
- **Docs/build:** product docs match the shipped behaviour (the new gates and the
  security report are recorded); format/lint/type/build/EF clean; the full backend
  and frontend suites pass; the milestone is rebuilt, redeployed and live-verified.

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, the M14 section of `docs/MILESTONES.md`, and the
   issue before each issue. Decisions in §1 are final; contradictions stop
   execution and escalate to the Lead.
2. Roslyn MCP first for all C# work (absolute `.csproj` `projectPath`, `filePath`
   relative to `/mnt/y/backend`); the workspace has been stale for newly added
   files — use `roslyn_get_project_info` plus targeted reads and state the
   fallback. Never `String.Normalize`.
3. Frontend gates run from a **repo-shaped** mirror (`/tmp/opencode/frontend` **and**
   `/tmp/opencode/docs`) with a real local `node_modules`; never commit from the
   mirror.
4. Backend gates: `dotnet format --verify-no-changes`, Release build, Release tests
   via the test binary with `-class` filters (never `dotnet test`),
   `dotnet ef migrations has-pending-model-changes`.
5. The reviewer independently executes the battery, re-runs the scans, and probes
   the findings itself — it must not accept a report that only cites implementer
   tests.
6. At M14 start, set M14 `in_progress` and create issues. At M14-10, all gates must
   pass **and CI must be green**, then `docker-admin` rebuilds/redeploys and the
   live instance is verified before M14 is marked `complete`, tagged
   `m14-complete`, pushed, and the tracking issue closed. See `AGENTS.md` →
   "Milestone completion gate".

### 5.1 Execution cadence

Deliverable-sized sessions with a **checkpoint commit after every verified
deliverable**; never leave more than one unverified unit in the working tree.

---

## 6. Explicitly deferred from M14

- UI-polish bugs `BUG-004` (change-type error), `BUG-005` (save stays on the
  form), `BUG-006` (card click target), `BUG-008` (grocery label) — **M15**.
- Removing the AI-development scaffolding and the in-app roadmap, and the public
  history decision — **M15**.
- The v1.0.0 version bump, release notes and release — **M15**.
- Scheduled/remote backups, per-capability permission keys, and the other V2 items
  in `docs/FUTURE_FEATURES.md`.
- Any new feature work: M14 is hardening only.
