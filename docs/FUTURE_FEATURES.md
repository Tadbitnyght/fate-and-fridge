# Fate & Fridge — Future Features & Backlog

Single, living registry of every feature, enhancement, and idea that is **not
part of the current V1 milestone plan** but may be implemented later. This file
supersedes `docs/V2_BACKLOG.md` (kept only as a redirect) and consolidates
deferred items scattered across `PRODUCT_SPEC.md`, `MILESTONES.md`, the
per-milestone plans (`M*_PLAN.md`), the verification reports
(`M*_VERIFICATION.md`), and the architecture/database/security specs.

The remaining **V1** milestones (M6–M14) are *not* listed here — they are the
committed roadmap in `docs/MILESTONES.md`. This document is only for work beyond
that plan.

## How to use this document

- Nothing here is approved or scheduled. An item becomes work only when the Lead
  classifies it (per `AGENTS.md` → Scope Rules) and it is prioritized into a
  milestone or a `V1.x` issue.
- Every new idea should be recorded here at the moment it is discussed, with its
  provenance, so nothing is lost.
- Status values:
  - **V2** — explicitly deferred from V1; a candidate for the next major version.
  - **V1.x** — a small enhancement to a shipped V1 area, tracked outside the
    milestone set.
  - **Enhancement** — an improvement or known limitation of an existing area.
  - **Research** — needs investigation or an external dependency before it can be
    scoped.
  - **Never-V1** — explicitly rejected for V1 (may be revisited only by the
    product owner).
  - **Rejected** — considered and not planned.
- To promote an item: open an issue that names the requirement, classify it
  (V1 blocker / V1.x / V2 / rejected), and link back to the entry here.

## Summary

| Area | Status | Source |
|------|--------|--------|
| [Pantry / inventory](#pantry--inventory) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §47` |
| [Serving scaling / diner-aware quantities](#serving-scaling--diner-aware-quantities) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §8/§47` |
| [Package intelligence](#package-intelligence) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §47` |
| [Dietary scheduling & restrictions](#dietary-scheduling--restrictions) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §47` |
| [Advanced variety / randomization rules](#advanced-variety--randomization-rules) | V2 | `V2_BACKLOG.md`, `M6_PLAN.md §6` |
| [Seasonal weighting](#seasonal-weighting) | V2 | `V2_BACKLOG.md` |
| [Holiday models / regional packs](#holiday-models--regional-packs) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §47` |
| [AI assistance](#ai-assistance-optional) | Research (optional) | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §47` |
| [Voice integrations](#voice-integrations) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §47` |
| [External API credentials / integrations](#external-api-credentials--integrations) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §47` |
| [Authentication: 2FA / passkeys](#authentication-2fa--passkeys) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §47` |
| [Scheduled & remote backups](#scheduled--remote-backups) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §47`, owner 2026-10-22 |
| [Recovery-key protection](#recovery-key-protection) | V2 | Owner 2026-10-22, `M13_PLAN.md §1.11` |
| [Advanced duplicate detection](#advanced-duplicate-detection) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §47`, `M4_VERIFICATION.md` |
| [Statistics expansion](#statistics-expansion) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §12` |
| [Printing enhancements](#printing-enhancements) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §26` |
| [Nutrition / macros](#nutrition--macros) | V2 | `V2_BACKLOG.md`, `PRODUCT_SPEC.md §47` |
| [Native apps / PWA](#native-apps--pwa) | V2 | `V2_BACKLOG.md` |
| [Multi-household tenancy](#multi-household-tenancy) | V2 | `PRODUCT_SPEC.md §47` |
| [Administration & audit expansion](#administration--audit-expansion) | V2 | `M12_PLAN.md §6` |
| [Import & recipe enhancements](#import--recipe-enhancements) | Enhancement | `M4_VERIFICATION.md` |
| [Calendar enhancements](#calendar-enhancements) | Enhancement | `M5_VERIFICATION.md` |
| [Randomizer enhancements](#randomizer-enhancements) | Enhancement | `M6_PLAN.md §6`, `RANDOMIZER.md` |
| [Per-user & device preferences](#per-user--device-preferences) | V1.x | This file (raised 2026-10-18) |
| [Infrastructure & performance](#infrastructure--performance) | Research | `DATABASE.md §16`, `TESTING.md §12` |
| [Explicitly never in V1](#explicitly-never-in-v1) | Never-V1 | `AGENTS.md`, `M4_PLAN.md §6`, `PRODUCT_SPEC.md §45` |

---

## Pantry / inventory
Status: **V2**

- Track pantry / fridge / freezer inventory.
- Deduct on-hand ingredients from the grocery output.
- Expiration dates.
- Package quantities.
- Pantry staples as actual stock (distinct from the V1 reusable staples list).

Notes: overlaps with Package intelligence and Serving scaling; likely a single
"pantry" subsystem if pursued. Source: `docs/V2_BACKLOG.md`, `docs/PRODUCT_SPEC.md §47`.

## Serving scaling / diner-aware quantities
Status: **V2**

- Recipe base serving size.
- Automatic ingredient scaling by the date's diner count.
- Per-date scale override.

Notes: V1 stores diner count as metadata only (`docs/PRODUCT_SPEC.md §8`);
`calendar_entries.diner_count` already exists. Source: `docs/V2_BACKLOG.md`,
`docs/M5_VERIFICATION.md`.

## Package intelligence
Status: **V2**

- Understand `2 × 14 oz cans`, jar sizes, bottle sizes.
- Package-level shopping optimization and output such as "Buy 2 cans".

Notes: must never guess package-size conversions in V1
(`docs/AGENTS.md` Grocery Rules); this is the explicit future that enables it.
Source: `docs/V2_BACKLOG.md`, `docs/PRODUCT_SPEC.md §47`.

## Dietary scheduling & restrictions
Status: **V2**

- Optional randomization preferences: vegan frequency, vegetarian frequency.
- Dietary restrictions and allergy exclusions.

Source: `docs/V2_BACKLOG.md`, `docs/PRODUCT_SPEC.md §47`.

## Advanced variety / randomization rules
Status: **V2**

- Same-protein lookback/lookahead distance beyond the direct-neighbor default.
- Cuisine frequency, pasta frequency, cooking-method frequency.
- Beef/chicken caps per week, meatless meal targets.
- Grilling / slow-cooker frequency.

Notes: M6 designs the randomizer so these can expand later (configurable
neighbor distance; household `Randomizer` settings group). Source:
`docs/V2_BACKLOG.md`, `docs/RANDOMIZER.md §6`, `docs/M6_PLAN.md §6`.

## Seasonal weighting
Status: **V2**

- Seasonal recipes, winter/summer preferences, optional month/season availability.

Source: `docs/V2_BACKLOG.md`.

## Holiday models / regional packs
Status: **V2**

- Selectable regional holiday packs (US, UK, Australia, others); optional.

Notes: V1 has a `holiday` entry type with a custom label but no automatic holiday
calendar. Source: `docs/V2_BACKLOG.md`, `docs/PRODUCT_SPEC.md §47`.

## AI assistance (optional)
Status: **Research** (must remain optional)

- Optional providers: a local model, an OpenAI-compatible endpoint, or another
  configurable provider.
- Potential uses: parse ambiguous ingredient strings, suggest ingredient
  mappings, clean instructions.

Notes: the core product must stay fully usable without AI, and no mandatory
external AI dependency may be introduced (`docs/AGENTS.md` Architecture Rules).
Source: `docs/V2_BACKLOG.md`, `docs/PRODUCT_SPEC.md §47`.

## Voice integrations
Status: **V2**

- Alexa, Google Home, possibly Home Assistant.
- Example actions: "what's for dinner today/tomorrow", "add milk to groceries",
  "read the grocery list", "reroll Friday", "set Saturday to tacos".

Notes: depends on the External API credentials model below; the API-first rule
(`docs/ARCHITECTURE.md §9`) exists to enable this. Source: `docs/V2_BACKLOG.md`.

## External API credentials / integrations
Status: **V2**

- Service-account / API credential model.
- Scoped permissions (e.g. `calendar:read`, `calendar:write`, `grocery:read`,
  `grocery:write`, `recipes:read`).

Notes: V1 deliberately exposes no external API credentials
(`docs/PRODUCT_SPEC.md §47`, `docs/ARCHITECTURE.md §9`). Source:
`docs/V2_BACKLOG.md`.

## Authentication: 2FA / passkeys
Status: **V2**

- TOTP, and passkeys later.

Source: `docs/V2_BACKLOG.md`, `docs/PRODUCT_SPEC.md §47`.

## Scheduled & remote backups
Status: **V2**

- User-configurable **recurring schedule**: run the same encrypted backup path as
  M13 automatically — every N days (e.g. every 3 days), weekly, or monthly, at a
  user-selected time.
- **Rolling retention**: keep the last **X** scheduled backups (X user-defined)
  and overwrite/delete the oldest once X is reached, independently of the M13
  safety-backup retention.
- Targets: local folder, SMB/NFS, S3-compatible, OneDrive, Google Drive.

Notes: V1 (M13) provides on-demand encrypted backup/restore, a fixed last-5
safety-backup retention, and a recovery key; scheduled runs, user-defined
frequency, and rolling retention are deferred. Recurring-schedule and
rolling-retention detail raised by the product owner 2026-10-22. Source:
`docs/V2_BACKLOG.md`, `docs/PRODUCT_SPEC.md §47`.

## Recovery-key protection
Status: **V2**

- Store `recovery.key` **encrypted at rest** instead of as a plaintext base64url
  file (M13 keeps it plaintext with mode `0600`), and make the Admin export hand
  out only an **encrypted envelope** — no plaintext key ever leaves the process
  or lands in a downloaded file.
- Unlock with a **user-supplied passphrase** (or an equivalent user-held secret):
  the passphrase is required to export and to restore on a rebuilt device, so
  "only the program knows the key" cannot be defeated by copying one file.
- Preserve disaster recovery: the household must still be able to restore onto a
  clean install, so any device-held secret needs a documented, user-held recovery
  path (the passphrase) rather than becoming unrecoverable.

Notes: raised by the product owner 2026-10-22. M13 ships the plaintext
`/data/config/recovery.key` (mode `0600`, never logged) plus an Admin-only guarded
download; encrypting it changes the restore credential model (every backup is
keyed from it), so it is a deliberate V2 redesign rather than a small hardening
patch. Source: this registry, `docs/M13_PLAN.md` §1.11, `docs/SECURITY.md` §11.

## Advanced duplicate detection
Status: **V2**

- Signals: ingredient overlap, title similarity, instruction similarity,
  source-site canonical URL, optional AI assistance.

Notes: V1 import has exact normalized-URL and scored similar-name warnings only
(`docs/M4_PLAN.md §1.9`); `docs/M4_VERIFICATION.md` records the bounded
similar-name scan as a limitation. Source: `docs/V2_BACKLOG.md`,
`docs/PRODUCT_SPEC.md §47`.

## Statistics expansion
Status: **V2**

- Times planned, average days between repeats, favorite usage, cuisine trends,
  grocery ingredient frequency, household preference trends.

Notes: V1 (M11, 2026-10-18) ships Last Eaten / Next Planned / Times Eaten plus
the `PRODUCT_SPEC.md §41` household statistics and the read-only meal-history
browser; "Times Planned" is explicitly deferred (`docs/PRODUCT_SPEC.md §12`), and
grocery ingredient frequency was **not** included in M11 (`docs/M11_PLAN.md`
§1.2, §6). Source: `docs/V2_BACKLOG.md`, `docs/M11_PLAN.md §6`.

## Printing enhancements
Status: **V2**

- List-style calendar print, weekly print, custom grocery print templates,
  recipe packet printing.

Notes: V1 (M10) does landscape monthly calendar printing and the grocery list;
"list-style calendar printing" is explicitly deferred
(`docs/PRODUCT_SPEC.md §26`). Source: `docs/V2_BACKLOG.md`.

## Nutrition / macros
Status: **V2**

- Calories, macros, nutrition imports, dietary summary.

Notes: not required by the core planner (`docs/PRODUCT_SPEC.md §47`). Source:
`docs/V2_BACKLOG.md`.

## Native apps / PWA
Status: **V2**

- PWA install, iOS/Android wrapper, or a native client using the same API.

Source: `docs/V2_BACKLOG.md`.

## Multi-household tenancy
Status: **V2**

- More than one household per installation.

Notes: V1 is single-household (single `household_settings` row;
`docs/PRODUCT_SPEC.md §47`). Source: `docs/PRODUCT_SPEC.md §47`.

## Administration & audit expansion
Status: **V2**

- Bulk / date-range historical correction by an Admin (M12 corrects one past date
  at a time).
- Per-capability permission keys — `audit.view`, `history.correct`,
  `recipes.delete` — so an Admin can delegate audit read (or other admin
  capabilities) to a member; V1 keeps audit read, historical correction, and hard
  recipe delete Admin-only with the seven-key model.
- Automatic or scheduled audit pruning, and scheduled/recurring audit exports
  (M12 prunes only on an explicit Admin action).
- Tamper-evident audit storage (hash chaining or signing) beyond the current
  append-only rows.
- A dedicated `/admin` navigation area (M12 keeps the audit log inside
  `/settings`).

Notes: raised while planning M12 (2026-10-18); each was deferred by the product
owner during the M12 decision set. Source: `docs/M12_PLAN.md §6`.

## Import & recipe enhancements
Status: **Enhancement**

- Populate `source.page_title` during import (currently `null`).
- Broaden source-URL canonicalization: fold an empty query (`…?`), a trailing
  `&`, percent-encoding case, and repeated-key value order.
- Raise/extend the similar-name scan beyond the bounded 1000-recipe, id-ordered
  window.

Notes: all are deliberate V1 limitations that only ever cause a warning to be
missed, never a false warning (`docs/M4_VERIFICATION.md`). Source:
`docs/M4_VERIFICATION.md`, `docs/M4_PLAN.md §6`.

## Calendar enhancements
Status: **Enhancement**

- Harden single-date create concurrency (the V1 write path is last-write-wins;
  M7 adds batch concurrency protection, but a concurrent create on the same date
  is not specially handled).
- Undo/redo retention beyond "until the entry is next modified".
- Richer historical snapshot/display metadata.
- Automatic holiday packs (see Holiday models).

Source: `docs/M5_VERIFICATION.md`, `docs/M5_PLAN.md §6`.

## Randomizer enhancements
Status: **Enhancement**

- Debugging/analysis tooling over the stored operation metadata (reproduce or
  analyze unexpected outcomes).
- Additional configurable knobs as advanced variety rules land.

Notes: V1 stores a full score breakdown in
`calendar_entry_history.metadata_json` but exposes only a compact operation to
clients (`docs/M6_PLAN.md §1.12`). Source: `docs/RANDOMIZER.md §7`,
`docs/M6_PLAN.md`.

## Per-user & device preferences
Status: **V1.x**

- Light/dark theme (and follow-system) as a **device-level** preference, applied
  before first paint so there is no flash of the wrong theme.
- A configurable **display** date format (the UI already renders `MM-DD-YY` via
  `frontend/src/lib/dateFormat.ts`).
- Other per-user UI/display preferences as they arise (the grocery range preset
  is already a per-user preference — `user_grocery_preferences`).

Notes: storage is **PostgreSQL, never a file** — persistent data must not depend
on the replaceable container filesystem (`docs/AGENTS.md` Architecture Rules;
`docs/DATABASE.md §1`). Server-authoritative user preferences go in a dedicated
table keyed by `user_id`, mirroring `user_grocery_preferences` (`user_id` is the
primary key), rather than generic JSON (`docs/DATABASE.md §11`). Because the
single-user mode system actor has no `users` row, its preferences must fall back
to `application_settings` under a reserved key, exactly as
`system_grocery_preferences` does (`docs/DATABASE.md §2/§9/§11`). Per-user
preference writes emit **nothing** on the live-sync bus
(`docs/M9_TEST_MATRIX.md`), and any new payload needs its own validator and a
migration with Lead approval. Two boundaries are fixed and are **not** part of
this item: **timezone stays household-scoped** (`household_settings.timezone`
drives calendar-date evaluation, the midnight rollover, randomizer windows, and
the `historical_boundary_date` freeze — a per-user timezone would make "today"
ambiguous for a shared plan), and the **API wire date format stays ISO** (see
"Explicitly never in V1").

Source: raised 2026-10-18 in a settings-storage design discussion (agent session);
grounded in `docs/DATABASE.md §2/§9/§11`, `docs/MILESTONES.md` (M1 single-user
mode), `docs/M9_TEST_MATRIX.md`.

## Infrastructure & performance
Status: **Research**

- PostgreSQL full-text search / trigram extensions, only if baseline performance
  shows a need (`docs/DATABASE.md §16`).
- Caching infrastructure such as Redis only with a demonstrated need
  (`docs/AGENTS.md` Tech Stack).
- Performance baselines and load testing (`docs/TESTING.md §12`).

## Explicitly never in V1
Status: **Never-V1**

- Browser automation, third-party JavaScript execution, headless rendering, and
  CAPTCHA handling during recipe import (`docs/AGENTS.md`, `docs/M4_PLAN.md §6`).
- Automatic application self-update — V1 only checks for a newer release and
  shows a notice (`docs/PRODUCT_SPEC.md §45/§47`).
- Telemetry by default (`docs/AGENTS.md` Architecture Rules).
- Mandatory external AI dependencies (`docs/AGENTS.md` Architecture Rules).
- A non-ISO **API wire date format** (e.g. localised `MM-DD-YY` in JSON): V1 keeps
  ISO `yyyy-MM-dd` on the wire for stored data and clients, and localises only the
  product UI display (raised planning M12, 2026-10-18; `docs/M12_PLAN.md §1.15`).
