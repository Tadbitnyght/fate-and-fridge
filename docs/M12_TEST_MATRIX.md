# M12 Test Matrix — Administration

Maps every `docs/M12_PLAN.md` §4 battery bullet to its primary automated test(s).
The independent verdict and the live-deployment evidence live in
`docs/M12_VERIFICATION.md`.

Legend: **API** = xUnit integration/unit suite in
`backend/tests/FateAndFridge.Api.Tests/`; **Vitest** = frontend component suite;
**e2e** = hermetic Playwright spec.

| §4 area | Primary coverage |
|---------|------------------|
| Audit coverage — one row per write with actor/target/before-after | `M12AuditCoverageIntegrationTests` (calendar create/update/delete/lock/unlock, the idempotent lock no-op writing **no** extra row, user lifecycle ×7, settings household/security, auth-mode switch, grocery manual items + staples, auth mirror) |
| Audit coverage — failed audit write aborts the change | `M12AuditCoverageIntegrationTests` (calendar), `M12HistoricalCorrectionIntegrationTests`, `M12RecipeHardDeleteIntegrationTests` (forced-failure rollback) |
| Audit coverage — no secret ever logged | `M12AuditCoverageIntegrationTests` (secret-leak scan over persisted `before_json`/`after_json`/`metadata_json` for resets, create-with-password, login/logout: plaintext, temporary passwords, session token, CSRF token, and `argon2`/`password_hash`/`token_hash`/`recovery` substrings all absent) |
| Audit coverage — rerolls are not in `audit_events`; no page views | `BatchApplyIntegrationTests`, `BatchUndoIntegrationTests`, `UndoEndpointsIntegrationTests` (count only non-`auth.` rows and assert no change row per reroll) |
| Merged viewer — audit + reroll timeline, sources, ordering | `M12AuditLogQueryIntegrationTests` (merged timeline, all four `kind` mappings, same-instant tie-break, source labels) |
| Merged viewer — ordinary edits not duplicated | `M12AuditLogQueryIntegrationTests` (a real `PUT` produces one audit row and no history item) |
| Merged viewer — filters, paging, detail, actor join | `M12AuditLogQueryIntegrationTests` (each filter narrows and AND-combines; household-local day bounds; keyset walk visits the log once with no gaps/duplicates; `next_cursor` null last; actor username join; malformed inputs → `VALIDATION_FAILED`) |
| Merged viewer — no N+1 | `M12AuditLogQueryIntegrationTests` (`limit=1` and `limit=200` both issue one bounded command; +1 for the timezone read with a date filter) |
| Export — CSV quoting, injection, cap, JSON equality, format, Admin-only | `M12AuditExportCsvTests` (unit: quoting, CRLF, header order, `= + - @ tab CR` neutralisation) and `M12AuditExportRetentionIntegrationTests` (HTTP: filtered export, cap → `VALIDATION_FAILED`, unknown format, Admin-only) |
| Retention — range validation, keep-forever, prune boundary/audit/scope | `M12AuditExportRetentionIntegrationTests` (settings validation incl. `0`; a row exactly at the cutoff survives; only `audit_events` touched; `audit.pruned` written afterwards; keep-forever refuses; Admin-only) and `SecuritySettingsIntegrationTests` |
| Dangerous actions — reason/confirmation enforced server-side | `DangerousActionGuardTests` (26 cases: blank/whitespace/over-long/null-char reason, exact-match ordinal confirmation, near-miss, success) and the HTTP-level guard cases in the correction and delete suites |
| Historical correction — past-only, locked dates, atomicity, snapshots | `M12HistoricalCorrectionIntegrationTests` (end-to-end with reason/history/audit/live sync; today+future → `CALENDAR_DATE_NOT_PAST`; locked → 409 then 200 with `is_locked` unchanged; guard field errors; per-type rules still apply; refreshed snapshot survives a rename **and** a hard delete; other dates untouched; atomic rollback on forced audit failure; admin-only; a correction racing a batch apply does not 500) |
| Hard delete — M11 invariants (tombstones, statistics, backfill) | `M12RecipeHardDeleteIntegrationTests` (past meals survive with the snapshot and statistics intact; tombstone `status: deleted`; a **stale** snapshot is never overwritten and a missing one is backfilled) |
| Hard delete — planned removal, images, FK safety, audit, irreversibility | `M12RecipeHardDeleteIntegrationTests` (today/future removed and gone from groceries and the randomizer; image row **and file** removed; child cascade with no orphans; `recipe.hard_deleted` before-image + counts; `404`/guard failures delete nothing; admin-only; a bare recipe still deletes) |
| Permissions — Admin-only, no eighth key | the correction/delete/audit suites' 401/403/200 matrices, `PermissionKeysTests`, `PermissionMatrixIntegrationTests` |
| Danger dialog UI (a11y, gating, submit) | **Vitest** `DangerousActionDialog.test.tsx` (14), `HistoricalCorrectionDialog.test.tsx` (10), `RecipeFormPage.test.tsx` (+10 danger-zone cases) |
| Audit viewer UI (filters, paging, detail, export, prune, gating) | **Vitest** `AuditLogSection.test.tsx` (+5), `SecuritySection.test.tsx` (+2 retention field), `useAuditLog`/`apiDownload` coverage; **e2e** `audit-log.spec.ts` (desktop read/expand/filter/export/prune + 390px no-overflow) |
| Hard delete UI (danger zone, typed gate, redirect) | **Vitest** `RecipeFormPage.test.tsx`; **e2e** `recipes.spec.ts` (admin delete flow at 390×844) |
| Date display (MM-DD-YY) | **Vitest** `dateFormat.test.ts` (28 cases), updated `statistics`/`grocery`/`recipes`/`settings` suites; **e2e** `statistics.spec.ts`, `groceries.spec.ts` |

## Gaps and accepted limitations

- **No N+1 / performance bar on a `docs/TESTING.md` §12-sized audit log.** The
  viewer's command count is proven page-size independent, but the large-dataset
  (10 years + a big audit log) latency measurement was not added as an automated
  test; the merged query relies on the existing `audit_events.created_at` and
  `calendar_entry_history.changed_at` indexes. An index migration is permitted
  later if a measured baseline misses the bar (`docs/M12_PLAN.md` §1.14).
- **User PATCH no-op still writes a `user.renamed` row** (`before == after`) —
  found in review, accepted as a minor: it is a real request and the row records
  the actor and reason. Recorded in `docs/M12_VERIFICATION.md`.
- **Correction scheduled-clock DST edge:** `AtStartOfLocalDay` uses
  `GetUtcOffset(localWallTime)`, so a DST transition exactly at local midnight
  would use the standard offset. No practical impact for the default
  `America/Chicago`; accepted.
- **`BUG-011`:** a reroll/batch apply racing a hard delete can still 500 on the
  recipe FK (transaction rolls back; nothing corrupted). Out of M12's scope
  because fixing it means changing the randomizer's candidate/commit path.
