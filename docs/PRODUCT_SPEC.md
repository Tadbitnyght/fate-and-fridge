# Fate & Fridge — Product Specification

## 1. Overview
**Fate & Fridge** is a self-hosted household dinner planner.

Codename: **Operation Dinner Plate**

V1 focuses on:
- recipe storage
- dinner scheduling
- controlled randomization
- manual meal selection
- historical meal tracking
- printable monthly calendars
- live grocery-list generation
- recipe URL import
- multiuser household access
- statistics
- backup/restore

The application is dinner-only in V1.

Each calendar date contains one dinner entry.

---

## 2. Deployment
- Web-based
- Dockerized
- Self-hosted
- TrueNAS SCALE friendly
- Generic Docker-compatible
- One installation = one household
- Core functions work offline
- No telemetry by default

---

## 3. Setup Modes

### Single User
- No login
- Intended for trusted/private networks
- May later be changed to authenticated multiuser mode

### Multiuser
- Requires Admin account
- Shared household data
- Admin + Member roles
- Member capabilities controlled by permission groups

---

## 4. Member Permission Groups
- Calendar: View
- Calendar: Edit
- Groceries: View
- Groceries: Edit
- Recipes: View
- Recipes: Edit
- Reference Data: Edit

Admin always has all permissions.

Admin-only:
- users
- permissions
- audit log
- historical correction
- backup/restore
- destructive admin workflows
- global security/settings

---

## 5. Main Navigation
- Calendar
- Dashboard
- Recipes
- Groceries
- Statistics
- Settings

Calendar is default startup page.

User may choose Calendar or Dashboard as preferred startup page.

---

## 6. Calendar Behavior

### Default
- Current month opens by default.
- Future planning may extend arbitrarily far.
- Week start is configurable.
- Default week start: Sunday.

### Desktop
Traditional month grid.

### Mobile
Toggle:
- Month
- Agenda

### Historical Dates
- Dimmed
- Read-only
- Backend enforced

### Today
- Highlighted
- Editable

### Locked Dates
- Show lock icon
- Protected from every automatic/random change
- May be manually changed
- Remain locked after manual change until explicitly unlocked

Blank dates cannot be locked.

---

## 7. Meal Types

### Recipe
Requires recipe.

### Eat Out
Optional:
- restaurant
- notes

No groceries.

### Fend For Yourself
Optional note.

No groceries.

### Host Family Dinner
Optional:
- recipe
- note
- diner count

If recipe exists, groceries are included.

### Go To Family Dinner
Optional host/location.

No recipe required.
No groceries.

### Holiday
Optional:
- custom name
- recipe
- notes

Recipe contributes groceries if attached.

### Misc
Required custom label.
Optional recipe.
Recipe contributes groceries if attached.

### Vacation
Blocks automated meal generation.
No groceries.

Special days may use a user-selected icon from a curated icon set.

---

## 8. Diner Count
Default household diner count: **4**

May vary per date.

V1 stores diner count as metadata only.

Future versions may scale recipe quantities based on diner count.

---

## 9. Historical Handling
At midnight in household timezone:
- yesterday becomes historical
- it becomes immutable in normal UI
- it counts toward historical statistics

Today remains planned until it becomes historical.

Admin may correct history only through specialized administration.

---

## 10. Historical Correction
Admin workflow must:
1. Live outside normal calendar editing
2. Show explicit warning
3. Require reason
4. Require confirmation checkbox
5. Require final confirmation
6. Create audit event with before/after values

---

## 11. Recipes

Fields:
- Name
- Description
- Photo
- Source Site
- Source URL
- Author
- Prep Time
- Cook Time
- Notes
- Favorite
- Randomization Status
- Ingredients
- Ingredient Groups
- Instruction Sections
- Ordered Instruction Steps
- Primary Proteins
- Tags
- Vegan flag
- Vegetarian flag
- Created At
- Updated At

Randomization Status:
- Normal
- Manual Only
- Retired

Retired recipes remain in history/statistics but are not randomly selected.

---

## 12. Recipe Statistics
Display:
- Last Eaten
- Next Planned
- Times Eaten

Definitions:
- Last Eaten = latest historical occurrence
- Next Planned = nearest today/future occurrence
- Times Eaten = historical occurrences only

Times Planned is deferred.

---

## 13. Manual Meal Selection
Searchable autocomplete.

Result card emphasizes:
- image
- name
- last eaten
- historical count

If exact recipe lies inside repeat-avoidance window:
show warning but allow user override.

Do not warn for same-protein soft preference.

---

## 14. Reroll
Single-date action label: **Reroll**

Behavior:
- immediate
- no confirmation
- respects rules
- disabled on locked dates
- temporary Undo offered

Desktop:
- compact action on hover/context area

Mobile:
- action inside date detail

---

## 15. Seven-Day Reroll
Selected starting date + next 6 days.

Preview only:
- proposed 7-day outcome
- locked dates shown unchanged

Actions:
- Try Another
- Apply Changes
- Cancel

Apply is atomic.

No partial save.

---

## 16. Rest-of-Month Reroll
Starts selected date.
Ends last day of that month.

Same preview workflow as 7-day reroll.

---

## 17. Fill Empty Days
Separate from reroll.

Fill:
- populates blank eligible dates only
- never changes existing entries
- skips special days
- skips locked dates

---

## 18. Grocery List
The grocery list is a live projection of:

**planned recipe ingredients in active range + manual grocery items**

It is not a stale saved derivative.

Meal changes immediately recalculate recipe-derived groceries.

Manual items remain.

---

## 19. Grocery Range Presets
- Next 3 Days
- Next 7 Days
- Next 14 Days
- Rest of Month
- Custom

Custom:
- start date
- number of days

---

## 20. Grocery Aggregation
Same canonical ingredient + compatible units should combine.

Example:
- 1 lb ground beef
- 8 oz ground beef

=> 1.5 lb ground beef

Do not combine incompatible dimensions.

Example:
- 1 onion
- 1/2 cup chopped onion

Stay separate.

Package-size inference is deferred.

---

## 21. Grocery Attribution
Each recipe-derived grocery item should show which meals caused it.

Example:

**Ground Beef — 4 lb**

- Tacos — Tue 10/6
- Meatloaf — Thu 10/8
- Burgers — Sat 10/10

---

## 22. Grocery Departments
Default:
- Produce
- Meat & Seafood
- Dairy & Eggs
- Bakery
- Deli
- Frozen
- Canned Goods
- Dry Goods / Pantry
- Spices & Seasonings
- Condiments & Sauces
- Beverages
- Household
- Personal Care
- Other

Users may:
- add
- rename
- reorder
- disable/remove where safe

Display and print follow configured order.

---

## 23. Grocery Check State
Checked item:
- dimmed
- struck through
- moved below unchecked items in same department

Unchecking restores normal style.

Provide:
- Hide Checked Items

Checked state persists.

When date range changes, matching checked items should remain checked where reasonable.

---

## 24. Staples
Reusable Household Staples list.

Examples:
- Milk
- Bread
- Eggs
- Coffee
- Dog food
- Toilet paper

Manual grocery items may include:
- Save for future

Reusable items retain department metadata.

On-screen, manual/staple items should look visually distinct from recipe-derived items, but not through color alone.

Printed list uses normal styling.

---

## 25. Grocery Mobile Experience
Priority V1 use case.

Requirements:
- large checkboxes
- large tap targets
- sticky department headers where useful
- minimal clutter
- live sync across devices

---

## 26. Printing

### Grocery
- grouped by department
- respects department order
- checkbox before each item
- recipe/date attribution
- manual items included
- checked items excluded by default
- option to include checked items

### Calendar
- landscape
- one month per page
- day number
- recipe or special-event text
- no meal photos
- no lock icons
- wrap long names

> **Clarification.** "Wrap long names" and "one month per page" are reconciled
> as follows: long names **wrap fully**; a busy month **auto-shrinks** to a
> **≈10pt legibility floor**; and a month that still overflows at that floor
> **continues onto a following page** rather than shrinking further or clipping.

List-style calendar printing deferred.

---

## 27. Recipe Browser
Default: photo card grid.

Primary:
- photo
- recipe name

Secondary:
- last eaten
- times eaten

Filters:
- search by name
- favorite
- status
- primary protein
- tags
- last eaten

Sort:
- name
- recently eaten
- least recently eaten
- most eaten
- favorites
- recently added

Optional compact list view for large libraries.

---

## 28. Recipe Import
User pastes URL and explicitly chooses:
- Import Recipe
- Cancel

Parsing order:
1. schema.org Recipe JSON-LD
2. best-effort HTML parse

Normalize:
- whitespace
- fractions
- unit aliases
- obvious ingredient aliases
- basic formatting

Do not invent uncertain data.

Always review before save.

If page has multiple recipes, ask which one.

Downloaded recipe photos should be stored locally when possible.

Source URL/site/author should be retained.

---

## 29. Duplicate Import Handling
V1 duplicate checks:
- exact normalized Source URL
- normalized/similar recipe name

Exact URL duplicate:
strong warning.

Offer:
- Open Existing
- Import Anyway
- Cancel

Similar-name duplicate:
warn and allow user to rename imported recipe.

Advanced duplicate detection deferred.

---

## 30. Recipe Deletion
Normal workflow:
- Retire

No ordinary hard delete.

Admin may permanently delete any recipe only through extreme confirmation flow.

If recipe has history:
- preserve historical display integrity using snapshots/tombstones
- do not break references

---

## 31. Ingredient Master List
Recipe ingredients reference canonical ingredients.

Autocomplete appears under ingredient text field.

If no match:
- Create Ingredient inline

Ingredient fields:
- canonical name
- aliases
- grocery department
- active/retired state

---

## 32. Ingredient Aliases
Example:

Canonical:
- Scallion

Aliases:
- Green Onion
- Spring Onion

Aliases participate in:
- search
- autocomplete
- import matching

---

## 33. Ingredient Merge
Duplicate ingredients may be merged.

Merge:
- reassigns recipe references
- preserves historical integrity
- preserves useful old names as aliases

---

## 34. Ingredient Delete
Admin may delete ingredient only if unused by every recipe, including retired recipes.

If used:
- offer merge
- offer retire
- do not delete

---

## 35. Units
Default units:
- tsp
- tbsp
- fl oz
- cup
- pint
- quart
- gallon
- oz
- lb
- g
- kg
- each
- package
- can
- bottle
- jar
- bunch
- clove
- slice
- pinch
- to taste

Custom units allowed.

Once created, custom units become reusable standard units.

---

## 36. Unit Aliases
Example:

Canonical:
- tbsp

Aliases:
- tablespoon
- tablespoons
- Tbsp
- T

Imports and entry normalize aliases to canonical values.

---

## 37. Quantities
Store internally as precise numeric values.

Display user-friendly fractions.

Examples:
- 0.5 => 1/2
- 1.5 => 1 1/2
- 2.25 => 2 1/4

---

## 38. Primary Proteins
Recipe supports multiple values.

User-editable master list.

Examples:
- Beef
- Chicken
- Pork
- Turkey
- Seafood
- Lamb
- Venison
- Duck
- Other

Vegetarian/Vegan are separate metadata flags and do not affect V1 randomization.

---

## 39. Tags
Flexible user-created tags with autocomplete.

Examples:
- Italian
- Mexican
- Pasta
- Grill
- Slow Cooker
- Air Fryer
- Comfort Food

Imported category values may be suggested but require user control.

---

## 40. Favorites
Favorite primarily supports display/filtering.

Randomizer bonus is small:
- normal = 1.00
- favorite = 1.25

Favorite weighting never outranks repeat spacing or protein variety.

---

## 41. Statistics Page
Dedicated V1 page.

Candidate metrics:
- most eaten recipes
- least recently eaten recipes
- meal count this month
- meal count this year
- primary protein distribution
- Eat Out count
- Family Dinner count
- Fend For Yourself count
- Vacation count

Keep analytics modest and useful.

---

## 42. Meal History
Historical dinners are browseable by date.

Normal users cannot edit past entries.

Admin correction is separate.

---

## 43. Audit Log
Admin-only.

Meaningful changes only.

Include:
- meal changes
- rerolls
- lock/unlock
- recipe changes
- grocery manual changes
- ingredient merge/retire
- user changes
- permission changes
- historical corrections
- backup restore
- successful login
- failed login

Do not log ordinary page views.

---

## 44. Live Sync
Use WebSockets.

Live updates:
- grocery changes
- calendar changes
- lock changes
- visible recipe changes where practical

---

## 45. Update Checks
V1 periodically checks whether a newer release exists.

Display update notice.

Do not self-update.

No telemetry beyond what is required to determine version availability.

---

## 46. Backup/Restore
Manual encrypted backup in V1.

Backup contains:
- database/application data
- recipes
- historical data
- settings
- users/permissions
- images
- grocery/reference data
- required persistent config

Recovery key:
- generated at setup
- stored in persistent config volume
- can be exported separately by Admin
- not casually displayed

Restore:
- Admin only
- validate archive/version/key
- severe confirmation
- create safety backup first
- restore
- validate resulting state

Scheduled/offsite backups deferred.

---

## 47. V1 Non-Goals
Not in V1:
- pantry inventory
- serving scaling
- AI parsing
- Alexa/Google Home
- public API keys
- 2FA
- multi-household tenancy
- scheduled cloud backups
- automatic holiday packs
- nutrition/macros
- advanced duplicate detection
- package/can-size conversion
- dietary scheduling rules
- advanced cuisine scheduling
- automatic app updating
