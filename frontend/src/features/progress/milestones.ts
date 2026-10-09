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
    commit: '2088cf2',
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
    status: 'planned',
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
    issues: [],
    issuesNote:
      'Planned in docs/MILESTONES.md; GitHub issues are created when the milestone starts.',
  },
  {
    id: 'M9',
    number: 9,
    name: 'Live Sync',
    status: 'planned',
    summary:
      'Multi-device updates over WebSockets with authenticated connections and query invalidation.',
    features: [
      'WebSocket authentication',
      'Calendar, grocery, and lock update events',
      'Query invalidation on events',
      'Two-device sync tests',
    ],
    issues: [],
    issuesNote:
      'Planned in docs/MILESTONES.md; GitHub issues are created when the milestone starts.',
  },
  {
    id: 'M10',
    number: 10,
    name: 'Printing',
    status: 'planned',
    summary: 'Print output for the calendar and grocery list, with browser print CSS.',
    features: [
      'Landscape monthly print and multi-month page handling',
      'Long-name wrapping',
      'Grocery print attribution and include-checked option',
      'Browser print CSS testing',
    ],
    issues: [],
    issuesNote:
      'Planned in docs/MILESTONES.md; GitHub issues are created when the milestone starts.',
  },
  {
    id: 'M11',
    number: 11,
    name: 'Statistics and History',
    status: 'planned',
    summary: 'Household statistics and a meal-history browser over the planned archive.',
    features: [
      'Statistics overview',
      'Recipe history metrics',
      'Protein distribution',
      'Special-event counts',
      'Meal-history browser',
    ],
    issues: [],
    issuesNote:
      'Planned in docs/MILESTONES.md; GitHub issues are created when the milestone starts.',
  },
  {
    id: 'M12',
    number: 12,
    name: 'Administration',
    status: 'planned',
    summary:
      'Audit trail, historical correction workflow, hard recipe deletion with snapshot/tombstone integrity, and dangerous-action confirmation.',
    features: [
      'Audit event model and service',
      'Admin audit viewer',
      'Historical correction workflow and audit entry',
      'Hard recipe deletion workflow',
      'Snapshot/tombstone handling',
      'Admin-only dangerous-action confirmation framework',
    ],
    issues: [],
    issuesNote:
      'Planned in docs/MILESTONES.md; GitHub issues are created when the milestone starts.',
  },
  {
    id: 'M13',
    number: 13,
    name: 'Backup / Restore / Updates',
    status: 'planned',
    summary:
      'Recovery-key export, encrypted backups with integrity metadata, validated restore, and update checks. (Key generation and storage shipped early in M1.)',
    features: [
      'Recovery key export',
      'Encrypted backup format with checksum/version metadata',
      'Database and image/config backup',
      'Restore validation and safety backup before restore',
      'Restore workflow',
      'Release version display and periodic update check',
    ],
    issues: [],
    issuesNote:
      'Planned in docs/MILESTONES.md; recovery-key generation/storage were delivered early in M1 (#6).',
  },
  {
    id: 'M14',
    number: 14,
    name: 'Hardening',
    status: 'planned',
    summary:
      'Final security and quality pass: penetration tests, audits, concurrency, timezone/DST, dependency scans, and documentation polish.',
    features: [
      'Permission-matrix penetration tests and past-date bypass tests',
      'SSRF security review and malicious upload tests',
      'Backup archive traversal tests',
      'WebSocket auth tests',
      'Timezone/DST suite and multiuser concurrency tests',
      'Accessibility and mobile audits',
      'Dependency/security scan and documentation polish',
    ],
    issues: [],
    issuesNote:
      'Planned in docs/MILESTONES.md; GitHub issues are created when the milestone starts.',
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
