# Fate & Fridge — Database Model

## 1. Principles
- PostgreSQL
- relational integrity first
- preserve history
- avoid destructive cascades into historical data
- aliases and canonical master records
- audit meaningful changes
- support future API/service accounts without rebuilding auth model

Conventions (approved M1):
- Primary keys are `bigint` identity surrogate keys. IDs are not treated as secrets; authorization is always server-side.
- Instants are stored as `timestamptz` and are always UTC in code.
- Calendar dates are stored as `date` and evaluated in the household timezone.
- Enum-like columns are stored as text with application-level constants (no PostgreSQL enum types).
- Normalized columns are lowercase, trimmed, and whitespace-collapsed, with Unicode diacritics folded (`jalapeño` → `jalapeno`); no plural folding (plurals are explicit aliases).

---

## 2. Core Tables

### household_settings
Suggested fields:
- `id`
- `household_name`
- `timezone`
- `default_diner_count`
- `week_starts_on`
- `startup_page`
- `auth_mode`
- `created_at`
- `updated_at`

---

### users
- `id`
- `username`
- `normalized_username` — unique; lowercase, trimmed
- `password_hash`
- `must_change_password` — set by Admin reset; cleared after a successful change
- `role` (`admin`, `member`)
- `is_active`
- `locked_until`
- `failed_login_count` — consecutive failures since the last success
- `last_failed_login_at`
- `lockout_count` — lockouts inside the current lockout window
- `lockout_window_started_at`
- `created_at`
- `updated_at`
- `last_login_at`

Single-user mode may use no ordinary user session.

---

### user_permissions
- `id`
- `user_id`
- `permission_key`
- unique(`user_id`, `permission_key`)

Keys:
- `calendar.view`
- `calendar.edit`
- `groceries.view`
- `groceries.edit`
- `recipes.view`
- `recipes.edit`
- `reference_data.edit`

Edit implies view: the server evaluates an effective permission set of granted keys plus the matching view key for every granted edit key. Permission changes take effect immediately; sessions do not cache permissions.

---

### user_sessions
- `id`
- `user_id`
- `token_hash` — unique; only a hash of the session token is stored
- `created_at`
- `idle_expires_at`
- `absolute_expires_at`
- `last_seen_at`
- `revoked_at` nullable
- `revoked_reason` nullable
- `ip_address` nullable
- `user_agent` nullable

Policy (approved M1): 30-day idle timeout, 90-day absolute cap; rotation on login, password change, and privilege change; all sessions revoked when a user is disabled or has a password reset.

---

### auth_events
- `id`
- `user_id` nullable
- `username_attempted` nullable
- `event_type`
- `ip_address` nullable
- `user_agent` optional
- `occurred_at`
- `metadata_json`

Event examples:
- login_success
- login_failure
- account_locked
- password_reset

---

## 3. Recipes

### recipes
- `id`
- `name`
- `normalized_name`
- `description`
- `source_site`
- `source_url`
- `normalized_source_url`
- `author`
- `prep_minutes`
- `cook_minutes`
- `notes`
- `is_favorite`
- `status` (`normal`, `manual_only`, `retired`)
- `is_vegan`
- `is_vegetarian`
- `created_at`
- `updated_at`

Do not hard-delete through ordinary UI.

---

### recipe_images
- `id`
- `recipe_id`
- `storage_path`
- `source_url`
- `alt_text`
- `created_at`

---

### recipe_instruction_sections
- `id`
- `recipe_id`
- `title`
- `sort_order`

### recipe_instruction_steps
- `id`
- `section_id`
- `text`
- `sort_order`

---

## 4. Ingredients

### grocery_departments
- `id`
- `name`
- `normalized_name` — unique; lowercase, trimmed, whitespace-collapsed, diacritics folded
- `sort_order`
- `is_active`
- `created_at`
- `updated_at`

### ingredients
- `id`
- `canonical_name`
- `normalized_name` (unique; lowercase, trimmed, whitespace-collapsed, diacritics folded)
- `department_id`
- `merged_into_ingredient_id` nullable — set when this ingredient is merged into another; the source is retired and kept for traceability
- `is_active`
- `created_at`
- `updated_at`

### ingredient_aliases
- `id`
- `ingredient_id`
- `alias`
- `normalized_alias` — unique; an alias resolves to exactly one canonical ingredient

---

## 5. Units

### units
- `id`
- `symbol` — unique
- `display_name`
- `normalized_name` — unique; lowercase, trimmed, whitespace-collapsed, diacritics folded
- `dimension`
- `base_factor` — nullable `numeric(28,14)`
- `is_custom`
- `is_active`

Example dimensions:
- mass
- volume
- count
- nonconvertible

Dimension semantics (approved M2):
- `base_factor` converts to the dimension base unit: grams for mass, millilitres for volume.
- `base_factor` is editable per install for mass/volume units (`PATCH /api/v1/units/{id}`) and every change is audited; values must be positive and within a sane range.
- Count units have no factor and only combine with the **same** unit; different count units never convert (no package-size guessing).
- `to taste` is nonconvertible and may carry a null quantity.
- A unit's dimension is immutable after creation; changing families requires retiring the unit and creating a new one.

### unit_aliases
- `id`
- `unit_id`
- `alias`
- `normalized_alias`
- unique(`unit_id`, `normalized_alias`) — per unit, because the seed's `t` (teaspoon) and `T` (tablespoon) normalize to the same text

---

## 6. Recipe Ingredients

### recipe_ingredient_groups
- `id`
- `recipe_id`
- `title`
- `sort_order`

### recipe_ingredients
- `id`
- `recipe_id`
- `group_id` nullable
- `ingredient_id`
- `quantity_numeric` nullable
- `unit_id` nullable
- `preparation_note` nullable
- `is_optional`
- `sort_order`

For values like "to taste", quantity may be null with unit set appropriately.

---

## 7. Proteins and Tags

### proteins
- `id`
- `name`
- `normalized_name`
- `is_active`

### recipe_proteins
- `recipe_id`
- `protein_id`
- unique pair

### tags
- `id`
- `name`
- `normalized_name`

### recipe_tags
- `recipe_id`
- `tag_id`
- unique pair

---

## 8. Calendar

### calendar_entries
Suggested fields:
- `id`
- `meal_date`
- `entry_type`
- `recipe_id` nullable
- `is_locked`
- `diner_count`
- `restaurant_name` nullable
- `custom_label` nullable
- `notes` nullable
- `icon_key` nullable
- `include_groceries`
- `snapshot_recipe_name` nullable
- `snapshot_display_text` nullable
- `created_by_user_id` nullable
- `updated_by_user_id` nullable
- `created_at`
- `updated_at`

Unique:
- one entry per `meal_date`

Entry types:
- recipe
- eat_out
- fend_for_yourself
- host_family_dinner
- go_to_family_dinner
- holiday
- misc
- vacation

Snapshot fields preserve historical meaning after recipe mutation/deletion.

---

### calendar_entry_history
Used for audit-friendly before/after snapshots and temporary undo support.

Suggested:
- `id`
- `calendar_entry_id`
- `operation_id`
- `before_json`
- `after_json`
- `changed_by_user_id`
- `changed_at`
- `change_reason` nullable
- `is_historical_correction`

Do not rely on this table alone for the admin audit log; audit events should remain a separate stable security/administration record.

---

## 9. Groceries

### grocery_staples
- `id`
- `name`
- `ingredient_id` nullable
- `department_id`
- `default_quantity_numeric` nullable
- `unit_id` nullable
- `is_active`
- `created_at`

### manual_grocery_items
- `id`
- `name`
- `ingredient_id` nullable
- `department_id`
- `quantity_numeric` nullable
- `unit_id` nullable
- `range_key` or active-list association strategy
- `save_for_future`
- `created_by_user_id`
- `created_at`
- `updated_at`

### grocery_check_state
Checkbox persistence should key to a stable semantic identity for the active item.

Possible fields:
- `id`
- `item_identity_hash`
- `checked`
- `checked_by_user_id`
- `checked_at`
- `updated_at`

Exact identity strategy should be designed carefully so:
- matching items remain checked across range expansion where reasonable
- different quantities/variants do not accidentally collapse

---

## 10. Audit

### audit_events
- `id`
- `actor_user_id` nullable
- `event_type`
- `entity_type`
- `entity_id` nullable
- `before_json` nullable
- `after_json` nullable
- `reason` nullable
- `ip_address` nullable
- `created_at`
- `metadata_json`

Admin-only read access.

Created in M2 (ahead of the M12 admin viewer) so that reference-data merges, retirements, deletions, and conversion-factor changes are audited from the start; M12 adds the viewer and extends usage to calendar/user/settings events.

Do not log ordinary page views.

---

## 11. Application Settings

### application_settings
Key/value or typed settings.

Examples:
- protein_neighbor_distance = 1
- update_check_enabled
- update_check_interval
- reference_data_seeded — set once by the M2 startup seeder
- ip_allowlist_enabled
- ip_allowlist_ranges
- grocery defaults
- startup behavior

Avoid putting complex relational domain data into generic JSON settings when a dedicated table is more appropriate.

Security note: the backup recovery key is **not** stored in the database. It is generated during setup (M1) and stored at `/data/config/recovery.key` with file mode `0600`. It is never logged and never returned by ordinary settings endpoints; a single display at setup completion and the M13 export endpoint are the only exposure points.

---

## 12. Backup Metadata

### backup_metadata
- `id`
- `filename`
- `app_version`
- `schema_version`
- `created_at`
- `created_by_user_id`
- `checksum`
- `size_bytes`
- `status`

Actual backup files live in mounted storage.

---

## 13. Soft Delete / Retirement Strategy
Use retirement/deactivation instead of hard deletion for:
- recipes
- ingredients
- units
- proteins
- departments where applicable

Hard deletion is reserved for explicit Admin workflows and only where product rules permit it.

---

## 14. Recipe Hard Delete with History
Admin may hard-delete a recipe after extreme confirmation.

Before delete:
- ensure historical calendar entries contain durable snapshot text
- null/tombstone live recipe foreign key safely
- retain audit record
- preserve statistics/history interpretation where feasible

No broken foreign keys.

---

## 15. Ingredient Merge Transaction
Merge must be transactional.

Steps:
1. validate source != target
2. migrate recipe_ingredient references
3. migrate compatible reusable grocery references
4. add source names/aliases to target aliases
5. retire/remove source canonical record as designed
6. emit audit event
7. commit atomically

---

## 16. Indexing
Add indexes for:
- calendar_entries.meal_date
- recipes.normalized_name
- recipes.normalized_source_url
- grocery_departments.normalized_name (unique)
- ingredients.normalized_name
- ingredient_aliases.normalized_alias
- units.symbol (unique)
- units.normalized_name (unique)
- tags.normalized_name
- proteins.normalized_name
- audit_events.created_at
- audit_events.actor_user_id
- audit_events.(entity_type, entity_id)
- auth_events.occurred_at
- auth_events.user_id
- users.normalized_username (unique)
- user_sessions.token_hash (unique)
- user_sessions.user_id
- user_sessions.absolute_expires_at

Use PostgreSQL text search/trigram extensions only if needed after baseline performance is known.
