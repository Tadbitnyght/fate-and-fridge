# M13 Test Matrix — Backup / Restore / Updates

Maps every `docs/M13_PLAN.md` §4 battery bullet to its primary automated test(s).
The independent verdicts and the live-deployment evidence live in
`docs/M13_VERIFICATION.md`.

Legend: **API** = xUnit suite in `backend/tests/FateAndFridge.Api.Tests/`;
**Vitest** = frontend component suite; **e2e** = hermetic Playwright spec.

| §4 area | Primary coverage |
|---------|------------------|
| Archive round-trip; manifest equality; streaming | `M13BackupArchiveTests` (binary/empty/multi-chunk entries, manifest checksums, 64 MiB streamed round-trip) |
| Wrong key | `M13BackupArchiveTests` (`KEY_MISMATCH`, no partial plaintext) |
| Tamper: key-check/salt vs everything else | `M13BackupArchiveTests` (flipped key-check/salt → `KEY_MISMATCH`; header fields/ciphertext/tag → `INTEGRITY_FAILED`) |
| Truncation, reordering, trailing bytes | `M13BackupArchiveTests` (chunk-boundary and mid-chunk truncation, swapped records, bytes after the terminator) |
| Per-archive key-check (no cross-archive fingerprint) | `M13BackupArchiveTests` (two archives, same key, different key-check bytes, both validate) |
| Path safety on read and write | `M13BackupArchiveTests` (`../`, absolute, backslash, NUL, empty, `.`, `a//b`, drive prefix, duplicates, reader-side unsafe tar) |
| No plaintext/key leakage | `M13BackupArchiveTests` (marker + recovery-key bytes absent from the ciphertext) |
| Unsupported format version | `M13BackupArchiveTests` (`FORMAT_UNSUPPORTED`) |
| Checksum-before-decryption | `M13BackupArchiveTests` (expected-checksum mismatch fails fast) |
| pg_dump invocation, no shell, password not in argv/logs | `M13PgRestoreRunnerTests`/`M13BackupServiceUnitTests` (argument shape, `PGPASSWORD` only, missing binary) |
| Client older than server refuses | `M13PgToolingInfoTests`, `M13PgVersionParserTests`, `M13BackupServiceIntegrationTests` |
| Backup lifecycle, checksum/size, temp-then-rename, concurrency, delete guard, retention, audit | `M13BackupServiceIntegrationTests` (+ `M13BackupServiceUnitTests`) — incl. the disk-truth retention test (7 files / 6 rows / a manual file → newest 5 kept) |
| `backup_metadata` schema | `M13BackupMetadataSchemaModelTests`, `M13BackupMetadataSchemaIntegrationTests` |
| Restore validation before mutation (key/format/integrity/checksum/schema) | `M13RestoreServiceIntegrationTests` (every failure asserts nothing touched) |
| Mandatory safety backup; maintenance 503; single-transaction restore; session revocation; audit into the restored DB; swap rollback | `M13RestoreServiceIntegrationTests` (success sequence + `M13PgRestoreRunnerTests`) |
| Pre-commit failure leaves data intact; post-commit failure still restarts | `M13RestoreServiceIntegrationTests` (two explicit cases + the marker/post-step error) |
| Restore working dirs reclaimed | `M13RestoreServiceIntegrationTests`, `M13BackupServiceIntegrationTests` |
| Clean-install restore: pre-setup only, key shape first, adoption ordering, no overwrite, 409 after setup, no mutation on failure | `M13SetupRestoreIntegrationTests` |
| Recovery-key export: Admin-only, typed `EXPORT`, attachment-only, `no-store`, audit without key, never creates the key | `M13RecoveryKeyExportTests` |
| Version display: authenticated, real SemVer + applied migration, `null` degradation, no admin gate | `M13AboutEndpointTests`, **Vitest** `AboutSection.test.tsx`, **e2e** `settings.spec.ts` |
| Update privacy: exact URL/method/UA/Accept, no body, no extra headers | `M13UpdateCheckTests` (handler-level assertion of the exact header set) |
| SemVer, drafts/prereleases, unparseable tags | `M13UpdateCheckTests` |
| 24 h TTL, forced check, disabled ⇒ zero HTTP, cache read never calls the network | `M13UpdateCheckTests` |
| Failure tolerance (404/5xx/timeout/offline/oversized/invalid) + `last_error` + last-known release | `M13UpdateCheckTests` |
| Non-blocking startup check | `M13UpdateCheckTests` (health answers while the release request is in flight) |
| Banner/About UI (dismiss per version, https-only link, no colour-only, member 403 readable, admin-only toggle) | **Vitest** `UpdateBanner.test.tsx`, `AboutSection.test.tsx`; **e2e** `settings.spec.ts` |
| Backups UI (list/create/download/delete/restore, typed gates, 2 GB upload, restoring state, errors, member absence) | **Vitest** `BackupsSection.test.tsx`; **e2e** `settings.spec.ts` |
| Clean-install restore UI (file + key + typed `RESTORE`, polling, errors) | **Vitest** `RestoreFromBackupPage.test.tsx`, `SetupWizard.test.tsx`; **e2e** `app.spec.ts` |
| Permissions/scope: Admin-only routes, authenticated reads, pre-setup-only route, no eighth key, one migration | the suites above + `PermissionMatrixIntegrationTests`, `PermissionKeysTests`, `dotnet ef migrations has-pending-model-changes` |

## Gaps and accepted limitations

- **The real `pg_dump`/`pg_restore` paths are skipped on the development
  workstation** (no PostgreSQL client tools installed). Their argument
  construction, failure handling and orchestration are covered with fakes; the
  actual binaries are exercised by the live gate on the runtime image
  (`postgresql-client-17`), which must prove a create → download → restore →
  restart → migrations → sessions-revoked round trip.
- **Restore is not exercised destructively against production data.** The live
  gate uses a disposable stack for the round trip and non-destructive probes
  (validation refusals) against the production instance.
- The **file swap runs after the database restore commits** (locked in the plan
  §1.9): a swap failure rolls files back and leaves the database restored, with
  the safety archive as the way back; a post-commit failure still restarts.
- The archive is decrypted twice (validate, then extract) by design; a very large
  upload pays that I/O cost.
- `RerollUndo.test.tsx` (calendar, pre-M13) is a known intermittent timeout under
  load; it is not part of M13 and passes on a clean run.
