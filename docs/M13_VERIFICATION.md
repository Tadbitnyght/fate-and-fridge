# M13 Verification — Backup / Restore / Updates

Status: **complete** (2026-10-22) — three live restore round-trips. The first
found two blockers (the missing `backup.restored` audit row and a corrupted
catalog) and the second found a third (the file swap renaming bind-mount roots);
all three are fixed and the third round-trip passed every criterion, including a
fourth cosmetic fix (preserving the live directory mode). Runbook:
`docs/M13_PLAN.md` (approved 2026-10-20). Coverage map:
`docs/M13_TEST_MATRIX.md`. Tracking: #162; M13-01…M13-10: #163–#172.

---

## What shipped

- **Encrypted backups** — one `.ffbackup` archive per backup: a `pg_dump`
  (custom format), the recipe images, the Data Protection keys, and a checksummed
  manifest, sealed with **chunked AES-256-GCM** (1 MiB records, per-record nonce
  and tag, header + record index + final flag bound in the AAD) under a key
  derived by **HKDF-SHA256 from the setup recovery key**. The recovery key is
  **never inside the archive**. A per-archive key-check (derived with the archive
  salt) verifies the key before decryption without becoming a stable fingerprint.
- **Backup management** — `backup_metadata` (one `M13Backups` migration),
  Admin Settings → Backups: list, create, download, delete (typed `DELETE`), with
  the ciphertext checksum and size, `backup.created`/`backup.deleted` audits, and
  last-5 **safety** retention reconciled from disk (manual backups never pruned).
- **Restore** — validate (format/key/integrity/checksum/schema) **before any
  mutation**, mandatory verified safety backup, maintenance window (`503` except
  health), traversal-safe staging extraction, `pg_restore --clean --if-exists
  --single-transaction`, **all sessions revoked**, `backup.restored` written into
  the restored database, images/dp-keys swapped with rollback, marker + graceful
  stop so the container restarts and migrations run. A pre-commit failure leaves
  the data intact; a **post-commit** failure still restarts and records the error.
- **Clean-install restore** — the setup wizard restores before setup from an
  archive plus the pasted recovery key (validated first, adopted after archive/
  schema validation and before the safety backup, never overwriting an existing
  key); `409` once setup is complete.
- **Recovery-key export** — Admin-only attachment download behind a reason +
  typed `EXPORT`, `Cache-Control: no-store`, audited with **no key material**
  anywhere, never rendered inline.
- **Version display** — `GET /about` (authenticated) with the real SemVer and the
  applied migration; Settings → About and a shell footer label.
- **Update check** — one unauthenticated bodiless GET to the public GitHub
  Releases API with only `User-Agent: FateAndFridge/<version>` and
  `Accept: application/vnd.github+json`; SemVer comparison, drafts/prereleases
  ignored, 24 h cache, forced check, `update_check_enabled` honoured (disabled ⇒
  zero HTTP), failures recorded as a short `last_error` and never thrown, a
  non-blocking startup check, a dismissible per-device dashboard notice, and a
  link to the release page. **No telemetry, no self-update.**
- **Runtime image** — `postgresql-client-17` (PGDG, pinned) with build-time
  `pg_dump`/`pg_restore`/`psql` 17.x assertions; the base is Ubuntu 24.04
  (`noble-pgdg`), documented in `docs/DEPLOYMENT.md`.

**No new permission key.** One migration (`backup_metadata`). No new npm/NuGet
dependency, no scheduler, no offsite target, no new infrastructure.

---

## Independent review

Two focused read-only passes (each required to execute the suites itself).

### Archive / backup / restore half — **PASS after fixes**

Verified: HKDF parameters and per-archive salt; AES-256-GCM nonce uniqueness and
the AAD binding header + record index + final flag; truncation at a record
boundary and mid-record, reordering, trailing bytes, header/body/tag tamper all
fail; the key-check cannot leak the key; the recovery key is absent from the
archive and no plaintext leaks; path safety on read and write; manifest checksums
verified; streaming. Backup: `pg_dump` without a shell with `PGPASSWORD` only, the
client≥server refusal, temp-then-rename, lifecycle, checksum/size, the operation
gate, delete guard, and retention. Restore: validate-before-mutate, the mandatory
safety backup, staging containment, single-transaction restore, session
revocation, the audit row in the restored DB, the marker/restart, and the
pre-commit rollback. Pre-setup: the only unauthenticated route, key shape first,
adoption ordering, no overwrite, 409 after setup.

Suites run by the reviewer: `*M13Backup*`, `*M13Restore*`, `*M13SetupRestore*`,
`*M13Pg*`, `*DangerousActionGuard*` → **153 passed, 0 failed, 3 skipped** (the
real-binary paths). Plus format clean, Release build 0/0, no pending model
changes.

### Key export / version / updates half — **PASS conditional, then fixed**

The update-check **privacy claim is proven**: the reviewer inspected the request
construction *and* the handler-level test, which asserts the exact URL, method,
`User-Agent`, `Accept`, an empty body, and the exact header set (`Accept` +
`User-Agent` only). The key export is attachment-only, server-gated, audited
without key material, and never creates the key file. Version display is
authenticated and real, degrading to `null` on an unreadable DB. Permissions and
scope are clean (no eighth key, one migration, no self-update path).

Suites: `*M13RecoveryKeyExport*`, `*M13About*`, `*M13Update*`, `*PermissionMatrix*`
→ **66 passed, 0 failed**; frontend lint/format/typecheck clean, Vitest 542.

### Findings and their disposition

| Severity | Finding | Disposition |
|----------|---------|-------------|
| **major** | After `pg_restore` commits, a later failure (swap/prune/marker) went through the pre-commit `Fail(...)` path and claimed "the original data is intact" while the DB had already been replaced and no restart/migrations happened. | **Fixed** (`304f482`): execution is split at the DB commit; post-commit failures record the error, write the marker, request the graceful stop, reclaim files and return a distinct `AppliedRestartRequired`. |
| **major** | Safety archives were registered in the pre-restore DB, which `pg_restore` replaces, so retention never saw them (one leaked full archive per restore); `.restore/<id>` uploads and `old-*` directories were never reclaimed. | **Fixed** (`30e7408`): safety retention reconciles the directory (newest 5, never manual), and working directories are reclaimed on success and failure while preserving rollback needs. |
| minor | The header key-check used a fixed salt → a stable per-key fingerprint across archives. | **Fixed** (`30e7408`): derived with the archive salt; the plan/spec now state that a flipped key-check/salt reports `BACKUP_KEY_MISMATCH` (it is a key verifier) while all other tamper reports `BACKUP_INTEGRITY_FAILED`. |
| minor | The recovery-key download lacked `Cache-Control: no-store`. | **Fixed** (`e2cf1e5`) + header assertion. |
| minor | The update response-size cap was applied after fully buffering the body and counted UTF-16 chars. | **Fixed** (`e2cf1e5`): `ResponseHeadersRead`, a `Content-Length` pre-check, and a byte-capped read loop; tests cover the byte semantics. |
| minor (a11y) | `aria-describedby` pointed at an element with no `id`. | **Fixed** (`e2cf1e5`) + assertion. |
| nit | Contradictory "checked" and "last check did not complete" messaging after a 200-with-error. | **Fixed** (`e2cf1e5`). |
| nit | No service-level invalid-JSON test; no log-capture assertion for the key. | Accepted; parser-level coverage and the audit-field scan remain. |
| nit (out of scope) | `RerollUndo.test.tsx` (calendar, pre-M13) is an intermittent timeout under load. | Noted as pre-existing; it passes on a clean run. |

---

## Gates and evidence at completion

Frontend gates run from a **repo-shaped** mirror (`frontend/` + `docs/`) so the
registry-parity guard cannot silently skip.

| Gate | Result |
|------|--------|
| `dotnet format FateAndFridge.slnx --verify-no-changes` | clean |
| `dotnet build -c Release FateAndFridge.slnx` | succeeded, 0 warnings / 0 errors |
| `dotnet ef migrations has-pending-model-changes` | no pending model changes |
| Backend tests (full Release suite) | **2487 passed / 0 failed / 3 skipped** (the M9 route guard was fixed earlier in `7616f64`) |
| Vitest | 51 files / 542 tests passed (one earlier run flaked 3 pre-existing timeouts; a clean re-run passed) |
| Playwright (hermetic) | 77 passed / 3 gated stack skips |
| `npm run lint` / `format:check` / `typecheck` | clean |

---

## Live deployment verification

### Production deployment (part A — non-destructive)

Rebuilt/redeployed from the completed tree. Both the workstation and the NAS
checkout were confirmed at **`3dd2842`** (clean). The stack was rebuilt with
`scripts/deploy.sh up` and the **`M13Backups`** migration applied.

- **Image / container:** `fate-and-fridge:0.1.0` =
  `sha256:27d4efe0bcc4778edee3d541902bce6e2d42c40439bd13bdbe3dcb5cfa2a7ef1`
  (local build, no registry digest); app container `5f717f1c427a`, `Up
  (healthy)`, `0.0.0.0:50080->8080`; PostgreSQL `Up (healthy)`, not published.
- **Health:** `GET /api/v1/health` → 200; `GET /api/v1/health/db` → 200.
- **Migration:** `GET /api/v1/about` → `{"app_version":"0.1.0",
  "schema_version":"20261021023131_M13Backups"}` — the M13 migration applied and
  the About read works for an authenticated (single-user) caller.
- **Real `pg_dump` in the image (the path the workstation cannot test):**
  `POST /api/v1/admin/backups` → **201** with
  `{"id":1,"filename":"fatefridge-0.1.0-20261022T012841Z.ffbackup",
  "kind":"manual","status":"completed","size_bytes":815704,
  "checksum":"566cd48dd96facb83dec993b8a86685acf4e71993f4c9b13cfc43f28c535a1c7"}`;
  `GET /api/v1/admin/backups/1/download` → **200**,
  `application/octet-stream`, attachment with that filename, **815,704 bytes**;
  the first bytes are **`FFBK`** (magic + format 0001 + KDF 01 + reserved 00 +
  salt), and the file contains **no** `CREATE TABLE` / `PGDMP` markers and no
  recovery-key text — the payload is genuinely encrypted.
- **Real update check against the public release:** `GET /api/v1/updates` → 200
  `{"current_version":"0.1.0","latest_version":"0.1.0",
  "update_available":false,"last_checked_at":"2026-10-22T01:25:19.5588166+00:00",
  "last_error":null,"enabled":true,
  "release_url":"https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v0.1.0"}`
  — the unauthenticated GitHub Releases call works on the real network, SemVer
  matches, and no banner is shown.
- **`/progress`** rendered live in Chromium: **`14 of 15 milestones complete
  (93%).`**, the **`M13 — Backup / Restore / Updates`** card shows **Complete**
  and `Completed 2026-10-21`, no milestone reports "In progress"; the served
  bundle is `index-CsNOEZsb.js`.
- The production archive is left in `/data/backups` as evidence; nothing was
  restored, pruned or deleted on production.

### Real restore round-trip (part B — disposable stack) — blockers found, fixed

A throwaway compose project (`faf-m13-verify`, own data dir, port 50081, own
image tag) proved the real `pg_restore` path end to end. The data outcome was
correct — the app came back healthy, migrations ran, the post-backup recipe was
gone, only the backup's data remained, `setup/status` was completed, the safety
archive existed on disk, `.restore/` was reclaimed and `config/recovery.key`
survived — **but two blocker defects were exposed**, which is exactly why the
workstation-only suite was not sufficient:

1. **`backup.restored` is never written (audit gap).** `PgRestoreRunner` terminates
   **all** sessions on the target database — including the application's own
   pooled connection — so the subsequent `DELETE FROM user_sessions` fails with
   **`57P01: terminating connection due to administrator command`**. That aborts
   the post-commit path before `WriteRestoreAuditAsync`, so no `backup.restored`
   audit row exists (the API still returned `202` and the app restarted, so it
   looked successful). Destructive operations must be audited (Definition of Done).
   The terminate count is also wrong (`Terminated -1 session(s)`).
2. **The backup catalog is corrupted after a restore.** `pg_dump` snapshots
   `backup_metadata` while the manual row is still `pending`/`size_bytes 0`/empty
   checksum (it is marked `completed` after the dump finishes), and the safety
   backup row is inserted after the dump — so restoring yields a **stale `pending`
   row** and **no safety row**, while the disk holds two valid archives. The
   catalog must be reconciled from the on-disk archives/manifests after a restore.
3. **The image / Data-Protection-key swap always fails.** `RestoreService` swapped
   `images/` and `config/dp-keys/` with `Directory.Move(live, old)` followed by
   `Directory.Move(staged, live)`. In the container both live paths are
   **bind-mount roots** (`/data/images`, `/data/config`), and the kernel refuses
   to rename a mount root (`EXDEV` / "Resource busy") even on the same
   filesystem. Every restore therefore restored the database and reported the
   *file swap* as failed: the rollback restored the originals and the marker
   recorded `post_step_error`, but the images and the Data Protection key ring
   were **never actually replaced** from the archive.

Production was verified untouched before and after (same archive, checksum, size;
`safety/` empty; both containers healthy), and the disposable stack and its image
were torn down (no containers/networks/volumes remain; port 50081 refused).

**Fixes (all three blockers closed in code):**

1. **Audit guaranteed + fresh connections + real count.** `PgRestoreRunner` no
   longer counts an `ExecuteNonQuery` (which reported `-1`): it reads one row per
   terminated backend, so the log states the real number, and it keeps excluding
   its own maintenance backend. After termination and again when `pg_restore`
   returns it calls `NpgsqlConnection.ClearAllPools()`, so no post-commit step can
   reuse a terminated backend. `RestoreService` runs the session revoke and the
   `backup.restored` insert through a fresh-connection retry (once on
   `57P01`/`57P02`/`57P03`/`08…`/transient/timeout); the audit is attempted even
   if the revoke fails, and a step that still fails is recorded as
   `post_step_error` in the marker while the application still restarts.
   `M13RestoreServiceIntegrationTests` now reproduces the terminated pooled
   connection with a real `pg_terminate_backend` and asserts the audit row is
   still written.
2. **Catalog reconciled from disk.** `BackupService.ReconcileCatalogAsync`
   enumerates `backups/*.ffbackup` (manual) and `backups/safety/*.ffbackup`
   (safety), upserts one `completed` row per filename with the real size and
   streamed SHA-256 of the encrypted file, marks non-pending rows whose file is
   gone `deleted`, never deletes a file, and is idempotent. It runs after a
   successful restore (before the marker) and once at startup when the marker is
   consumed. Tests cover the pending manual row, a safety file without a row, an
   orphan row, and the idempotent second pass.

3. **Copy-based image/config swap (bind-mount roots cannot be renamed).** The
   second round-trip found the swap calling `Directory.Move` on the live
   `images/`/`config` bind-mount roots, which the kernel rejects (`EXDEV`/
   "Resource busy") even on the same filesystem — so every restore reported a
   post-commit failure and never replaced the images/Data-Protection keys. The
   swap now snapshots the target **contents** into `.restore/<id>/old-*` by
   recursive copy, clears the target's children (the mount root itself stays),
   copies the verified staging content in, and rolls back by copying the snapshot
   back; `config/recovery.key` is never touched. `IRestoreFileOperations` exposes
   **no rename/move operation at all**, and the tests include a success case
   (stale files removed, key byte-identical, snapshots reclaimed), a mid-copy
   failure that restores the original contents and reports the post-commit error,
   and a mount-root case where the live tree sits on a different filesystem so a
   rename would fail with `EXDEV`.

**Second round-trip (after the audit + catalog fixes):** the audit row, the real
terminate count, the catalog (manual `completed` with the on-disk checksum, safety
row present, no stale `pending`), the restart, migrations, session revocation and
`.restore` reclamation all **passed** — and it exposed defect 3 above, which the
copy-based swap now fixes.

**Disposition:** **PASS.** The third round-trip on a disposable stack proved every
criterion: healthy restart (`RestartCount 1`); `schema_version` =
`20261021023131_M13Backups`; only the backup's recipe remained; **`images/recipes/marker-keep.txt`
was restored with the content `keep` and the post-backup `images/stale.txt` was
gone** (the copy-based swap replaces contents); exactly **one** `backup.restored`
audit row with the restore id, backup filename and safety filename; the catalog was
coherent (manual `completed` with checksum `096d3c8a…` matching the file on disk,
safety `completed` with `fe10b33d…` matching its file, **no stale `pending` row**);
`.restore/` was empty and the marker cleared; `config/recovery.key` was intact
(43 bytes, mode 0600); the log had **zero** `post-commit step failed` /
`could not be swapped` lines and showed `Terminated 1 session(s)`,
`Reconciled the backup catalog after the restore: … 1 added, 1 updated`,
`Wrote the restore marker`, `requesting a graceful application stop`, and after the
restart `Reconciled the backup catalog from disk after the restore: … 0 added, 0 updated`
and `the restore marker was cleared`. Production was verified untouched (same
single archive, same checksum, both containers healthy) and the disposable stack,
network, image and data dir were torn down.

The round-trip also surfaced a fourth, cosmetic issue — the swap adopted the
archive's directory mode on the live root (`0777 → 0755`), which matters because
the deployment's `data/images` is a world-writable SMB share. Fixed in `ff4a03c`:
the live root's mode is captured, re-applied after the copy, and restored on
rollback; tests cover both the success and rollback paths.

**Final M13 restore verdict: PASS.**

---

## CI status after completion

_Pending._
