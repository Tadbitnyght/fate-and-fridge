# Fate & Fridge — Milestones and Initial Issue Plan

## Milestone 0 — Project Skeleton
Goal: working repo and Docker stack.

Issues:
1. Initialize monorepo layout.
2. Add ASP.NET Core backend shell (.NET 10, minimal APIs).
3. Add React/TypeScript/Vite frontend shell.
4. Add PostgreSQL service.
5. Add EF Core (Npgsql) + migrations baseline.
6. Add Docker Compose.
7. Add app health endpoint.
8. Add frontend health/status screen.
9. Add linting/formatting/type checks.
10. Add backend/frontend test runners.
11. Add CI workflow.
12. Add `.env.example`.

Acceptance:
`docker compose up -d` produces reachable app shell.

---

## Milestone 1 — Setup and Authentication

Approved decisions (2026-09-29):
- The backup recovery key is generated during setup in M1 (moved forward from M13 issues 1–2). M13 keeps export and restore.
- Sessions: DB-backed opaque session cookie (`user_sessions`), 30-day idle / 90-day absolute, rotation on login, password change, and privilege change; all sessions revoked on password reset or disable; Data Protection keys persisted in `/data/config`; antiforgery on unsafe methods.
- Password policy: 12–128 characters, no composition rules, paste allowed.
- Admin reset issues a one-time temporary password and sets `users.must_change_password` (new column).
- Lockout: 5 failures → progressive delay, 10 → 15-minute lock, 5 lockouts in 24h → 24-hour lock; per-IP 20 failures/10 min → 429; generic errors to prevent account enumeration.
- Argon2id via libsodium bindings, PHC-encoded parameters, rehash on login when parameters change.
- Single-user mode: system actor (null `created_by`/`updated_by`); Settings supports single → multiuser conversion with first-Admin creation. Multiuser → single is deferred.
- Permissions: per-user keys in `user_permissions`; "permission groups" are UI presets only; edit implies view server-side; new members default view-only.
- IP allowlist: app-wide except `/api/v1/health`; loopback always allowed; IPv4/IPv6 CIDR; forwarded headers honored only when the immediate peer is a configured trusted proxy.
- Pre-setup gating: all `/api/v1` routes except `/health` and `/setup/*` return 409 `SETUP_REQUIRED`.

Issues:
| # | Issue | Deliverable |
|---|-------|-------------|
| M1-01 | Schema baseline | `household_settings`, `application_settings`, `users`, `user_permissions`, `auth_events`, `user_sessions` + migration |
| M1-02 | Setup state + error envelope | stable error envelope, `GET /setup/status`, `SETUP_REQUIRED` gating |
| M1-03 | Household settings API | `GET/PATCH /settings/household` + validation |
| M1-04 | Setup completion | `POST /setup/complete` (atomic, single-shot) + recovery key generation/storage |
| M1-05 | Password hashing | libsodium Argon2id hasher + tests |
| M1-06 | Session infrastructure | DB sessions, cookie scheme, CSRF, persisted Data Protection keys |
| M1-07 | Auth endpoints | login/logout/session + lockout + rate limits + auth events |
| M1-08 | Password lifecycle | change-password, admin reset, `must_change_password` |
| M1-09 | Permissions | evaluator, edit-implies-view, enforcement helpers |
| M1-10 | User management | users CRUD, permissions, disable/enable, last-admin safety, auth-mode switch |
| M1-11 | IP allowlist | middleware + security settings + trusted proxies |
| M1-12 | Frontend foundation | router, query client, API client, session context, login/logout, shell |
| M1-13 | Setup wizard UI | mode, household, admin, allowlist, recovery key display |
| M1-14 | Admin console UI | household, users/permissions, security, change password |
| M1-15 | M1 verification | permission matrix, CSRF/session/allowlist/setup race tests, docs |

Acceptance:
- Fresh install reaches the setup wizard; setup is single-shot, atomic, and creates the recovery key.
- Multiuser login, lockout, rate limits, CSRF, and the full permission matrix pass independent tests.
- Single-user mode operates without login and can be converted to multiuser from Settings.
- The IP allowlist blocks disallowed clients app-wide but never health checks or loopback.

---

## Milestone 2 — Reference Data

Approved decisions (2026-09-30): see `docs/M2_PLAN.md` for the full runbook, seed data,
and issue acceptance criteria.
- Idempotent startup seeder (flag `reference_data_seeded`) inserts the default
  departments, units + aliases, and proteins; existing installs are backfilled once and
  user edits are never overwritten.
- Conversion factors are configurable per unit (`base_factor`, mass/volume only, audited,
  range-validated); dimensions are immutable after creation.
- Names/aliases normalize with diacritic folding, no auto-plurals.
- Ingredient merges record `merged_into_ingredient_id`; delete/retire rules are per type.
- Minimal `audit_events` + `AuditService` land in M2 (viewer stays in M12).
- `/api/v1/departments` family plus `PUT /departments/order`; autocomplete contract and
  conflict codes in `docs/API.md` §22.
- M2 includes a Reference Data management UI in the admin console; recipe-entry
  autocomplete lands with M3.
- `IReferenceUsageChecker` seam returns zero until M3 adds recipe tables.

Issues (execution order; details in `docs/M2_PLAN.md` §3):
| # | Issue | Depends on |
|---|-------|-----------|
| M2-01 | Schema + audit baseline | — |
| M2-02 | Minimal audit service | M2-01 |
| M2-03 | Normalization utilities | — |
| M2-04 | Reference-data seeder | M2-01, M2-03 |
| M2-05 | Departments API | M2-02…M2-04 |
| M2-06 | Units API + aliases | M2-02…M2-04 |
| M2-07 | Unit conversion service | M2-06 |
| M2-08 | Ingredients API + aliases | M2-02, M2-03, M2-05 |
| M2-09 | Ingredient merge | M2-08 |
| M2-10 | Proteins + tags APIs | M2-02, M2-03 |
| M2-11 | Reference-data admin UI | M2-05…M2-10 |
| M2-12 | M2 verification sweep | M2-01…M2-11 |

Acceptance: seed data matches `docs/M2_PLAN.md` §2; conversion math respects configured
factors; merges are transactional and audited; delete safety holds; the management UI
works on mobile and keyboard; the progress manifest marks M2 complete and the milestone
is tagged `m2-complete`.

---

## Milestone 3 — Recipes
Approved decisions (2026-10-01): see `docs/M3_PLAN.md` for the authoritative
recipe model, API/UI scope, image policy, pagination, permissions, validation
limits, issue order, and execution protocol.

Issues:
1. Recipe model.
2. Recipe status.
3. Favorite flag.
4. Vegan/vegetarian metadata.
5. Recipe images.
6. Ingredient groups.
7. Recipe ingredients.
8. Instruction sections.
9. Ordered steps.
10. Recipe create/edit UI.
11. Recipe card browser.
12. Compact list mode.
13. Filters/sorts.
14. Basic recipe statistics.

---

## Milestone 4 — URL Import
Approved decisions (2026-10-03): see `docs/M4_PLAN.md` for the authoritative
draft-only import architecture, SSRF/fetch limits, parser/normalization rules,
image policy, API contract, issue order, and execution protocol.

Issues:
1. Safe URL fetcher.
2. JSON-LD Recipe parser.
3. Multi-recipe selection.
4. HTML fallback parser.
5. Fraction/unit normalization.
6. Ingredient matcher.
7. Unknown ingredient flow.
8. Unknown custom unit flow.
9. Image downloader.
10. Import review screen.
11. Duplicate source URL warning.
12. Similar-name warning.
13. Import security tests.

---

## Milestone 5 — Calendar Core
Approved decisions (2026-10-05): see `docs/M5_PLAN.md` for the authoritative
entry model, per-type field rules, route contract, timezone/historical freeze,
lock semantics, icon catalog, UI/a11y scope, issue order, and verification
battery.

Issues:
1. Calendar entry model.
2. Month calendar.
3. Mobile agenda.
4. Date detail UI.
5. Special event types.
6. Event icons.
7. Diner metadata.
8. Lock/unlock.
9. Manual recipe selection.
10. Historical date backend enforcement.
11. Historical visual styling.
12. Arbitrary future navigation.
13. Calendar print skeleton.

---

## Milestone 6 — Randomization Engine
Approved decisions (2026-10-06): see `docs/M6_PLAN.md` for the authoritative
scoring settings, eligibility/window/protein/spacing rules, reroll/undo
contract, operation metadata, UI scope, issue order, and verification battery.

Issues:
1. Randomizer service contract.
2. Candidate eligibility.
3. 7-day backward exact-repeat window.
4. 14-day forward exact-repeat window.
5. Special-event reduced penalty.
6. direct-neighbor protein penalty.
7. spacing score.
8. favorite weighting.
9. secure random selection.
10. single-date Reroll.
11. operation metadata.
12. temporary Undo.
13. exhaustive randomizer tests.

---

## Milestone 7 — Batch Planning
Issues:
1. 7-day reroll preview.
2. Try Another.
3. atomic Apply.
4. Rest-of-month preview.
5. Fill Empty Days.
6. lock behavior in batch.
7. vacation blocking.
8. batch concurrency protection.

---

## Milestone 8 — Groceries
Issues:
1. Grocery aggregation service.
2. Unit conversion families.
3. Recipe attribution.
4. Date range presets.
5. Custom start + day count.
6. Manual grocery items.
7. Reusable manual items.
8. Staples.
9. Department ordering.
10. Checkbox persistence.
11. Hide checked.
12. mobile grocery UI.
13. print grocery list.

---

## Milestone 9 — Live Sync
Issues:
1. WebSocket auth.
2. Calendar update events.
3. Grocery update events.
4. Lock update events.
5. query invalidation.
6. two-device sync tests.

---

## Milestone 10 — Printing
Issues:
1. Landscape monthly print.
2. multi-month page handling.
3. long-name wrapping.
4. grocery print attribution.
5. include-checked option.
6. browser print CSS testing.

---

## Milestone 11 — Statistics and History
Issues:
1. Statistics overview.
2. recipe history metrics.
3. protein distribution.
4. special-event counts.
5. meal-history browser.

Approved decisions (2026-10-14): see `docs/M11_PLAN.md` for the authoritative
metric set, occurrence/eaten rules, household-local windows, history range and
paging, API contracts, and verification battery. Delivered 2026-10-18; runbook
`docs/M11_PLAN.md`, coverage map `docs/M11_TEST_MATRIX.md`, and the independent
verification record `docs/M11_VERIFICATION.md`. Tracking issue #140; issues
M11-01…M11-08 are #141–#148.

---

## Milestone 12 — Administration
Issues:
1. Audit event model/service.
2. Admin audit viewer.
3. historical correction workflow.
4. historical correction audit.
5. hard recipe deletion workflow.
6. snapshot/tombstone handling.
7. admin-only dangerous-action confirmation framework.

---

## Milestone 13 — Backup / Restore / Updates
Issues:
1. recovery key generation — delivered in M1 (M1-04).
2. persistent key storage — delivered in M1 (M1-04; `/data/config/recovery.key`).
3. key export.
4. encrypted backup format.
5. database backup.
6. image/config backup.
7. backup checksum/version metadata.
8. restore validation.
9. safety backup before restore.
10. restore workflow.
11. release version display.
12. periodic update check.

---

## Milestone 14 — Hardening
Issues:
1. permission matrix penetration tests.
2. past-date bypass tests.
3. SSRF security review.
4. malicious file upload tests.
5. backup archive traversal tests.
6. WebSocket auth tests.
7. timezone/DST suite.
8. multiuser concurrency tests.
9. accessibility audit.
10. mobile audit.
11. dependency/security scan.
12. documentation polish.

---

## Scope Control
Every new issue must identify:
- V1 blocker
- V1.x enhancement
- V2 candidate
- rejected

The Lead Developer must reject "while we're here" feature expansion unless it is necessary to satisfy an approved requirement.

All V2 and future ideas are recorded in `docs/FUTURE_FEATURES.md` (the single
registry, superseding the former `docs/V2_BACKLOG.md`).

### V1.x Enhancements (tracked)
- Milestone progress page (issue #22) — delivered post-M1; classification stays V1.x (not part of the M0–M14 V1 plan). The Definition of Done now requires updating the progress manifest when a milestone completes.
