# M13 Verification — Backup / Restore / Updates

Status: **in progress** (live deployment gate pending)
Runbook: `docs/M13_PLAN.md` (approved 2026-10-20). Coverage map:
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
| Backend tests (full Release suite) | **2481 passed / 0 failed / 3 skipped** (the one failure in the first full run was the M9 live-sync route guard, fixed in `7616f64` and re-run green) |
| Vitest | 51 files / 542 tests passed (one earlier run flaked 3 pre-existing timeouts; a clean re-run passed) |
| Playwright (hermetic) | 77 passed / 3 gated stack skips |
| `npm run lint` / `format:check` / `typecheck` | clean |

---

## Live deployment verification

_Pending: a rebuild/redeploy from the completed commit, then (a) non-destructive
checks on the production instance (health, About/version, the update check against
the real public release, `/progress`), and (b) the real `pg_dump`/`pg_restore`
round trip on a **disposable** stack (create → download → restore → restart →
migrations → sessions revoked → `backup.restored`), because the client binaries
cannot be exercised on the workstation._

---

## CI status after completion

_Pending._
