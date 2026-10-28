# Changelog

All notable changes to Fate & Fridge are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/).

## [1.0.1] - 2026-10-28

Release packaging only: the published tree contains no development-process
references. No functional change.

### Changed

- Removed development and release-tooling vocabulary, section citations, and
  release identifiers from source comments, test names, and the published
  documentation. Public behaviour is unchanged.

## [1.0.0] - 2026-10-26

The first release: a self-hosted household dinner planner with a randomized
dinner calendar, recipe management with URL import, live grocery lists,
statistics, printing, multiuser permissions, and encrypted backup/restore.

### Added

- **Dinner calendar** — month grid on desktop and Month/Agenda views on mobile,
  with a day-detail editor for the meal, notes, and diner count. Eight entry
  types (recipe, eat out, fend for yourself, host family dinner, go to family
  dinner, holiday, misc, vacation) with the optional details each type allows
  (restaurant, host/location, custom label, notes), a per-date diner count
  (default 4), lock/unlock of individual dates, and a non-blocking repeat
  warning when the same recipe is planned twice in a row.
- **Historical integrity** — past dates are server-enforced read-only in normal
  use, with a frozen last-historical boundary that survives timezone changes.
  Recipe renames refresh today's and future entries while historical entries
  keep their stored snapshot text; retiring or deleting a recipe never loses
  history. Admins can correct a single past date through a reason-and-typed-
  confirmation workflow that writes both a history row and an audit entry.
- **Randomizer** — the "what's for dinner?" engine. Single-date reroll and
  range reroll (next 7 days, rest of month, fill empty days) with a per-date
  preview diff, "try another", apply, and undo. Only `Normal` recipes are
  eligible; exact repeats are heavily suppressed in a configurable 7-day-back /
  14-day-forward window, a shared primary protein on a neighbouring day is
  penalised, greater spacing from other appearances is preferred, and
  favourites act only as a weak tie-breaker. Ten scoring settings are
  Admin-configurable, selection uses cryptographically secure randomness, and
  each reroll stores its score breakdown so results can be explained. Locked and
  vacation dates are left alone, and dates outside the selected range are never
  touched.
- **Recipes** — photo-card grid and compact list with server-side search,
  filters (status, favourite, vegan, vegetarian, protein, tag), sorts, and
  paging. Full editor with lifecycle status (normal, manual-only, retired),
  favourites, vegan/vegetarian flags, local images with required alt text,
  ordered ingredient groups with precise nullable quantities and optional units,
  preparation notes, ordered instruction sections and steps, primary proteins,
  and tags. Deletes are guarded by reference checks; retiring keeps history.
- **Recipe URL import** — schema.org Recipe JSON-LD first, best-effort HTML
  parsing as a fallback (third-party JavaScript is never executed), and an
  editable multi-candidate review draft that only becomes a recipe when saved.
  Deterministic quantity, fraction, unit, and alias normalization; unknown
  ingredients and units resolved during review with permission-gated inline
  creation; duplicate warnings on an exact normalized source URL or a similar
  name; per-user throttling on page and image fetches.
- **Groceries** — a live projection of planned meals over the active range plus
  manual items and staples. Range presets (next 3, 7, or 14 days, rest of month)
  and custom ranges, with the last-used range remembered per user. Lines
  aggregate by canonical ingredient and unit dimension: compatible units combine
  through the configured conversion factors, incompatible measurements stay
  separate, and package sizes are never guessed. Department grouping in
  configured order, per-meal attribution, range-scoped manual items, a
  household-wide staple catalog, and shared check state that survives quantity
  changes and range expansion.
- **Printing** — a deep-linkable calendar print view (A4 landscape, one month
  per page, any month including past months, full name wrapping with a
  deterministic legibility floor) and a grocery list print view (A4 portrait,
  department order, paper checkboxes, attribution, checked items excluded by
  default). Print output is black on white and legible in greyscale.
- **Statistics and history** — most-eaten and least-recently-eaten recipes,
  meal counts for the household month and year, a primary-protein distribution
  with an explicit "no primary protein" figure, and counts for all seven
  special-event types. A read-only, month-grouped meal-history browser with
  entry-type, recipe, and protein filters, plus last-eaten / next-planned /
  times-eaten metrics on every recipe card. Hard-deleted recipes keep their
  meals as stored-snapshot tombstones.
- **Multiuser households** — a first-run setup wizard (single-user or multiuser,
  household details, first Admin, optional IP allowlist), Admin and Member
  roles, and seven server-enforced permission keys (calendar view/edit,
  groceries view/edit, recipes view/edit, reference-data edit) with the
  edit-implies-view rule. User management with last-Admin protection, forced
  password change, Admin password reset, and a one-way single-user → multiuser
  conversion.
- **Authentication and session security** — Argon2id password hashing, opaque
  database-backed sessions with idle and absolute timeouts, rotation on login,
  password change, and privilege change, per-session revocation, CSRF tokens on
  unsafe requests, login lockout with progressive delay, per-IP failure
  limiting, and trusted-proxy aware forwarded headers.
- **Reference data** — household-managed departments, units with conversion
  factors and aliases, ingredients with aliases and merge redirects, primary
  proteins, and tags, all seeded with sensible defaults and protected by
  usage checks that block destructive deletes while references exist.
- **Audit log** — a newest-first merged timeline of audit events and reroll
  records with event-type, entity, actor, source, and date filters, keyset
  paging, expandable before/after detail, CSV/JSON export of the filtered view,
  and a configurable age-based retention window with an Admin-confirmed prune.
- **Backups and restore** — manual encrypted `.ffbackup` archives containing the
  database dump, recipe images, data-protection keys, and a checksummed manifest,
  sealed with chunked AES-256-GCM under a key derived (HKDF-SHA256) from the
  setup recovery key, which is never stored inside the archive. Archived,
  validated, listed, downloaded, and deleted from Settings → Backups, with the
  five most recent safety backups retained. Restore validates format, key,
  integrity, checksum, and schema before mutating anything, takes a mandatory
  safety backup, enters a maintenance window, extracts with path-traversal
  guards, restores in a single transaction, revokes all sessions, and records
  the restore. A clean install can be restored from an archive plus the
  recovery key, and the recovery key is exportable only through a reason and
  typed confirmation.
- **Live sync** — an authenticated WebSocket hub that notifies connected devices
  after committed writes (calendar, lock changes, groceries, recipes, settings,
  session revocation) so every open tab stays current, with automatic reconnect,
  a non-blocking connection indicator, and no entity bodies on the wire.
- **About and update checks** — an About panel with application and schema
  versions, and a privacy-preserving update check that reads the public release
  feed with a single unauthenticated request, caches the result for a day,
  tolerates being offline, and never self-updates.
- **Deployment** — a single application image (API plus built frontend) and a
  Docker Compose stack with PostgreSQL 17 on a private network, bind-mounted
  persistent data (`postgres/`, `config/`, `images/`, `backups/`), automatic
  migrations at startup, health endpoints, and an example environment file.
- **Accessibility and mobile** — skip links, route-change focus management,
  focus-trapped dialogs, labelled controls, mobile-first layouts for the
  calendar and grocery list, and an automated axe WCAG 2.1 AA gate in CI.

### Changed

- Product UI dates render as **MM-DD-YY** for readability, through one shared
  formatter; the API wire format remains ISO `yyyy-MM-dd` and stored values are
  unchanged.
- The default published port is **50080** (`HOST_PORT`); the container still
  listens on 8080.
- The frontend is built and served by the backend in one image, so a deployment
  is a single container plus PostgreSQL rather than a separate web server.
- Settings are grouped into Household, Reference data, Users, Security, Audit,
  Backups, About, and Account sections, with server-side authorization on every
  write.

### Fixed

- Changing a recipe's status no longer reports a spurious error.
- Saving a recipe returns to the recipe list instead of leaving the form open.
- Recipe cards respond across their whole clickable area rather than only the
  title.
- Grocery line labels read correctly for unitless and same-unit quantities.

### Security

- Every write endpoint enforces permissions server-side; the frontend never
  decides access.
- Historical dates are immutable without the audited Admin correction workflow.
- Recipe import is SSRF-hardened: HTTP/HTTPS only, DNS revalidated per hop,
  manually re-validated redirects, bounded timeouts and response sizes, no
  credentials or cookies, and loopback/private/link-local/CGNAT/metadata ranges
  blocked.
- Backup archives are authenticated encryption end to end; truncation,
  reordering, and tampering fail closed, and unsafe archive paths are rejected
  on read and write. Restore refuses to start on an invalid archive.
- Audit export neutralises spreadsheet formula injection, and audit records
  never contain passwords, reset secrets, session tokens, or the recovery key.
- The runtime image ships a PostgreSQL client matching the server version so a
  backup can never silently break, and CI scans dependencies and the built image
  for known vulnerabilities.
- No telemetry. The only outbound request is the optional, identifier-free
  release check.

[1.0.1]: https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v1.0.1
[1.0.0]: https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v1.0.0
