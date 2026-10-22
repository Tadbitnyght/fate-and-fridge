/**
 * Checked-in future-features manifest mirroring `docs/FUTURE_FEATURES.md`
 * (issue #117). The `/progress` page's "Future & ideas" tab renders this
 * offline — no runtime fetch, no external service.
 *
 * Keep in sync with docs/FUTURE_FEATURES.md; guarded by futureFeatures.test.ts.
 */

export type FutureFeatureStatus =
  'v2' | 'v1x' | 'enhancement' | 'research' | 'never_v1' | 'rejected'

export const FUTURE_STATUS_LABELS: Record<FutureFeatureStatus, string> = {
  v2: 'V2',
  v1x: 'V1.x',
  enhancement: 'Enhancement',
  research: 'Research',
  never_v1: 'Not planned for V1',
  rejected: 'Rejected',
}

export interface FutureFeature {
  id: string
  /** Section heading from `docs/FUTURE_FEATURES.md` (used by the consistency guard). */
  title: string
  status: FutureFeatureStatus
  summary: string
  source: string
}

export const FUTURE_FEATURES: FutureFeature[] = [
  {
    id: 'pantry-inventory',
    title: 'Pantry / inventory',
    status: 'v2',
    summary:
      'Track pantry, fridge, and freezer inventory so on-hand ingredients can be deducted from the grocery output, including expiration dates and package quantities. V1 keeps only a reusable staples list, not actual stock.',
    source: 'docs/V2_BACKLOG.md, docs/PRODUCT_SPEC.md §47',
  },
  {
    id: 'serving-scaling',
    title: 'Serving scaling / diner-aware quantities',
    status: 'v2',
    summary:
      'Store a recipe base serving size and automatically scale ingredients to each date’s diner count, with a per-date override. V1 stores diner count as metadata only.',
    source: 'docs/V2_BACKLOG.md, docs/M5_VERIFICATION.md',
  },
  {
    id: 'package-intelligence',
    title: 'Package intelligence',
    status: 'v2',
    summary:
      'Understand package forms such as “2 × 14 oz cans”, jar sizes, and bottle sizes, and generate package-level shopping output like “Buy 2 cans”. V1 deliberately never guesses package-size conversions.',
    source: 'docs/V2_BACKLOG.md, docs/PRODUCT_SPEC.md §47',
  },
  {
    id: 'dietary-scheduling',
    title: 'Dietary scheduling & restrictions',
    status: 'v2',
    summary:
      'Optional dietary randomization preferences (vegan and vegetarian frequency) plus dietary restrictions and allergy exclusions.',
    source: 'docs/V2_BACKLOG.md, docs/PRODUCT_SPEC.md §47',
  },
  {
    id: 'advanced-variety',
    title: 'Advanced variety / randomization rules',
    status: 'v2',
    summary:
      'Configurable same-protein lookback/lookahead distance beyond the direct-neighbor default, plus cuisine, pasta, cooking-method, and grilling frequencies, beef/chicken caps, and meatless targets. M6 was designed so these knobs can expand later.',
    source: 'docs/V2_BACKLOG.md, docs/RANDOMIZER.md §6, docs/M6_PLAN.md §6',
  },
  {
    id: 'seasonal-weighting',
    title: 'Seasonal weighting',
    status: 'v2',
    summary:
      'Seasonal recipes with winter/summer preferences and optional month or season availability.',
    source: 'docs/V2_BACKLOG.md',
  },
  {
    id: 'holiday-models',
    title: 'Holiday models / regional packs',
    status: 'v2',
    summary:
      'Optional selectable regional holiday packs (US, UK, Australia, others). V1 has a holiday entry type with a custom label but no automatic holiday calendar.',
    source: 'docs/V2_BACKLOG.md, docs/PRODUCT_SPEC.md §47',
  },
  {
    id: 'ai-assistance',
    title: 'AI assistance (optional)',
    status: 'research',
    summary:
      'Optional local or OpenAI-compatible providers that could parse ambiguous ingredient strings, suggest mappings, or clean instructions. The core product must stay fully usable without AI and no mandatory external AI dependency may be introduced.',
    source: 'docs/V2_BACKLOG.md, docs/PRODUCT_SPEC.md §47',
  },
  {
    id: 'voice-integrations',
    title: 'Voice integrations',
    status: 'v2',
    summary:
      'Alexa, Google Home, and possibly Home Assistant actions such as asking what is for dinner, adding groceries, reading the grocery list, rerolling a date, or setting a meal. Depends on the external API credentials model.',
    source: 'docs/V2_BACKLOG.md',
  },
  {
    id: 'external-api-credentials',
    title: 'External API credentials / integrations',
    status: 'v2',
    summary:
      'A service-account and API credential model with scoped permissions such as calendar:read, calendar:write, grocery:read, and recipes:read. V1 deliberately exposes no external API credentials.',
    source: 'docs/V2_BACKLOG.md',
  },
  {
    id: 'authentication-2fa-passkeys',
    title: 'Authentication: 2FA / passkeys',
    status: 'v2',
    summary: 'Add TOTP two-factor authentication, with passkeys later.',
    source: 'docs/V2_BACKLOG.md, docs/PRODUCT_SPEC.md §47',
  },
  {
    id: 'scheduled-remote-backups',
    title: 'Scheduled & remote backups',
    status: 'v2',
    summary:
      'User-configurable recurring backups (every N days, weekly or monthly, at a chosen time) with rolling retention that keeps the last X scheduled archives before overwriting the oldest, targeting a local folder, SMB/NFS, S3-compatible storage, OneDrive or Google Drive. V1 (M13) provides on-demand encrypted backup/restore and a fixed last-5 safety-backup retention only.',
    source: 'docs/V2_BACKLOG.md, docs/PRODUCT_SPEC.md §47, product owner 2026-10-22',
  },
  {
    id: 'in-app-changelog-update-notes',
    title: 'In-app changelog & update notes',
    status: 'v2',
    summary:
      'A footer CHANGELOG link opening the full list of changes for every released version, plus a dismissible "What is new" popup on the first load after the version changes that summarises that version. Content comes from the release metadata already fetched by the update check or a bundled changelog (offline-capable, no telemetry). V1 (M13) shows only a dismissible newer-release banner and no changelog surface.',
    source: 'docs/M13_PLAN.md §1.13/§1.14, CHANGELOG.md, product owner 2026-10-22',
  },
  {
    id: 'recovery-key-protection',
    title: 'Recovery-key protection',
    status: 'v2',
    summary:
      'Store recovery.key encrypted at rest and export only an encrypted envelope, so no plaintext key ever leaves the process or lands in a downloaded file. Unlock with a user-supplied passphrase (or an equivalent user-held secret) so a copied file is not enough, while preserving a documented disaster-recovery path for a clean install. V1 (M13) keeps the plaintext /data/config/recovery.key (mode 0600) and an Admin-only guarded download.',
    source: 'docs/M13_PLAN.md §1.11, docs/SECURITY.md §11, product owner 2026-10-22',
  },
  {
    id: 'advanced-duplicate-detection',
    title: 'Advanced duplicate detection',
    status: 'v2',
    summary:
      'Richer duplicate signals for import: ingredient and instruction overlap, title similarity, canonical source URL, and optional AI assistance. V1 has exact normalized-URL and bounded similar-name warnings only.',
    source: 'docs/V2_BACKLOG.md, docs/PRODUCT_SPEC.md §47',
  },
  {
    id: 'statistics-expansion',
    title: 'Statistics expansion',
    status: 'v2',
    summary:
      'Add times planned, average days between repeats, favorite usage, cuisine trends, grocery ingredient frequency, and household preference trends. V1 ships Last Eaten / Next Planned / Times Eaten only.',
    source: 'docs/V2_BACKLOG.md',
  },
  {
    id: 'printing-enhancements',
    title: 'Printing enhancements',
    status: 'v2',
    summary:
      'List-style calendar printing, weekly print, custom grocery print templates, and recipe packet printing. V1 (M10) does landscape monthly calendar printing and the grocery list.',
    source: 'docs/V2_BACKLOG.md',
  },
  {
    id: 'nutrition-macros',
    title: 'Nutrition / macros',
    status: 'v2',
    summary:
      'Calories, macros, nutrition imports, and a dietary summary. Not required by the core planner.',
    source: 'docs/V2_BACKLOG.md',
  },
  {
    id: 'native-apps-pwa',
    title: 'Native apps / PWA',
    status: 'v2',
    summary: 'A PWA install, an iOS/Android wrapper, or a native client using the same API.',
    source: 'docs/V2_BACKLOG.md',
  },
  {
    id: 'multi-household-tenancy',
    title: 'Multi-household tenancy',
    status: 'v2',
    summary:
      'Support more than one household per installation. V1 is single-household with a single household_settings row.',
    source: 'docs/PRODUCT_SPEC.md §47',
  },
  {
    id: 'administration-audit-expansion',
    title: 'Administration & audit expansion',
    status: 'v2',
    summary:
      'Bulk / date-range historical correction; per-capability permission keys (audit.view, history.correct, recipes.delete) so an Admin can delegate admin capabilities to a member; automatic or scheduled audit pruning and recurring audit exports; tamper-evident audit storage beyond the current append-only rows; and a dedicated /admin navigation area. V1 (M12) keeps audit read, historical correction and hard delete Admin-only on the seven-key model, prunes only on an explicit Admin action, and keeps the audit log inside /settings.',
    source: 'docs/M12_PLAN.md §6',
  },
  {
    id: 'import-recipe-enhancements',
    title: 'Import & recipe enhancements',
    status: 'enhancement',
    summary:
      'Populate source.page_title during import, broaden source-URL canonicalization, and extend the similar-name scan beyond its bounded window. These are deliberate V1 limitations that can only miss a warning, never raise a false one.',
    source: 'docs/M4_VERIFICATION.md, docs/M4_PLAN.md §6',
  },
  {
    id: 'calendar-enhancements',
    title: 'Calendar enhancements',
    status: 'enhancement',
    summary:
      'Harden single-date create concurrency, extend undo/redo retention beyond the next modification, enrich historical snapshot metadata, and add automatic holiday packs.',
    source: 'docs/M5_VERIFICATION.md, docs/M5_PLAN.md §6',
  },
  {
    id: 'randomizer-enhancements',
    title: 'Randomizer enhancements',
    status: 'enhancement',
    summary:
      'Add debugging and analysis tooling over the stored operation metadata, plus additional configurable knobs as advanced variety rules land. V1 stores a full score breakdown but exposes only a compact operation to clients.',
    source: 'docs/RANDOMIZER.md §7, docs/M6_PLAN.md',
  },
  {
    id: 'per-user-device-preferences',
    title: 'Per-user & device preferences',
    status: 'v1x',
    summary:
      'Per-user and per-device display preferences: light/dark theme (and follow-system) applied before first paint so there is no flash, a configurable display date format (the UI already renders MM-DD-YY), and other UI/display preferences as they arise. Storage stays in PostgreSQL, never a file: a user-keyed table mirroring user_grocery_preferences, with the single-user system actor falling back to application_settings.',
    source: 'docs/FUTURE_FEATURES.md (raised 2026-10-18), docs/DATABASE.md §11',
  },
  {
    id: 'infrastructure-performance',
    title: 'Infrastructure & performance',
    status: 'research',
    summary:
      'Investigate PostgreSQL full-text search and trigram extensions, and caching such as Redis, only where baseline performance shows a need — plus performance baselines and load testing.',
    source: 'docs/DATABASE.md §16, docs/TESTING.md §12',
  },
  {
    id: 'explicitly-never-in-v1',
    title: 'Explicitly never in V1',
    status: 'never_v1',
    summary:
      'Explicitly rejected for V1: browser automation, third-party JavaScript execution, headless rendering, and CAPTCHA handling during import; automatic self-update; telemetry by default; and mandatory external AI dependencies.',
    source: 'docs/AGENTS.md, docs/M4_PLAN.md §6, docs/PRODUCT_SPEC.md §45',
  },
]
