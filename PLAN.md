# Fate & Fridge
## Codename: Operation Dinner Plate
### V1 AI Build Blueprint

## 1. Product Vision

**Fate & Fridge** is a self-hosted, Dockerized household dinner-planning application.

Its primary purpose is to answer:

> What's for dinner?

while reducing repetitive meals and automatically turning the meal plan into an organized grocery list.

The application maintains a recipe database, randomly generates dinners according to configurable variety rules, allows manual meal selection and rerolling, tracks meal history, generates grocery lists, imports recipes from websites, and supports both single-user and multi-user households.

It must function without an internet connection for all core features.

Internet access is optional for:

- Recipe URL imports
- Version/update checking
- Remote household access
- Future external integrations

No external AI service is required by V1.

---

# 2. Deployment Philosophy

Fate & Fridge is:

- Web-based
- Self-hosted
- Dockerized
- Designed especially well for TrueNAS SCALE
- Portable to ordinary Docker hosts
- Mobile responsive
- API-first internally
- Capable of running entirely on a LAN
- Capable of remote access when configured by the administrator
- Zero-telemetry by default

One installation represents **one household**.

Multiple independent households should run separate installations.

---

# 3. Recommended Technical Architecture

## Backend

**.NET 10 (LTS) + ASP.NET Core (C# 14, minimal APIs)**

Responsibilities:

- REST API
- Authentication/authorization
- Recipe management
- Ingredient normalization
- Randomization engine
- Grocery aggregation
- Statistics
- Recipe importing
- Backup/restore orchestration
- Audit logging
- Update checking
- WebSocket live synchronization

## Database

**PostgreSQL**

PostgreSQL is preferred over SQLite because Fate & Fridge includes:

- Multiple simultaneous users
- Live updates
- Relational recipe/ingredient data
- Meal history
- Future inventory management
- Statistics
- Authentication
- Audit records
- API integrations
- Long-term expansion

## ORM / Database Migration

Use:

- Entity Framework Core (Npgsql provider)
- EF Core migrations

All database changes must be implemented through migrations.

## Frontend

**React + TypeScript**

Recommended organization:

- Vite
- React Router
- TanStack Query
- Accessible component library
- Responsive CSS

The frontend must communicate with the backend exclusively through the application's API.

Do not directly couple React components to database concepts.

## Live synchronization

Use SignalR over WebSockets.

Changes should propagate between logged-in clients for:

- Calendar changes
- Grocery changes
- Recipe changes affecting visible screens
- Lock state

Example:

One household member checks Milk while shopping.

Other connected devices should update automatically.

Do **not** add Redis to V1 unless actual architecture testing demonstrates that it is necessary.

One application instance is the expected V1 deployment.

---

# 4. Docker Architecture

Primary deployment:

```text
fate-fridge-app
postgres
```

The database must not expose its port outside the Docker network by default.

Application port is configurable through Docker configuration.

Example:

```text
HOST_PORT=8080
```

Persistent mounts should conceptually include:

```text
/data/config
/data/images
/data/backups
```

PostgreSQL must have its own persistent dataset/volume.

Important:

**Never store required persistent configuration only inside the replaceable application container.**

Updating the container must not destroy:

- settings
- encryption keys
- images
- backups
- database
- household configuration

Installation should be approximately:

```text
docker compose pull
docker compose up -d
```

Then:

```text
http://SERVER-IP:PORT
```

launches the setup wizard.

---

# 5. Initial Setup Wizard

First launch presents:

## Installation Mode

### Single User

No login required.

### Multiuser

Requires creation of the initial Admin account.

The mode may later be changed without reinstalling Fate & Fridge.

Other setup values:

- Household name
- Default diners
- Default: 4
- Timezone
- Week starts Sunday/Monday
- Sunday default
- Optional allowed IP/network ranges
- Application update checks
- Backup recovery-key setup

Single-user mode must clearly warn that unauthenticated access should only be permitted from trusted networks.

---

# 6. User Model

V1 has two human roles:

## Admin

Full access.

Admin-only capabilities include:

- User management
- Permissions
- Audit history
- Historical corrections
- Permanent recipe deletion
- Backup/restore
- Global application settings
- Security settings

## Member

Access controlled through permission groups.

Permissions:

```text
calendar.view
calendar.edit

groceries.view
groceries.edit

recipes.view
recipes.edit

reference_data.edit
```

`recipes.edit` includes:

- Create recipe
- Edit recipe
- Retire recipe
- Favorite recipe
- Import recipe

`reference_data.edit` includes:

- Ingredients
- Units
- Primary proteins
- Tags
- Grocery departments

This permits configurations such as a child account with:

```text
Calendar View       YES
Calendar Edit       NO
Groceries View      YES
Groceries Edit      NO
Recipes View        YES
Recipes Edit        NO
Reference Data      NO
```

Do not implement a separate Read Only role.

---

# 7. Future API Identity

Do not fake API integrations as ordinary users.

The authorization architecture should later allow:

```text
Service Account
API Credential
```

with scopes such as:

```text
calendar:read
calendar:write
grocery:read
grocery:write
recipes:read
```

External API tokens are not required in V1.

---

# 8. Authentication Security

Multiuser authentication uses:

- Username
- Password
- Argon2id password hashing
- Secure session/token handling
- Login rate limiting

Authentication events logged:

- Successful login
- Failed login

Excessive failures may trigger progressively stronger delays and eventually a configurable **24–48 hour account lockout**.

Admin can reset Member passwords.

2FA is deferred to V2.

---

# 9. Application Navigation

Primary navigation:

```text
Calendar
Dashboard
Recipes
Groceries
Statistics
Settings
```

Default landing page:

**Calendar**

User setting allows startup page to change between:

- Calendar
- Dashboard

Dashboard provides shortcuts/cards to all major application areas.

---

# 10. Calendar

Calendar is the primary application experience.

Desktop:

Traditional month grid.

Mobile:

Toggle between:

- Month
- Agenda

Agenda should be optimized for phone use.

Historical dates:

- Visually dimmed
- Read-only

Today:

- Clearly highlighted
- Editable

Locked dates:

- Visible lock icon

Special days:

- User-selected icon
- Special-event text treatment

Do not display protein metadata in calendar cells.

---

# 11. Planning Horizon

The current month is the default calendar view.

Users may navigate and plan **arbitrarily far into the future**.

There is no artificial current-month-plus-one-month database restriction.

---

# 12. Historical Date Rule

Once the household timezone passes midnight:

Yesterday becomes historical.

Normal users cannot modify historical calendar entries.

This restriction must be enforced by the backend.

Do not rely exclusively on frontend controls.

---

# 13. Historical Correction

Admins may correct historical data through a completely separate administrative workflow.

It must **not** be accessible through normal calendar editing.

Conceptual flow:

```text
Settings
→ Administration
→ Historical Records
→ Find Date
→ Request Correction
```

Require:

1. Explicit warning
2. Reason for correction
3. Confirmation checkbox
4. Final destructive-style confirmation

All historical corrections create an audit record containing:

- Admin
- Date/time
- Original value
- Replacement value
- Reason

---

# 14. Meal Entry Model

Each date has one dinner slot.

Default number of diners:

```text
4
```

Diners may vary per date.

V1 treats diner count as metadata.

Recipe quantities do not automatically scale.

Architecture should leave future serving-size scaling possible.

---

# 15. Meal Types

User-facing choices:

### Recipe

Requires recipe.

### Eat Out

Optional:

- Restaurant
- Notes

No recipe.

No groceries.

### Fend For Yourself

Optional note.

No recipe.

No groceries.

### Host Family Dinner

Optional:

- Recipe
- Note
- Diner count

Attached recipe contributes groceries.

### Go To Family Dinner

Optional:

- Host/location text

No recipe.

No groceries.

### Holiday

Optional:

- Custom holiday name
- Recipe
- Note

Attached recipe contributes groceries.

### Misc

Requires custom label/text.

Optional recipe.

Attached recipe contributes groceries.

### Vacation

Blocks meal generation.

No random meal.

No groceries.

---

# 16. Special-Day Icons

Special-day creation should present a curated icon library.

User chooses an icon appropriate to the event.

Do not permit arbitrary HTML/icons from user input.

---

# 17. Recipe Record

A recipe contains:

```text
ID
Name
Description
Photo
Source Site
Source URL
Author
Instructions
Prep Time
Cook Time
Notes
Favorite
Randomization Status
Created At
Updated At
```

Randomization status:

```text
Normal
Manual Only
Retired
```

### Normal

Eligible for automatic randomization.

### Manual Only

May be manually selected but never randomly generated.

### Retired

Retained for:

- History
- Statistics
- Historical calendar integrity

Never randomly generated.

---

# 18. Recipe Statistics

Recipe views display:

```text
Last Eaten
Next Planned
Times Eaten
```

### Last Eaten

Latest historical occurrence.

### Next Planned

Nearest occurrence today or in the future.

### Times Eaten

Historical occurrences only.

Today's dinner remains planned until the date becomes historical.

Do not implement `Times Planned` in V1.

Architecture should permit it later.

---

# 19. Recipe Images

Manually created recipes may use:

- Image upload
- Image URL
- No image

Imported recipe images should be copied to local storage.

Do not depend permanently on remote image URLs.

Failure to download the image must not prevent recipe import.

---

# 20. Recipe Instructions

Instructions should be structured rather than one enormous string.

Support:

- Ordered steps
- Instruction sections/groups
- Drag-and-drop reorder

Example:

```text
Sauce
1. Melt butter.
2. Add garlic.

Chicken
1. Season chicken.
2. Bake.
```

Per-step images are deferred.

---

# 21. Ingredients

Recipes select ingredients from a master ingredient database.

Recipe ingredient structure:

```text
Ingredient
Quantity
Unit
Preparation/Note
Optional
Ingredient Group
```

Example:

```text
Chicken Breast | 4 | each | boneless, skinless | false | Chicken
```

Ingredient search uses autocomplete underneath the input field.

If no match exists:

**Create Ingredient**

may be performed inline without leaving the recipe editor.

---

# 22. Ingredient Aliases

Ingredients may have aliases.

Example:

```text
Canonical:
Scallion

Aliases:
Green Onion
Spring Onion
```

Aliases should participate in:

- Search
- Autocomplete
- Recipe import matching

---

# 23. Ingredient Merging

Admin/reference-data editors may merge duplicate ingredients.

Example:

```text
Ground Beef
ground beef
Hamburger Meat
```

Merge into:

```text
Ground Beef
```

Merge operation must:

- Reassign every recipe reference
- Preserve historical relationships
- Preserve useful old names as aliases

---

# 24. Ingredient Deletion

Ordinary hard deletion is not allowed.

An Admin may permanently delete an ingredient **only when it is unused by every recipe**, including retired recipes.

If used:

Offer:

- Merge
- Retire/deactivate

Do not allow deletion.

---

# 25. Units

Canonical units ship with common values including:

```text
tsp
tbsp
fl oz
cup
pint
quart
gallon
oz
lb
g
kg
each
package
can
bottle
jar
bunch
clove
slice
pinch
to taste
```

Users may create custom units.

Once created, custom units become reusable master units.

---

# 26. Unit Aliases

Example:

```text
Canonical:
tbsp

Aliases:
tablespoon
tablespoons
Tbsp
T
```

Input/import should normalize aliases to the canonical value.

Full words should therefore become standardized abbreviations where applicable.

---

# 27. Quantities

Internally store precise numeric quantities.

Display friendly fractions when sensible.

Examples:

```text
0.5 → ½
1.5 → 1 ½
2.25 → 2 ¼
```

Do not store fractions as unstructured strings when avoidable.

---

# 28. Grocery Conversion

Compatible units should normalize.

Examples:

```text
1 lb + 8 oz → 1.5 lb
8 tbsp → ½ cup
4 cups → 1 quart
```

Do not convert across incompatible quantity dimensions.

Example:

```text
1 onion
½ cup chopped onion
```

remain separate.

Likewise:

```text
2 cans tomatoes
28 oz tomatoes
```

remain separate in V1.

Package-size intelligence is a future feature.

---

# 29. Primary Proteins

Recipe supports multiple Primary Protein values.

Configurable master list might initially include:

```text
Beef
Chicken
Pork
Turkey
Seafood
Lamb
Venison
Duck
Other
```

Users may create additional values.

Vegetarian/Vegan should **not** be represented as fake proteins.

Instead recipe metadata should separately support:

```text
Vegan
Vegetarian
```

These fields do not influence randomization in V1.

A future setting may enable dietary-category variety logic.

---

# 30. Tags

Recipes have flexible user-created tags.

Examples:

```text
Italian
Mexican
Pasta
Grill
Slow Cooker
Air Fryer
Comfort Food
```

Autocomplete should be used.

Imported website categories may suggest tags, but users control final values.

---

# 31. Favorite Recipes

Recipes may be marked Favorite.

Favorite is primarily:

- Display
- Filtering

but adds a small randomizer bonus.

Default conceptual weight:

```text
Normal   1.00
Favorite 1.25
```

Favorite weighting must never overpower stronger variety rules.

---

# 32. Randomization Eligibility

Only recipes with:

```text
Status = Normal
```

may be randomly selected.

Manual Only and Retired recipes still participate appropriately in historical/statistical calculations.

---

# 33. Randomization Window

For a candidate date, exact-recipe occurrence evaluation examines:

```text
7 days backward
14 days forward
```

This includes:

- Historical meals
- Today's planned meal
- Future planned meals

A future occurrence outside the active reroll operation must never be silently changed.

---

# 34. Randomizer Priority

Candidate evaluation order:

## Level 1 — Eligibility

Reject:

- Manual Only
- Retired

## Level 2 — Strong Exact-Recipe Avoidance

An exact recipe occurrence inside the protected window receives a severe penalty.

Conceptual relative probability:

```text
Clean candidate: 100%
Repeat-window candidate: approximately 10%
```

This is not an absolute prohibition.

If good candidates exist outside the window, repeat candidates should almost never win.

## Level 3 — Protein Variety

Apply soft penalty when candidate shares any Primary Protein with:

- immediately previous day
- immediately following day

The number of neighboring days inspected should be implemented through a configuration value.

V1 default:

```text
1
```

Even if the UI doesn't expose this setting immediately, avoid hard-coding the algorithm so that expansion is difficult.

## Level 4 — Usage Spacing

Prefer least-recently-used candidates.

When forced to repeat, maximize distance to the nearest occurrence in either direction.

Example:

```text
A: occurrence 5 days away
B: occurrence 10 days away

Prefer B.
```

## Level 5 — Favorite

Apply modest favorite bonus.

## Level 6 — Randomness

Use secure random entropy.

Do not use predictable date/database-based seeds.

Each generation operation should receive a unique generation identifier.

During development/debugging, support recording enough information to reproduce a problematic generation if desired.

---

# 35. Special Event Randomizer Influence

Special events are never generated randomly.

If a special event contains a recipe:

- It counts normally toward recipe statistics.
- It contributes groceries normally where applicable.
- Its effect on exact-recipe randomization should be extremely reduced.

This distinction must be explicit in the scoring model rather than accidentally tying statistics and scoring together.

---

# 36. Locks

Populated future/today entries may be locked.

Lock means:

**No automatic process may change this date.**

This includes:

- Individual reroll
- 7-day reroll
- Rest-of-month reroll
- Fill operation
- Batch generation

A user may:

- Unlock it
- Manually replace the meal

Manually changing a locked meal does **not** unlock it.

It remains locked until explicitly unlocked.

Blank dates cannot be locked.

---

# 37. Individual Reroll

Professional UI label:

**Reroll**

Behavior:

- Immediately chooses replacement
- No confirmation dialog
- Respects all rules
- Disabled when locked
- Shows temporary Undo notification

Desktop:

Expose reroll action on hover/context controls.

Mobile:

Expose within date detail.

---

# 38. Undo

V1 provides temporary **Undo Last Change** behavior.

Initially available to users who have permission to make the corresponding change.

Architecture should allow future restriction to Admin-only.

Undo is ideal for:

- Individual meal reroll
- Grocery mutation
- Similar recent operations

Do not attempt to turn Undo into full database version control.

---

# 39. Seven-Day Reroll

User selects a starting date.

Range:

```text
Selected date + following 6 days
```

Example:

Tuesday → following Monday.

Generate a preview.

Preview shows the proposed seven days, including locked dates unchanged.

Actions:

```text
Try Another
Apply Changes
Cancel
```

`Try Another` creates a new preview without saving anything.

`Apply Changes` saves the operation atomically.

Either the complete operation succeeds or no changes are committed.

---

# 40. Rest-of-Month Reroll

Starts from selected date.

Ends on the final day of that calendar month.

Same workflow as seven-day reroll:

```text
Preview
Try Another
Apply Changes
Cancel
```

Locked dates remain unchanged.

---

# 41. Fill Empty Days

Separate concept from rerolling.

**Fill Empty Days**

- Fills blank meal dates
- Leaves all existing entries untouched
- Skips special events
- Never modifies locked dates

**Reroll**

- Replaces existing eligible unlocked recipe dates

Keep these actions visually and semantically separate.

---

# 42. Vacation

Vacation exists specifically to block planning.

Vacation days:

- Cannot receive automatically generated meals
- Do not contribute groceries
- Appear visually as Vacation
- May use chosen special-event icon

---

# 43. Grocery List Model

The grocery list should not be a stale saved derivative.

It is a live projection of:

```text
Recipes inside selected date range
+
Manual grocery items
```

Therefore changing a meal immediately changes recipe-derived groceries.

Manual items remain untouched.

---

# 44. Grocery Date Ranges

Support:

```text
Next 3 Days
Next 7 Days
Next 14 Days
Rest of Month
Custom
```

Custom should support:

```text
Start Date
Number of Days
```

Do not artificially restrict grocery calculation to current month.

---

# 45. Grocery Aggregation

Ingredients referencing the same canonical ingredient and compatible units should combine.

Example:

```text
Tacos:
Ground Beef 2 lb

Meatloaf:
Ground Beef 1.5 lb

Burgers:
Ground Beef 8 oz
```

Output:

```text
Ground Beef — 4 lb
```

Supporting attribution:

```text
Tacos — Tue 10/6
Meatloaf — Thu 10/8
Burgers — Sat 10/10
```

---

# 46. Grocery Departments

Default departments:

```text
Produce
Meat & Seafood
Dairy & Eggs
Bakery
Deli
Frozen
Canned Goods
Dry Goods / Pantry
Spices & Seasonings
Condiments & Sauces
Beverages
Household
Personal Care
Other
```

Users can:

- Add
- Rename
- Reorder
- Disable/remove unused departments where safe

Department ordering controls display and print ordering.

This allows users to arrange lists according to the physical route through their preferred store.

---

# 47. Grocery Checkboxes

Checkbox state persists.

Checked item:

- Dim text
- Strike through text
- Remain in department
- Move below unchecked items in the same department

Unchecking immediately restores normal display.

Provide:

**Hide Checked Items**

When list range changes, preserve checked state for matching items when reasonable.

---

# 48. Staples

Provide reusable **Household Staples**.

Examples:

```text
Milk
Bread
Eggs
Coffee
Dog Food
Toilet Paper
```

Users can add staples to the current shopping list.

Manual non-staple item creation includes:

```text
Save for future
```

Reusable items carry a grocery department.

---

# 49. Grocery Display Styling

Staples/manual items should be visually distinguishable from recipe-derived items on screen.

Do not rely exclusively on color; use another indicator for accessibility.

Printed grocery lists should normalize the appearance and treat them as ordinary grocery items.

---

# 50. Mobile Grocery Experience

Mobile grocery view is a priority V1 screen.

Requirements:

- Large tap targets
- Large checkboxes
- Clear department headers
- Sticky department headings where appropriate
- Checked-item dim/strike
- Minimal visual clutter
- Live device synchronization

---

# 51. Grocery Printing

Printable grocery list:

- Group by department
- Follow user department order
- Include empty checkboxes
- Include recipe/date attribution
- Include manual items
- Exclude checked items by default

Print option:

```text
Include Checked Items
Yes / No
```

Default:

```text
No
```

---

# 52. Calendar Printing

Print monthly calendar in:

**Landscape**

One month per page.

Calendar cells display:

- Day number
- Recipe name OR special event

No:

- Recipe photographs
- Lock icon
- Protein information

Recipe names wrap rather than silently truncating important information.

A future V2 may add list-style calendar printing.

---

# 53. Recipe Browser

Default recipe view:

**Photo cards**

Primary emphasis:

- Image
- Recipe name

Secondary information:

- Last eaten
- Times eaten

Search/filter:

- Name
- Favorite
- Status
- Primary protein
- Tags
- Last eaten

Sorting:

- Name
- Recently eaten
- Least recently eaten
- Most eaten
- Favorites
- Recently added

Provide compact list mode for very large databases.

---

# 54. Manual Meal Selection

Use searchable autocomplete.

Selection result emphasizes:

1. Photo, when available
2. Recipe name
3. Last eaten
4. Historical count

If exact recipe falls inside the protected repeat window:

Display warning.

Example:

```text
Last eaten 5 days ago.
This recipe is inside your repeat-avoidance window.
```

User may still choose it.

Do not warn merely because Primary Protein matches a neighbor.

Manual decisions override soft preferences.

---

# 55. Recipe URL Import

Workflow:

```text
Paste URL
→ Import Recipe / Cancel
```

Import only begins after explicit user action.

Order of parsing:

## Step 1

Look for `schema.org/Recipe` JSON-LD.

## Step 2

If absent/unusable, perform best-effort HTML parsing.

## Step 3

If confidence is insufficient, do not invent fields.

Present extracted information for manual review.

---

# 56. Import Normalization

Automatically normalize only obvious things:

- Whitespace
- Fractions
- Known units
- Unit aliases
- Ingredient aliases
- Basic formatting

Do not make aggressive semantic guesses.

---

# 57. Multi-Recipe Pages

If a webpage exposes multiple recipes:

Ask the user which recipe to import.

Never silently choose the first recipe.

---

# 58. Import Review Screen

Imported content is never written directly into the permanent recipe database.

Review:

- Name
- Description
- Image
- Author
- Source
- Ingredients
- Quantities
- Units
- Notes
- Instructions
- Sections
- Prep time
- Cook time
- Tags

Then:

```text
Save Recipe
Cancel
```

---

# 59. Import Ingredient Matching

Example imported text:

```text
boneless skinless chicken breasts
```

Potential canonical match:

```text
Chicken Breast
```

Present:

- Accept
- Search different ingredient
- Create new ingredient

Do not silently create hundreds of nearly duplicated ingredients.

---

# 60. Duplicate Recipe Detection

V1 duplication detection:

1. Exact normalized Source URL
2. Similar/exact normalized recipe name

Existing URL match should generate a **very strong warning**.

Options may include:

```text
Open Existing Recipe
Import Anyway
Cancel
```

Similar-name warning should permit changing the new recipe's name when it is legitimately a different dish.

Complex duplicate detection based on ingredient similarity is V2.

---

# 61. Recipe Deletion

Normal workflow does not hard-delete recipes.

Use:

**Retire**

Retired recipes preserve:

- Statistics
- Historical references
- Recipe data

An Admin may permanently delete any recipe through a special extreme-confirmation administrative workflow—even one with history.

Implementation requirement:

If historical references exist and permanent deletion occurs, preserve historical meal text/statistical integrity through a snapshot/tombstone mechanism.

Do **not** leave broken foreign keys or erase what historically happened.

---

# 62. Statistics Page

V1 includes a dedicated Statistics page.

Potential V1 metrics:

- Most eaten recipes
- Least recently eaten recipes
- Meal count this month
- Meal count this year
- Primary protein distribution
- Eat Out count
- Family Dinner count
- Fend For Yourself count
- Vacation count

Keep visualization useful rather than turning V1 into an analytics product.

---

# 63. Meal History

Provide browsable historical dinner history.

Historical calendar navigation remains supported.

Past entries are read-only outside the Admin correction workflow.

---

# 64. Audit Log

Admin-only.

Record meaningful mutations such as:

- Meal changed
- Meal rerolled
- Lock/unlock
- Recipe created/changed/retired/deleted
- Grocery manual item changes
- Ingredient merge/retirement
- User changes
- Permission changes
- Historical corrections
- Backup restore
- Successful login
- Failed login

Do not log ordinary page views.

Audit entry should capture where appropriate:

```text
Actor
Timestamp
Action
Entity Type
Entity ID
Before
After
IP/context
```

Do not store sensitive password contents.

---

# 65. Dashboard

Dashboard should be a convenient overview rather than required workflow.

Possible cards:

```text
Tonight's Dinner
Tomorrow's Dinner
Upcoming Meals
Grocery List
Recipes
Statistics
Quick Actions
```

Calendar remains default unless user changes setting.

---

# 66. Backup Architecture

V1 supports manual encrypted backup and restore.

Backup should include:

- PostgreSQL application data
- Historical meal data
- Recipes
- Ingredients
- Settings
- Images
- Grocery configuration
- Staples
- Permissions
- Required application configuration

## Credential handling

Do not make a backup unrecoverable by encrypting it using a key that exists only inside the server being backed up.

At setup, Fate & Fridge generates a **backup recovery key**.

Store operational copy in persistent `/data/config`.

Admin receives ability to:

**Export Recovery Key**

The recovery key must be kept separately from the backup.

The application should not casually display the recovery key on ordinary screens.

---

# 67. User Credentials in Backup

Preferred V1 behavior:

Do not export reusable user password credentials as standalone secrets.

If database-level restoration necessarily contains password-verifier data, protect it inside the encrypted archive and never expose it independently.

For maximum portability/security, support forcing an Admin password reset after disaster recovery when required.

Never store plaintext passwords.

---

# 68. Restore

Restore requires Admin authorization.

Workflow:

1. Upload/select backup
2. Validate archive
3. Validate backup version
4. Validate recovery key
5. Explain that current data will be replaced
6. Require severe confirmation
7. Automatically make a safety backup of current installation
8. Perform restore transaction/process
9. Validate restored data
10. Restart/reload safely

---

# 69. Scheduled/Offsite Backups

Deferred to V2.

Potential future destinations:

- Local scheduled folder
- SMB/NFS
- OneDrive
- Google Drive
- S3-compatible storage

---

# 70. Database Migrations

Application startup checks schema version.

Normal migrations may run automatically.

Before any migration classified as destructive:

**Create database backup first.**

A migration failure must not leave an unnoticed half-upgraded database.

---

# 71. Update Checking

V1 periodically checks whether a new Fate & Fridge version is available.

UI may display:

```text
Version 1.x.x is available.
```

Do not silently update containers.

Actual update remains administrator-controlled through Docker.

Use semantic versioning:

```text
MAJOR.MINOR.PATCH
```

---

# 72. Offline Behavior

Without internet:

These continue working:

- Login
- Recipes
- Calendar
- Randomization
- Grocery lists
- Printing
- Statistics
- Administration
- Backups
- Local live synchronization

Unavailable gracefully:

- Website recipe imports
- Update check
- Future cloud integrations

Internet loss must never break the main application.

---

# 73. External Access

Fate & Fridge itself should not attempt to configure routers/firewalls.

Document common secure deployment possibilities later.

Optional setup configuration may allow an Admin to restrict accepted IP/network ranges.

Do not depend on IP filtering as the application's sole security mechanism when authentication is enabled.

---

# 74. Internal API

V1 frontend uses the application's own REST API.

Representative API families:

```text
/api/v1/auth
/api/v1/users
/api/v1/calendar
/api/v1/meals
/api/v1/recipes
/api/v1/ingredients
/api/v1/units
/api/v1/proteins
/api/v1/tags
/api/v1/groceries
/api/v1/statistics
/api/v1/settings
/api/v1/import
/api/v1/admin/audit
/api/v1/admin/history
/api/v1/admin/backup
```

Design endpoints around resources and actions, not whatever happens to be convenient for React.

---

# 75. Future Voice Integration Contract

Future Alexa/Google Home integrations should be able to support operations such as:

```text
What's for dinner today?
What's for dinner tomorrow?
What's for dinner Friday?
Add milk to the grocery list.
Read my grocery list.
Reroll Friday.
Set Saturday to tacos.
What's coming up this week?
```

Do not implement voice integration in V1.

Design the API so it doesn't require rewriting core planner logic later.

---

# 76. V1 Explicitly Does NOT Include

Prevent scope creep.

V1 does not include:

- Pantry inventory
- Ingredient stock deduction
- Recipe serving scaling
- AI recipe interpretation
- Alexa integration
- Google Home integration
- Public API keys
- Two-factor authentication
- Multiple households per installation
- Automatic scheduled backups
- Cloud backup providers
- Automatic holiday calendars
- Advanced duplicate recipe AI
- Package/can-size conversion
- Vegan/vegetarian randomization rules
- Configurable advanced cuisine scheduling
- Nutrition tracking
- Calories/macros
- Meal-plan variants
- Automatic application updating
- Per-step recipe photos
- Printed agenda/list calendar

---

# 77. V2 Backlog

Candidate V2 work:

## Pantry / Inventory

Track what is currently available.

Automatically subtract pantry inventory from groceries.

## Serving Scaling

Use daily diner count to scale ingredient quantities.

## Package Intelligence

Understand:

```text
2 × 14 oz cans
```

and shopping package sizes.

## Dietary Randomization

Allow configurable rules involving:

- Vegan
- Vegetarian
- Dietary restrictions

## Advanced Variety Rules

Potential settings:

- Cuisine frequency
- Pasta frequency
- Protein frequency
- Cooking-method frequency
- Wider protein-neighbor window

## Holidays

Configurable regional models:

```text
US
UK
Australia
etc.
```

## AI Import Helper

Optional providers:

- Local model
- OpenAI-compatible
- Other

Never make AI mandatory.

## Voice

Alexa / Google Home.

## External API Credentials

Scoped service credentials.

## Automatic Backups

Including cloud/offsite destinations.

## 2FA

## Advanced Duplicate Detection

## List-Style Calendar Printing

---

# 78. Core Database Entities

At minimum expect entities corresponding to:

```text
household_settings

users
user_permissions
auth_events

recipes
recipe_images
recipe_instruction_sections
recipe_instruction_steps

ingredients
ingredient_aliases

units
unit_aliases

recipe_ingredients
recipe_ingredient_groups

proteins
recipe_proteins

tags
recipe_tags

calendar_entries
calendar_entry_history

grocery_departments
manual_grocery_items
grocery_staples
grocery_check_state

audit_events

application_settings

backup_metadata
```

Do not prematurely denormalize core relational information.

---

# 79. Calendar Entry Snapshotting

Calendar history must survive future recipe edits/deletions.

A historical meal should preserve enough snapshot information to answer:

> What did we actually have that night?

even if:

- Recipe is renamed
- Recipe is retired
- Admin permanently deletes it

Therefore historical calendar entries should preserve critical display information independently of the live recipe record.

---

# 80. Randomization Service Boundary

Randomization logic should live in a dedicated domain/service layer.

Do not implement randomization inside:

- API controller
- React component
- database model hooks

Conceptually:

```text
PlannerService
RandomizationEngine
CandidateScorer
```

This makes the algorithm independently testable.

---

# 81. Grocery Service Boundary

Likewise:

```text
GroceryAggregationService
UnitConversionService
```

Unit conversion requires explicit conversion families.

Never guess.

---

# 82. Import Service Boundary

Separate:

```text
RecipeImportService
StructuredRecipeParser
HtmlFallbackParser
IngredientMatcher
ImportNormalizer
```

Website-specific hacks must not leak through the rest of the application.

---

# 83. Frontend Screen Map

## Calendar

- Month/agenda
- Date detail
- Reroll
- Lock
- Manual selection
- Special events
- Generate/fill
- Batch reroll
- Print

## Dashboard

- Tonight
- Tomorrow
- Upcoming
- Grocery shortcut
- Recipe shortcut
- Stats shortcut

## Recipes

- Card browser
- Compact list
- Filters
- Recipe details
- Create
- Edit
- Import

## Groceries

- Date range
- Department sections
- Checkboxes
- Staples
- Manual additions
- Print

## Statistics

- Recipe/use statistics
- Household meal trends
- History

## Settings

### Household
### Users
### Permissions
### Ingredients
### Units
### Proteins
### Tags
### Grocery Departments
### Staples
### Appearance/Startup
### Security
### Backup
### Updates
### Administration

---

# 84. Responsive Requirements

Mobile support is mandatory.

Test common widths including approximately:

```text
360px
390px
768px
1024px+
```

No primary action may require hover on touchscreen.

Desktop hover behavior must always have an equivalent tap interaction.

---

# 85. Accessibility

Require:

- Keyboard navigation
- Semantic form controls
- Accessible modal focus management
- Proper labels
- Sufficient visual contrast
- Status indicated by more than color
- Large mobile tap targets
- Screen-reader-friendly checkbox labels

---

# 86. Project Repository

Suggested structure:

```text
fate-and-fridge/
│
├── AGENTS.md
├── README.md
├── CHANGELOG.md
├── compose.yaml
├── Dockerfile
├── .env.example
│
├── docs/
│   ├── PRODUCT_SPEC.md
│   ├── ARCHITECTURE.md
│   ├── DATABASE.md
│   ├── API.md
│   ├── RANDOMIZER.md
│   ├── SECURITY.md
│   ├── TESTING.md
│   ├── DEPLOYMENT.md
│   └── V2_BACKLOG.md
│
├── backend/
│   ├── src/FateAndFridge.Api/
│   │   ├── Program.cs
│   │   ├── Endpoints/
│   │   ├── Auth/
│   │   ├── Domain/
│   │   ├── Data/
│   │   ├── Repositories/
│   │   ├── Services/
│   │   ├── Imports/
│   │   ├── Hubs/
│   │   └── Admin/
│   ├── tests/FateAndFridge.Api.Tests/
│   ├── FateAndFridge.slnx
│   └── Directory.Build.props
│
├── frontend/
│   ├── src/
│   │   ├── api/
│   │   ├── components/
│   │   ├── features/
│   │   ├── pages/
│   │   ├── hooks/
│   │   ├── types/
│   │   └── utils/
│   └── tests/
│
└── scripts/
```

---

# 87. AI Development Roles

Use three independent roles.

## Lead Developer

Owns architecture.

Responsibilities:

- Read specification
- Break work into issues
- Define interfaces
- Control implementation order
- Review architectural consequences
- Prevent scope creep
- Approve migrations
- Approve public/internal API changes

Lead should generally avoid implementing large features itself unless needed to unblock work.

---

## Implementer

Works one issue at a time.

Requirements:

1. Read relevant specification.
2. Inspect existing implementation.
3. Create tests.
4. Implement smallest correct change.
5. Run tests.
6. Run lint/type checking.
7. Report exactly what changed.
8. Do not silently expand scope.

The Implementer does **not** redefine requirements.

If requirements appear contradictory:

Stop that portion and escalate the contradiction to Lead.

---

## Independent Reviewer

Must assume the implementation may be subtly wrong.

Review for:

- Requirement compliance
- Security
- Authorization bypass
- Database integrity
- Race conditions
- Date/timezone problems
- Randomizer correctness
- Accessibility
- Mobile usability
- Error handling
- Migration safety
- Test coverage
- Scope creep

Reviewer should independently run tests and reproduce acceptance criteria.

Reviewer should not approve code merely because tests written by the Implementer pass.

---

# 88. AI Git Workflow

Recommended:

```text
main
develop
feature/<issue>
fix/<issue>
```

Each substantial feature gets an issue.

Implementer:

```text
feature/123-randomizer
```

Pull request must contain:

- Requirement
- Implementation summary
- Tests added
- Migration impact
- Screenshots where UI changed
- Known limitations

Reviewer leaves findings.

Lead performs final merge decision.

---

# 89. Definition of Done

An issue is not finished because:

> The code is written.

It is finished when:

- Requirement implemented
- Automated tests pass
- Authorization tested
- Mobile behavior tested where relevant
- Error states handled
- Database migration tested if applicable
- Documentation updated
- Reviewer accepts it
- No unrelated scope introduced

---

# 90. Build Milestones

## Milestone 0 — Project Skeleton

Deliver:

- `Repository`
- Docker Compose
- ASP.NET Core service
- React frontend
- PostgreSQL
- Health checks
- Migration framework
- CI
- Formatting/linting/testing

Acceptance:

```text
docker compose up
```

produces a usable application shell.

---

## Milestone 1 — Setup / Household / Authentication

Implement:

- Setup wizard
- Single-user mode
- Multi-user mode
- Admin
- Members
- Permissions
- Argon2id
- Login protections
- Audit auth events

---

## Milestone 2 — Reference Data

Implement:

- Ingredients
- Ingredient aliases
- Merge
- Units
- Unit aliases
- Proteins
- Tags
- Grocery departments

---

## Milestone 3 — Recipes

Implement:

- CRUD
- Status
- Favorites
- Images
- Ingredient groups
- Instruction sections
- Recipe statistics base
- Search
- Filters
- Compact/card views

---

## Milestone 4 — Recipe Import

Implement:

- URL entry
- JSON-LD Recipe parser
- HTML fallback
- Normalizer
- Ingredient matching
- Duplicate warnings
- Review screen
- Image localization

---

## Milestone 5 — Calendar Core

Implement:

- Month view
- Agenda view
- Date details
- Special days
- Locks
- Manual recipe selection
- Historical immutability
- Future navigation

---

## Milestone 6 — Randomization Engine

Implement:

- Candidate filters
- 7-day backward rule
- 14-day forward rule
- Repeat penalty
- Protein penalty
- Last-used spacing
- Favorite weighting
- Secure randomness
- Individual Reroll
- Undo

Do not implement this milestone until extensive deterministic unit tests exist around scoring.

---

## Milestone 7 — Batch Planning

Implement:

- Fill Empty Days
- 7-day reroll preview
- Try Another
- Atomic Apply
- Rest-of-month reroll
- Locked-date handling

---

## Milestone 8 — Grocery System

Implement:

- Aggregator
- Unit conversion
- Departments
- Attribution
- Date ranges
- Manual items
- Staples
- Persistent checkboxes
- Hide checked
- Mobile shopping mode

---

## Milestone 9 — Live Synchronization

Implement:

- Grocery live updates
- Calendar live updates
- Multi-device state refresh

Test two simultaneous browser sessions.

---

## Milestone 10 — Printing

Implement:

- Landscape calendar print
- Grocery print
- Department ordering
- Recipe attribution
- Print checked-item option

---

## Milestone 11 — Statistics / History

Implement:

- Statistics page
- Recipe statistics
- Historical browser
- Special-event statistics

---

## Milestone 12 — Administration

Implement:

- Audit viewer
- Historical correction
- Extreme recipe deletion
- Ingredient safety rules
- Admin password resets

---

## Milestone 13 — Backup / Restore / Updates

Implement:

- Recovery key
- Encrypted archive
- Key export
- Manual backup
- Restore validation
- Pre-restore safety backup
- Update checker
- Version display

---

## Milestone 14 — Hardening

Perform:

- Permission attack testing
- Historical editing bypass tests
- Input fuzzing
- URL-import security tests
- SSRF prevention
- Authentication rate-limit testing
- Backup corruption testing
- Timezone rollover testing
- Multiuser concurrency testing
- Mobile testing
- Accessibility review

---

# 91. Critical Automated Tests

The AI team must not treat testing as optional.

Especially test:

### Randomizer

- Doesn't select Retired
- Doesn't select Manual Only
- Strongly suppresses recent exact recipes
- Uses 7 days backward
- Uses 14 days forward
- Does not alter future dates outside reroll range
- Respects locks
- Handles small recipe databases
- Handles every candidate being a repeat
- Handles multiple proteins
- Favorite never defeats stronger constraint
- Unique runs produce varied results

### Dates

- Midnight timezone rollover
- DST transitions
- Today editable
- Yesterday immutable
- Far-future planning
- Month boundaries
- Year boundaries
- Leap years

### Grocery

- oz/lb conversion
- tbsp/cup conversion
- incompatible units stay separate
- manual items survive recalculation
- checkbox persistence
- attribution
- reroll recalculation

### Permissions

Every write endpoint must be tested with:

- Admin
- Authorized Member
- Unauthorized Member
- Unauthenticated request where applicable

Never trust frontend hiding as authorization.

---

# 92. Recipe Import Security

This feature deserves special attention because it makes outbound HTTP requests.

Protect against SSRF.

Importer must reject or carefully control requests to:

- localhost
- Docker internal services
- private/internal IP ranges
- metadata-service addresses
- non-HTTP protocols

Enforce:

- URL validation
- request timeout
- response-size limits
- content-type checking
- redirect limits
- image-size limits

Never execute JavaScript from imported sites.

---

# 93. Update Check Privacy

Update check should request only information necessary to determine current version availability.

No:

- Recipe information
- Household statistics
- User counts
- Usage analytics
- Installation fingerprint

Zero telemetry remains the default philosophy.

---

# 94. Acceptance Scenario

A V1 release candidate should successfully demonstrate this complete workflow:

1. Install Fate & Fridge on a fresh Docker host.
2. Open browser.
3. Choose Multiuser.
4. Create Admin.
5. Add household member.
6. Configure child's read-only permissions.
7. Import several recipes from URLs.
8. Manually create another recipe.
9. Create ingredient aliases.
10. Mark one recipe Favorite.
11. Mark another Manual Only.
12. Retire another.
13. Generate meals.
14. Verify recent meals are strongly avoided.
15. Lock Wednesday.
16. Reroll a seven-day period.
17. Confirm Wednesday remains untouched.
18. Preview another randomization.
19. Apply it.
20. Reroll one day.
21. Undo.
22. Mark Saturday Eat Out.
23. Mark Sunday Host Family Dinner with recipe.
24. Mark following week Vacation.
25. Generate groceries.
26. Confirm recipe quantities aggregate.
27. Add household staples.
28. Add manual grocery item.
29. Check grocery item on Phone A.
30. Verify Phone B updates.
31. Print grocery list.
32. Print calendar.
33. Allow day to become historical.
34. Confirm normal editing is rejected.
35. Correct it through Admin historical correction.
36. Verify audit event.
37. Create encrypted backup.
38. Export recovery key.
39. Restore into clean installation.
40. Verify recipes, history, images, settings and permissions survive.

If all forty behaviors work as expected, we're approaching a genuine V1 rather than merely a demo.

---

# 95. Product Rule

Every AI working on Operation Dinner Plate should operate under this rule:

> Fate & Fridge should feel like a simple household meal planner hiding a sophisticated database—not a sophisticated database pretending to be a meal planner.

Complexity belongs behind the interface.

The ordinary household member should primarily see:

**What's for dinner?**

**What's coming up?**

**What do we need from the store?**

Everything else exists to make those three answers better.

---

# 96. Scope Freeze

This document defines the V1 product baseline.

From this point forward, new feature ideas should normally be classified as:

```text
V1 blocker
V1.x enhancement
V2 candidate
Rejected
```

They should not automatically enter V1.

This protects Operation Dinner Plate from becoming an endless AI-generated development project that never reaches a stable release.