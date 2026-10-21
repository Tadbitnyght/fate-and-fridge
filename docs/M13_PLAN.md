# Fate & Fridge — Milestone 13 Plan (Backup / Restore / Updates)

Status: **approved, ready to execute**
Approved: 2026-10-20 (product owner answered the full M13 decision set)
Purpose: authoritative M13 runbook. A fresh session should execute M13 from this
file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product, API,
architecture, database, security, and testing specifications without asking
further product questions. Where this file and an older spec disagree, escalate
to the Lead before implementing.

---

## 1. Locked product decisions

1. **Permissions — no new keys.** Backup, restore, recovery-key export, and the
   update settings are **Admin-only** on the existing seven keys
   (`docs/SECURITY.md` §4). The server is the sole authority; the UI hides what
   the caller cannot do.
2. **Manual, on-demand backups only.** No scheduler, no offsite target, no cloud
   SDK. Scheduled/remote backups remain **V2**
   (`docs/FUTURE_FEATURES.md` → "Scheduled & remote backups").
3. **Database mechanism.** Add `postgresql-client-17` to the runtime image and
   use **`pg_dump --format=custom`** / **`pg_restore`**. A startup check records
   the client/server majors and **refuses a backup** if the client major is older
   than the server's (a newer client is fine).
4. **Archive format.** One file, `*.ffbackup`:
   - a small cleartext **header** (magic `FFBK`, format version, KDF id, salt,
     chunk size), then
   - a **chunked AES-256-GCM** stream (1 MiB chunks, per-chunk nonce, tag, and
     chained AAD including the header and chunk index, so truncation/reordering/
     tampering is detected), keyed by **HKDF-SHA256** from the 32-byte recovery
     key;
   - the plaintext is a **tar stream** containing `database.dump`, `images/…`,
     `config/dp-keys/…`, and `manifest.json` (checksums, app version, schema
     version, created_at, counts, format version);
   - **the recovery key is never inside the archive** — a backup is useless
     without it, by design.
5. **Backup contents:** database dump, `recipe images`, the Data Protection keys
   (`/data/config/dp-keys`), and the manifest. **Excluded:** `recovery.key`,
   logs, other backups, and anything outside `/data/{config,images}`.
6. **Storage and naming.** Archives live in the mounted `/data/backups`
   directory (`fatefridge-<appversion>-<UTC yyyyMMddTHHmmssZ>.ffbackup`); safety
   backups created by a restore live in `/data/backups/safety/`. Persistent data
   never depends on the container filesystem.
7. **Metadata.** Add the specified **`backup_metadata` table** (DATABASE §12)
   plus a `kind` column (`manual` | `safety`) and a `status`
   (`pending` | `completed` | `failed` | `restored` | `deleted`): filename,
   app_version, schema_version, created_at, created_by_user_id, checksum
   (SHA-256 of the encrypted file), size_bytes, kind, status, error. One
   migration (`M13Backups`); no other schema change.
8. **Retention.** Keep the **last 5 safety backups** (pruned oldest-first after a
   successful restore) and **never auto-delete a manual backup**; manual
   deletion is an explicit, confirmed Admin action that also removes the file and
   marks the row `deleted`.
9. **Restore — in-app, then restart.** Admin validates and selects an archive
   (upload **or** a file already in `/data/backups`; uploads capped at **2 GB**),
   confirms through the shared dangerous-action dialog, and the app:
   1. validates the header/format version, the GCM auth, the checksum, and the
      archive's schema version (newer than the running app → refuse);
   2. **creates a safety backup first** and verifies it completed;
   3. enters a **maintenance mode** that rejects ordinary API traffic;
   4. extracts to a **staging directory**, rejecting absolute paths and `..`
      entries;
   5. terminates other DB connections and runs `pg_restore --clean --if-exists
      --no-owner --no-privileges` into the existing database;
   6. replaces `images/` and `config/dp-keys/` atomically (staging → rename);
   7. **deletes all `user_sessions`** (every client must sign in again) and
      writes a `backup.restored` audit row **into the restored database**;
   8. writes a restore marker and **exits(0)** so `restart: unless-stopped`
      restarts the container; on startup the app applies migrations, verifies
      health, clears the marker, and logs the outcome.
   A failed restore leaves the original data and the safety backup intact.
10. **Clean-install restore.** The setup wizard can restore **before setup
    completes** using an uploaded archive **plus a pasted recovery key**; the
    restore runs, then the app restarts into the restored (already set-up) state.
    Only the restore endpoints bypass the `SETUP_REQUIRED` gate for this flow,
    and they require the archive to decrypt with the supplied key.
11. **Recovery-key export.** Admin-only download of the key file, gated by the
    shared **dangerous-action dialog** (reason + typed `EXPORT`), audited
    (`recovery_key.exported`), and **never rendered inline**. A single display at
    setup completion remains the only other exposure point.
12. **Version display.** `Settings → About` shows the app version
    (`AppInfo.Version`, SemVer only), the schema/migration version, the update
    check state, and the last-checked time; a small version label also appears in
    the shell footer.
13. **Update check.** Reads the **public GitHub Releases API**
    (`/repos/Tadbitnyght/fate-and-fridge/releases/latest` — exact owner per the
    repository URL in `milestones.ts`), **unauthenticated**, with a neutral
    `User-Agent: FateAndFridge/<version>`; sends nothing else (no household data,
    no identifiers). Results are cached in `application_settings` for **24
    hours**; the check runs **non-blocking on startup** and on an explicit
    **"Check now"**. **Drafts and prereleases are ignored**; comparison is
    **SemVer**. Offline/HTTP failure is tolerated, recorded, and never surfaces as
    an app error. The setup preference `update_check_enabled` is honoured (and
    editable in Settings).
14. **Update notice.** A dismissible dashboard banner when a newer version
    exists, plus the About section's status. No self-update: the notice explains
    how to update the deployment (rebuild/redeploy), never performs it.
15. **Release versioning.** Adopt SemVer **`vX.Y.Z` tags + GitHub Releases** with
    notes. Publish `v0.1.0` (covering M0–M12) once the repository is public, and
    bump per milestone from M13 (e.g. `v0.2.0`). The app compares SemVer and
    ignores drafts/prereleases.
16. **Repository visibility.** The owner flips the repository **public** after
    reviewing the Lead's pre-publication secret scan; the update check is only
    live-verifiable after that. Until then, the implementation and tests use a
    mocked HTTP handler and the check degrades gracefully.
17. **Audit + live sync.** New audit event types: `backup.created`,
    `backup.restored`, `backup.deleted`, `recovery_key.exported`, and
    `settings.update_check_changed` (the `backup.created`/`backup.restored`
    stubs from M12 are wired here). **No live-sync event** is added: a restore
    restarts the process, and backup/update operations are Admin-local.
18. **No telemetry, no self-update, no external AI, no new infrastructure**
    (no worker/queue/Redis). The only outbound call is the release metadata GET.

---

## 2. Architecture and contracts

### 2.1 Backend layout (`src/FateAndFridge.Api/`)

- `Backup/BackupArchive.cs` — the streaming writer/reader (header, chunked
  AES-256-GCM, HKDF, tar entries, manifest, path-traversal guards).
- `Services/BackupService.cs` — create (pg_dump + tar + encrypt), list,
  download, delete, retention, and the `backup_metadata` lifecycle.
- `Services/RestoreService.cs` — validation, safety backup, maintenance mode,
  staging/extraction, `pg_restore`, image/config swap, session revocation, audit,
  marker + exit.
- `Services/UpdateCheckService.cs` — cached release metadata, SemVer comparison,
  offline tolerance, `last_checked`/`latest_version`/`last_error` settings.
- `Services/PgToolRunner.cs` — invokes `pg_dump`/`pg_restore` with
  `PGPASSWORD` via the configured connection, bounded timeouts, no shell
  interpolation, and a client/server major check.
- `Endpoints/BackupEndpoints.cs` — create/list/download/delete/restore
  (Admin-only).
- `Endpoints/UpdateEndpoints.cs` — status, "check now", and the enabled toggle
  (Admin-only).
- `Endpoints/SetupEndpoints.cs` — the clean-install restore route (pre-setup,
  key-gated).
- `Endpoints/AboutEndpoints.cs` (or extend health/settings) — app + schema
  version.
- `Services/RecoveryKeyStore.cs` — add the export read path (no new storage).

### 2.2 Contracts (recorded in `docs/API.md` before completion)

- `GET /api/v1/admin/backups` (Admin) → rows from `backup_metadata` with
  `filename, kind, status, app_version, schema_version, created_at, size_bytes,
  checksum`.
- `POST /api/v1/admin/backups` (Admin) → body `{ reason }` (audited) → **`201`
  Created** with the **completed** row (the create is synchronous; a failure
  returns `500 BACKUP_FAILED`, marks the row `failed` and leaves no partial
  archive); the archive is written to `/data/backups` with its checksum.
- `GET /api/v1/admin/backups/{id}/download` (Admin) → the encrypted file
  (`application/octet-stream`, attachment disposition).
- `DELETE /api/v1/admin/backups/{id}` (Admin) → body `{ reason, confirmation }`
  (typed `DELETE`) → removes the file + marks the row `deleted`.
- `POST /api/v1/admin/backups/restore` (Admin) → multipart upload or
  `{ backup_id }`, plus `{ reason, confirmation }` (typed `RESTORE`) → validates,
  creates the safety backup, returns `202` with the restore id, and the process
  enters maintenance and exits when done. Errors: `VALIDATION_FAILED`,
  `BACKUP_NOT_FOUND`, `BACKUP_FORMAT_UNSUPPORTED`, `BACKUP_INTEGRITY_FAILED`,
  `BACKUP_KEY_MISMATCH`, `BACKUP_SCHEMA_TOO_NEW`, `RESTORE_IN_PROGRESS`.
- `POST /api/v1/setup/restore` (pre-setup only) → multipart archive + recovery
  key → same validation path, then restart; `409 SETUP_ALREADY_COMPLETE` after
  setup.
- `GET /api/v1/admin/updates` (Admin) → `{ current_version, latest_version,
  update_available, last_checked_at, last_error, enabled }`;
  `POST /api/v1/admin/updates/check` → forces a check;
  `PATCH /api/v1/admin/updates` → `{ enabled }`.
- `DELETE /api/v1/admin/backups/...`/restore use the shared error envelope and
  snake_case JSON; all routes are `RequireAdmin()`.

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable | Depends on |
|---|-------|-------------|-----------|
| M13-01 | Runtime pg tooling + `backup_metadata` schema | `postgresql-client-17` in the runtime image, client/server major check, `backup_metadata` entity/configuration + `M13Backups` migration + model tests | — |
| M13-02 | Archive format | streaming chunked AES-256-GCM + HKDF writer/reader, tar container, manifest/checksums, path-traversal rejection, round-trip/tamper/wrong-key/truncation/reorder tests | M13-01 |
| M13-03 | Backup service + API | pg_dump capture, archive creation, `/data/backups` storage, `backup_metadata` lifecycle, list/download/delete endpoints, audit, last-5 safety retention | M13-01, M13-02 |
| M13-04 | Restore backend | validation (format/auth/checksum/schema), mandatory safety backup, maintenance mode, staging extraction, `pg_restore`, image/config swap, session revocation, audit, marker + restart, post-start verification | M13-03 |
| M13-05 | Clean-install restore | pre-setup restore endpoint + wizard step (upload + pasted key), `SETUP_REQUIRED` exception scoped to it, restart into the restored state | M13-04 |
| M13-06 | Backup/restore UI | Admin Settings → Backups: list (kind/status/size/date/checksum), create with reason, download, delete and restore through the dangerous-action dialog, progress/error states, upload (2 GB cap) | M13-03, M13-04 |
| M13-07 | Recovery-key export | Admin download endpoint + UI with reason and typed `EXPORT`, audited, never inline | M13-03 |
| M13-08 | Version display | About section (app + schema version, versions endpoint) and the shell footer label | — |
| M13-09 | Update check + notice | GitHub Releases client (SemVer, drafts/prereleases ignored, neutral UA), 24 h cache, startup + "Check now", `update_check_enabled`, offline tolerance, dashboard banner, tests with a mocked handler | M13-08 |
| M13-10 | M13 verification sweep | independent review, docs (`API.md`, `SECURITY.md`, `DATABASE.md`, `TESTING.md`, `CHANGELOG.md`), manifest M13 `complete`, rebuild/redeploy + live gate, tag `m13-complete`, close the tracking issue | M13-01…M13-09 |

Milestone mapping: 1→M13-08; 2→M13-07; 3→M13-02/M13-03; 4→M13-04; 5→M13-03;
6→M13-03; 7→M13-04; 8→M13-04; 9→M13-05; 10→M13-04; 11→M13-08; 12→M13-09.

---

## 4. M13 verification battery

- **Archive integrity:** round-trip equality (DB dump, images, dp-keys, manifest);
  a wrong recovery key → `BACKUP_KEY_MISMATCH`; a single flipped byte anywhere
  (header, chunk data, tag, trailer) → `BACKUP_INTEGRITY_FAILED`; a truncated or
  reordered-chunk archive fails; a tar entry with `../` or an absolute path is
  rejected; an unsupported format version → `BACKUP_FORMAT_UNSUPPORTED`; the
  recovery key is **never** present in the archive; no plaintext DB/images leak
  into the file (entropy/pattern assertion).
- **Backup:** a backup produces a row + file with the right checksum/size/status;
  the DB dump matches the live schema; `pg_dump` refuses when the client major is
  older than the server; a failure leaves `status = failed` with an error and no
  partial archive presented as complete; `/data/backups` is the only location.
- **Retention/deletion:** after a restore only the last 5 safety backups remain;
  a manual backup is never auto-pruned; manual delete removes the file and marks
  the row `deleted`, requiring the typed confirmation.
- **Restore:** validation happens **before** any mutation; a safety backup exists
  and is verified before the DB is touched; a failed restore leaves the original
  data intact and the app usable; on success the DB matches the archive, images
  and dp-keys are replaced, **all sessions are revoked** (an old cookie is
  rejected), migrations run on the next start, and health is green; the
  `backup.restored` audit row exists in the restored database; a schema-newer
  archive is refused; the maintenance window rejects ordinary API traffic.
- **Clean-install restore:** pre-setup restore with the correct key succeeds and
  the app restarts into a set-up state; a wrong key is refused; after setup the
  pre-setup route returns `SETUP_ALREADY_COMPLETE`/is unreachable.
- **Permissions:** every route Admin-only (anonymous 401, member 403, Admin and
  single-user 200); the pre-setup restore route is reachable only before setup;
  **no eighth permission key**; the UI hides what the caller cannot do.
- **Recovery-key export:** Admin-only, gated by the typed `EXPORT` confirmation,
  audited, and never returned by any other endpoint or rendered inline; the key
  never appears in logs or audit payloads.
- **Version/updates:** the About section shows the real app + schema version; the
  update check parses GitHub release JSON, ignores drafts/prereleases, compares
  SemVer correctly (incl. `0.9.0 < 0.10.0`), caches for 24 h, tolerates 404/5xx/
  timeout/offline, sends only the neutral UA (assert no extra headers/body), is
  disabled when `update_check_enabled = false`, and shows/dismisses the banner.
- **No telemetry / no self-update:** no other outbound call exists; nothing
  updates the application.
- **Performance/robustness:** a multi-hundred-MB archive streams without loading
  into memory; the backup/restore operations respect bounded timeouts; concurrent
  backup requests are serialized; a second restore while one is in progress is
  refused (`RESTORE_IN_PROGRESS`).
- **UI:** the Backups section is keyboard-accessible, mobile-usable, and uses the
  shared dangerous-action dialog; error/empty states are handled; the About and
  update surfaces are readable and not colour-only.

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, the M13 section of `docs/MILESTONES.md`, and the
   issue before each issue. Decisions in §1 are final; contradictions stop
   execution and escalate to the Lead.
2. Roslyn MCP first for all C# work: pass the **absolute**
   `/mnt/y/backend/src/FateAndFridge.Api/FateAndFridge.Api.csproj` as
   `projectPath`; `filePath` resolves against `/mnt/y/backend`. Confirm
   `is_msbuild_workspace: true`. Never use `String.Normalize` (invariant
   globalization).
3. Frontend gates run from a **repo-shaped** mirror (`/tmp/opencode/frontend` **and**
   `/tmp/opencode/docs`) with a real local `node_modules`; never commit from the
   mirror.
4. Backend gates: `dotnet format FateAndFridge.slnx --verify-no-changes`, Release
   build, Release tests via the test binary with `-class` filters (never
   `dotnet test`), and
   `dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`.
5. The reviewer independently executes the battery, and specifically probes the
   archive (wrong key, tamper, traversal), the restore safety path (failure
   leaves data intact), session revocation, the pre-setup gate, the export
   leakage paths, and the update-check privacy (no telemetry beyond the GET).
6. At M13 start, set M13 `in_progress` in the manifest and create issues. At
   M13-10, all gates must pass **and CI must be green**, then delegate a
   rebuild/redeploy to `docker-admin` and verify the live instance (healthy;
   create and download a backup; the About/version section; the update check with
   the public repo; `/progress` shows M13 complete) before marking M13 `complete`,
   tagging `m13-complete`, pushing, and closing the tracking issue. See
   `AGENTS.md` → "Milestone completion gate".

### 5.1 Execution cadence

Deliverable-sized sessions with a **checkpoint commit after every verified
deliverable**; never leave more than one unverified unit in the working tree.

---

## 6. External prerequisites (owner actions)

1. **Review the pre-publication secret scan** (Lead-provided) and flip the
   repository visibility to **public**.
2. After visibility changes, publish **`v0.1.0`** (GitHub Release covering
   M0–M12) and, at M13 completion, **`v0.2.0`**. The app's update check is only
   live-verifiable once the repo is public and a release exists; until then it is
   covered by mocked tests.

---

## 7. Explicitly deferred from M13 (already tracked as V2)

- Scheduled backups and remote/offsite targets (SMB/NFS, S3, OneDrive, Google
  Drive) — `docs/FUTURE_FEATURES.md` → "Scheduled & remote backups".
- Automatic application self-update — explicitly never in V1.
- Cloud/telemetry-based update analytics.
- Multi-user backup delegation (per-capability permission keys) — V2.
- Restore from a remote URL.
