/**
 * Checked-in milestone roadmap manifest (issue #22). The `/progress` page renders
 * this offline — no runtime fetch, no external service. Keep it updated when a
 * milestone completes (see AGENTS.md Definition of Done).
 */

export const REPOSITORY_URL = 'https://github.com/Tadbitnyght/fate-and-fridge'

export type MilestoneStatus = 'complete' | 'in_progress' | 'planned'

export interface MilestoneEvidence {
  label: string
  value: string
}

export interface Milestone {
  /** Stable identifier, `M0` … `M14`. */
  id: string
  number: number
  name: string
  status: MilestoneStatus
  summary: string
  features: string[]
  /** ISO date the milestone was accepted, for completed milestones. */
  completedOn?: string
  /** Git tag cut at completion, when one exists. */
  tag?: string
  /** Commit that completed the milestone, when known. */
  commit?: string
  evidence?: MilestoneEvidence[]
  /** GitHub issue numbers belonging to this milestone (empty while planned). */
  issues: number[]
  /** Free-form note shown with the issue links. */
  issuesNote?: string
}

export const MILESTONES: Milestone[] = [
  {
    id: 'M0',
    number: 0,
    name: 'Project Skeleton',
    status: 'complete',
    summary:
      'Working repository and Docker stack: monorepo layout, ASP.NET Core + React shells, PostgreSQL, migrations, health endpoint, lint/format/test runners, CI, and the environment template.',
    features: [
      'Monorepo layout (backend, frontend, docs, compose)',
      'ASP.NET Core minimal-API shell and React/Vite shell',
      'PostgreSQL service and EF Core migration baseline',
      'Docker Compose stack with health endpoint and status screen',
      'Linting, formatting, type checks, and test runners',
      'CI workflow and `.env.example`',
    ],
    completedOn: '2026-09-28',
    evidence: [{ label: 'Acceptance', value: 'compose stack healthy' }],
    issues: [],
    issuesNote:
      'Skeleton work (2026-09-28) predates the GitHub issue tracker; the original task list lives in docs/MILESTONES.md.',
  },
  {
    id: 'M1',
    number: 1,
    name: 'Setup and Authentication',
    status: 'complete',
    summary:
      'First-run setup through the recovery key, multiuser and single-user sessions, the full permission model, and the admin console — verified by an independent review.',
    features: [
      'Schema baseline: household/application settings, users, permissions, sessions, auth events',
      'Single-shot atomic setup completion with a one-time recovery key (mode 0600)',
      'Argon2id password hashing (libsodium) with canonical PHC parsing',
      'DB-backed opaque sessions, rotation/revocation, CSRF via X-CSRF-TOKEN',
      'Login lockout, per-IP throttling, forced password change, admin reset',
      'Seven server-enforced permission keys; edit implies view; last-admin protection',
      'IP allowlist with trusted proxies (health and loopback exempt)',
      'Setup wizard UI, admin console (household/users/security), startup-page routing',
      'Accessibility sweep: skip link, route focus, modal focus traps',
      'Hardening: body limits, JSON Content-Type policy, unified error envelopes',
    ],
    completedOn: '2026-09-30',
    tag: 'm1-complete',
    commit: 'f0c5dc7',
    evidence: [
      { label: 'Backend tests', value: '643' },
      { label: 'Vitest', value: '94' },
      { label: 'Playwright (hermetic)', value: '11' },
      { label: 'Real-stack e2e', value: 'multiuser flow' },
      { label: 'Independent review', value: 'M1-15 tag m1-complete' },
    ],
    issues: Array.from({ length: 21 }, (_, index) => index + 1),
    issuesNote: 'Tracking #1; feature issues #2–#16; verification (#11); follow-ups #17–#21.',
  },
  {
    id: 'M2',
    number: 2,
    name: 'Reference Data',
    status: 'complete',
    summary:
      'Reference-data APIs, a startup seeder, configurable conversion factors, ingredient normalization and merge/audit workflows, and the admin management UI for the household vocabulary.',
    features: [
      'Departments and display ordering',
      'Configurable units, conversion factors, and aliases',
      'Ingredients, aliases, autocomplete, and merge workflows',
      'Primary proteins and tags',
      'Audited reference-data changes and idempotent seeding',
      'Admin reference-data management UI',
    ],
    completedOn: '2026-10-01',
    tag: 'm2-complete',
    commit: '3e25a24',
    evidence: [
      { label: 'Backend tests', value: '976' },
      { label: 'Vitest', value: '165' },
      { label: 'Playwright (hermetic)', value: '20 passed / 2 pre-existing skips' },
      { label: 'M2 verification', value: 'independent review + full battery' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35],
    issuesNote:
      'Tracking #23; M2-01…M2-12: #32, #27, #26, #34, #30, #31, #25, #35, #33, #29, #24, #28.',
  },
  {
    id: 'M3',
    number: 3,
    name: 'Recipes',
    status: 'complete',
    summary:
      'The complete recipe REST API and management/browsing UI: status lifecycle, favorites and diet metadata, secure local images, ordered ingredient groups and instructions, protein/tag associations, and a server-filtered photo-card browser.',
    features: [
      'Recipe model, status (normal/manual-only/retired), and favorite flag',
      'Vegan/vegetarian metadata',
      'Secure local recipe images with alt text, replacement, and removal',
      'Ordered ingredient groups and ingredients with precise decimal quantities, optional units, and preparation notes',
      'Optional ordered instruction sections and steps',
      'Protein and tag associations with retired-reference rules',
      'Recipe create/edit UI with keyboard-accessible nested editors and fraction display',
      'Recipe browser: photo cards, compact list, search, filters, sorts, and paging',
      'Recipe references block reference-data hard deletes and ingredient merges reassign them',
    ],
    completedOn: '2026-10-03',
    tag: 'm3-complete',
    evidence: [
      { label: 'Backend tests', value: '1066' },
      { label: 'Vitest', value: '180' },
      { label: 'Playwright (hermetic)', value: '24 passed / 2 pre-existing stack skips' },
      { label: 'M3 verification', value: 'independent review + full battery' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [36, 40, 41, 42, 43, 44, 45, 46, 47, 48, 37, 38, 39],
    issuesNote:
      'Tracking #36; M3-01…M3-12: #40, #41, #42, #43, #44, #45, #46, #47, #48, #37, #38, #39.',
  },
  {
    id: 'M4',
    number: 4,
    name: 'URL Import',
    status: 'complete',
    summary:
      'Safe recipe import from the web: SSRF-hardened fetching, schema.org JSON-LD first with an HTML fallback, deterministic normalization and matching, and a mandatory draft-only review step.',
    features: [
      'SSRF-hardened fetcher: scheme/host/IP limits, per-hop DNS and redirect revalidation, timeouts and size/content limits',
      'Schema.org Recipe JSON-LD parser with multi-candidate pages and a static HTML/microdata fallback',
      'Deterministic quantity, fraction, and unit normalization with canonical/alias ingredient matching',
      'Draft-only import endpoint and review screen with unknown-ingredient/unit resolution',
      'Image download from URL under the M3 image policy (after review)',
      'Duplicate warnings: exact normalized source URL and scored similar names with a rename recheck',
      'Import security battery: SSRF/redirect/rebinding/content/malformed-markup matrix and route permission matrix',
    ],
    completedOn: '2026-10-05',
    tag: 'm4-complete',
    evidence: [
      { label: 'Backend tests', value: '1615' },
      { label: 'Vitest', value: '220' },
      { label: 'Playwright (hermetic)', value: '33 passed / 2 pre-existing stack skips' },
      { label: 'M4 verification', value: 'independent review + full §4 battery' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64],
    issuesNote:
      'Tracking #51; M4-01…M4-13: #52, #53, #54, #55, #56, #57, #58, #59, #60, #61, #62, #63, #64.',
  },
  {
    id: 'M5',
    number: 5,
    name: 'Calendar Core',
    status: 'complete',
    summary:
      'Month and agenda planning with special events, locked dates, and server-enforced historical immutability in the household timezone.',
    features: [
      'Calendar entry model, history table, and the M5CalendarCore migration',
      'Desktop ARIA-grid month calendar, keyboard navigation, and a mobile agenda',
      'Date-detail editor with per-type fields and manual recipe selection',
      'Eight meal/special-event types with a curated inline-SVG icon set',
      'Diner metadata with the household default and effective count',
      'Lock/unlock and a server-computed repeat warning',
      'Server-enforced historical immutability plus the frozen-timezone boundary',
      'Arbitrary future navigation (bounded) and a landscape print skeleton',
    ],
    completedOn: '2026-10-06',
    tag: 'm5-complete',
    evidence: [
      { label: 'Backend tests', value: '1642' },
      { label: 'Vitest', value: '238' },
      { label: 'Playwright (hermetic)', value: '37 passed / 2 pre-existing stack skips' },
      { label: 'M5 verification', value: 'independent review + full §4 battery' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79],
    issuesNote:
      'Tracking #65; M5-01…M5-14: #66, #67, #68, #69, #70, #71, #72, #73, #74, #75, #76, #77, #78, #79.',
  },
  {
    id: 'M6',
    number: 6,
    name: 'Randomization Engine',
    status: 'complete',
    summary:
      'The “what’s for dinner?” engine: eligibility, repeat-window penalties, spacing, favorites as a weak tie-break, and secure randomness.',
    features: [
      'Randomizer service contract and candidate eligibility',
      '7-day backward and 14-day forward exact-repeat windows',
      'Special-event and direct-neighbour protein penalties',
      'Spacing score and favorite weighting',
      'Secure random selection',
      'Single-date reroll with metadata and temporary undo',
      'Exhaustive randomizer tests',
    ],
    completedOn: '2026-10-08',
    tag: 'm6-complete',
    commit: '9b83d05',
    evidence: [
      { label: 'Backend tests', value: '1771' },
      { label: 'Vitest', value: '252' },
      {
        label: 'Playwright (hermetic)',
        value: '37 passed / 2 pre-existing stack skips',
      },
      { label: 'M6 verification', value: 'independent review + full §4 battery' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92],
    issuesNote: 'Tracking #80; M6-01…M6-12: #81–#92.',
  },
  {
    id: 'M7',
    number: 7,
    name: 'Batch Planning',
    status: 'complete',
    summary:
      'Planning a range at once: previews, conflicts, atomic apply, and lock/vacation awareness.',
    features: [
      '7-day reroll preview and “Try Another”',
      'Atomic apply and rest-of-month preview',
      'Fill Empty Days',
      'Lock behavior in batch operations',
      'Vacation blocking',
      'Batch concurrency protection',
    ],
    completedOn: '2026-10-09',
    tag: 'm7-complete',
    commit: 'd121947',
    evidence: [
      { label: 'Backend tests', value: '1818' },
      { label: 'Vitest', value: '266' },
      { label: 'Playwright (hermetic)', value: '37 passed / 2 pre-existing stack skips' },
      { label: 'M7 verification', value: 'independent review + full §4 battery' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104],
    issuesNote: 'Tracking #94; M7-01…M7-10: #95–#104.',
  },
  {
    id: 'M8',
    number: 8,
    name: 'Groceries',
    status: 'complete',
    summary:
      'The live grocery projection: aggregation, compatible unit families, manual items and staples, department ordering, and printing.',
    features: [
      'Grocery aggregation service with recipe attribution',
      'Unit conversion families (incompatible units stay separate)',
      'Date range presets and custom ranges',
      'Manual grocery items and reusable staples',
      'Department ordering, checkbox persistence, hide checked',
      'Mobile grocery UI and printable list',
    ],
    completedOn: '2026-10-12',
    tag: 'm8-complete',
    commit: '80bc722',
    evidence: [
      { label: 'Backend tests', value: '2029' },
      { label: 'Vitest', value: '315' },
      { label: 'Playwright (hermetic)', value: '47 passed / 2 pre-existing stack skips' },
      { label: 'M8 verification', value: 'independent review + full §4 battery' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116],
    issuesNote: 'Tracking #106; M8-01…M8-10: #107–#116. Runbook: docs/M8_PLAN.md.',
  },
  {
    id: 'M9',
    number: 9,
    name: 'Live Sync',
    status: 'complete',
    summary:
      'Multi-device updates over an authenticated SignalR hub with coarse events and client query invalidation.',
    features: [
      'WebSocket authentication',
      'Calendar, grocery, and lock update events',
      'Recipe, settings, and reference-data update events',
      'Session revocation and socket teardown',
      'Query invalidation on events',
      'Two-device sync tests',
    ],
    completedOn: '2026-10-13',
    tag: 'm9-complete',
    commit: 'db9569c',
    evidence: [
      { label: 'Backend tests', value: '2114' },
      { label: 'Vitest', value: '341' },
      { label: 'Playwright (hermetic)', value: '49 passed / 3 pre-existing stack skips' },
      { label: 'M9 verification', value: 'independent review + full §4 battery' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130],
    issuesNote: 'Tracking #120; M9-01…M9-10: #121–#130. Runbook: docs/M9_PLAN.md.',
  },
  {
    id: 'M10',
    number: 10,
    name: 'Printing',
    status: 'complete',
    summary:
      'Print output for the calendar and grocery list: a dedicated landscape calendar print view with multi-month page handling, long-name wrapping with auto-shrink and spill, and per-view print page ownership.',
    features: [
      'Landscape monthly calendar print with multi-month page handling',
      'Long-name wrapping with auto-shrink and spill',
      'Per-view print page ownership (calendar landscape, grocery portrait)',
      'Grocery print attribution and include-checked option',
      'Browser print CSS testing (print media + PDF geometry)',
    ],
    completedOn: '2026-10-14',
    tag: 'm10-complete',
    commit: 'ee6729d',
    evidence: [
      { label: 'Backend tests', value: '2114' },
      { label: 'Vitest', value: '378' },
      { label: 'Playwright (hermetic)', value: '65 passed / 3 pre-existing stack skips' },
      { label: 'M10 verification', value: 'independent review + full §4 battery' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [131, 132, 133, 134, 135, 136, 137, 138, 139],
    issuesNote: 'Tracking #131; M10-01…M10-08: #132–#139. Runbook: docs/M10_PLAN.md.',
  },
  {
    id: 'M11',
    number: 11,
    name: 'Statistics and History',
    status: 'complete',
    summary:
      'Household statistics and a read-only meal-history browser, computed on demand over the current calendar entries (no new tables, no caching).',
    features: [
      'Household overview: most eaten, least recently eaten, meal count this month/year',
      'Per-recipe Last Eaten / Next Planned / Times Eaten on the browser cards and detail',
      'Primary protein distribution (offline CSS bars) and the seven special-event counts',
      'Read-only, month-grouped, filterable meal-history browser',
    ],
    completedOn: '2026-10-18',
    tag: 'm11-complete',
    commit: '141168a',
    evidence: [
      { label: 'Backend tests', value: '2183' },
      { label: 'Vitest', value: '420' },
      { label: 'Playwright (hermetic)', value: '67 passed / 3 pre-existing stack skips' },
      { label: 'M11 verification', value: 'independent review + full §4 battery' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [140, 141, 142, 143, 144, 145, 146, 147, 148],
    issuesNote: 'Tracking #140; M11-01…M11-08: #141–#148. Runbook: docs/M11_PLAN.md.',
  },
  {
    id: 'M12',
    number: 12,
    name: 'Administration',
    status: 'complete',
    summary:
      'The admin surface: an Admin-only audit log with filters, export and retention, a past-date historical correction workflow, hard recipe deletion with snapshot/tombstone integrity, and a shared dangerous-action confirmation framework.',
    features: [
      'Audit coverage across calendar, recipe, user/permission, settings, grocery and login events',
      'Admin audit log: merged audit + reroll timeline, filters, paging, before/after detail',
      'Audit CSV/JSON export and a confirmed, age-based retention prune',
      'Historical correction of a single past date (reason, audit, snapshot refresh, locked-date acknowledgement)',
      'Hard recipe deletion: snapshot backfill, planned-entry removal, tombstones, image removal, audited',
      'Admin-only dangerous-action confirmation framework (reused by M13)',
    ],
    completedOn: '2026-10-19',
    tag: 'm12-complete',
    commit: '820a20c',
    evidence: [
      { label: 'Backend tests', value: '2293' },
      { label: 'Vitest', value: '489' },
      { label: 'Playwright (hermetic)', value: '70 passed / 3 pre-existing stack skips' },
      { label: 'M12 verification', value: 'independent review + full §4 battery' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 159],
    issuesNote: 'Tracking #149; M12-01…M12-10: #150–#159. Runbook: docs/M12_PLAN.md.',
  },
  {
    id: 'M13',
    number: 13,
    name: 'Backup / Restore / Updates',
    status: 'complete',
    summary:
      'Encrypted manual backups with validated, safety-backup-first restore (including a clean install), recovery-key export, release version display, and a privacy-preserving update check.',
    features: [
      'Encrypted `.ffbackup` archives: DB dump, images, Data Protection keys, checksummed manifest',
      'Backup list/create/download/delete with metadata, retention and audit',
      'In-app restore with validation, mandatory safety backup, maintenance mode, restart and session revocation',
      'Clean-install restore from the setup wizard with the recovery key',
      'Recovery-key export behind the dangerous-action confirmation',
      'Release version display and a cached, offline-tolerant GitHub Releases update check',
    ],
    completedOn: '2026-10-22',
    tag: 'm13-complete',
    commit: 'a385a79',
    evidence: [
      { label: 'Backend tests', value: '2487' },
      { label: 'Vitest', value: '542' },
      { label: 'Playwright (hermetic)', value: '77 passed / 3 pre-existing stack skips' },
      { label: 'M13 verification', value: 'independent review + 3 live restore round-trips' },
      { label: 'Backend gates', value: 'format/build/EF clean' },
    ],
    issues: [162, 163, 164, 165, 166, 167, 168, 169, 170, 171, 172],
    issuesNote: 'Tracking #162; M13-01…M13-10: #163–#172. Runbook: docs/M13_PLAN.md.',
  },
  {
    id: 'M14',
    number: 14,
    name: 'Hardening',
    status: 'in_progress',
    summary:
      'Final security and quality pass: dependency and image vulnerability gates, WCAG 2.1 AA accessibility, mobile, timezone/DST, authz/SSRF/upload/archive/WebSocket/concurrency batteries, the open security bugs, and a hands-on pen pass.',
    features: [
      'Dependency and container-image vulnerability gates in CI',
      'WCAG 2.1 AA accessibility gate (axe + manual checklist)',
      'Mobile Playwright project (Pixel 7 + iPhone 13) and fixes',
      'Three-zone timezone/DST suite',
      'Authz, past-date bypass, SSRF/import, upload/archive and WebSocket auth batteries',
      'Multiuser concurrency races; the open security bugs fixed',
      'Hands-on pen pass on a disposable stack and a security report',
    ],
    issues: [173, 174, 175, 176, 177, 178, 179, 180, 181, 182, 183],
    issuesNote: 'Tracking #173; M14-01…M14-10: #174–#183. Runbook: docs/M14_PLAN.md.',
  },
  {
    id: 'M15',
    number: 15,
    name: 'v1.0.0 Release Readiness',
    status: 'planned',
    summary:
      'The release gate after the M0–M14 plan: remove development-only surfaces (the in-app roadmap, OpenAPI, the permission probe), clean up the UI, and ship v1.0.0.',
    features: [
      'Remove development-only surfaces (in-app roadmap, OpenAPI, permission probe, dead placeholders)',
      'Public-repo presentation: remove AI-development scaffolding and agent/process docs',
      'UI copy, consistency, visual polish and app metadata',
      'Accessibility and mobile pass across product routes',
      'Dependency, asset and runtime-image cleanup',
      'Version 1.0.0 with CHANGELOG, release notes and the v1.0.0 release',
      'Release gate: suites, independent review, deploy and live verification',
    ],
    issues: [],
    issuesNote: 'Added 2026-10-22 after the M0–M14 plan; defined in docs/MILESTONES.md.',
  },
]

export interface ProgressSummary {
  total: number
  completed: number
  inProgress: number
  /** Completed share, rounded to a whole percent. */
  percent: number
}

export function progressSummary(milestones: Milestone[] = MILESTONES): ProgressSummary {
  const total = milestones.length
  const completed = milestones.filter((milestone) => milestone.status === 'complete').length
  const inProgress = milestones.filter((milestone) => milestone.status === 'in_progress').length
  return {
    total,
    completed,
    inProgress,
    percent: total === 0 ? 0 : Math.round((completed / total) * 100),
  }
}

export const STATUS_LABELS: Record<MilestoneStatus, string> = {
  complete: 'Complete',
  in_progress: 'In progress',
  planned: 'Planned',
}

export function issueUrl(issueNumber: number): string {
  return `${REPOSITORY_URL}/issues/${issueNumber}`
}
