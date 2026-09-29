# Fate & Fridge — Milestones and Initial Issue Plan

## Milestone 0 — Project Skeleton
Goal: working repo and Docker stack.

Issues:
1. Initialize monorepo layout.
2. Add FastAPI backend shell.
3. Add React/TypeScript/Vite frontend shell.
4. Add PostgreSQL service.
5. Add SQLAlchemy + Alembic.
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
Issues:
1. Household settings model.
2. Setup wizard.
3. Single-user no-login mode.
4. Multiuser mode.
5. Admin creation.
6. Member creation.
7. Argon2id password hashing.
8. Session/auth system.
9. Permission groups.
10. Login rate limits.
11. Auth event logging.
12. Admin password reset.
13. Optional IP allowlist configuration.

---

## Milestone 2 — Reference Data
Issues:
1. Grocery departments.
2. Units.
3. Unit aliases.
4. Ingredients.
5. Ingredient aliases.
6. Ingredient autocomplete.
7. Inline ingredient create.
8. Ingredient merge.
9. Ingredient retire/delete safety.
10. Primary proteins.
11. Tags.

---

## Milestone 3 — Recipes
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
1. recovery key generation.
2. persistent key storage.
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
