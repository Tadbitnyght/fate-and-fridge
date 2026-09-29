# Fate & Fridge — Database Model

## 1. Principles
- PostgreSQL
- relational integrity first
- preserve history
- avoid destructive cascades into historical data
- aliases and canonical master records
- audit meaningful changes
- support future API/service accounts without rebuilding auth model

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
- `password_hash`
- `role` (`admin`, `member`)
- `is_active`
- `locked_until`
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

---

### auth_events
- `id`
- `user_id` nullable
- `username_attempted` nullable
- `event_type`
- `ip_address`
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
- `sort_order`
- `is_active`
- `created_at`
- `updated_at`

### ingredients
- `id`
- `canonical_name`
- `normalized_name`
- `department_id`
- `is_active`
- `created_at`
- `updated_at`

### ingredient_aliases
- `id`
- `ingredient_id`
- `alias`
- `normalized_alias`
- unique normalized alias where practical

---

## 5. Units

### units
- `id`
- `symbol`
- `display_name`
- `dimension`
- `base_factor` nullable
- `is_custom`
- `is_active`

Example dimensions:
- mass
- volume
- count
- nonconvertible

### unit_aliases
- `id`
- `unit_id`
- `alias`
- `normalized_alias`

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

Do not log ordinary page views.

---

## 11. Application Settings

### application_settings
Key/value or typed settings.

Examples:
- protein_neighbor_distance = 1
- update_check_enabled
- update_check_interval
- ip_allowlist_enabled
- ip_allowlist_ranges
- grocery defaults
- startup behavior

Avoid putting complex relational domain data into generic JSON settings when a dedicated table is more appropriate.

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
- ingredients.normalized_name
- ingredient_aliases.normalized_alias
- tags.normalized_name
- proteins.normalized_name
- audit_events.created_at
- auth_events.occurred_at

Use PostgreSQL text search/trigram extensions only if needed after baseline performance is known.
