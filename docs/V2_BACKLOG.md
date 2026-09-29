# Fate & Fridge — V2 Backlog

This file contains ideas explicitly deferred from V1.

Items here are not automatically approved for V2.

Each requires separate prioritization.

---

## Pantry / Inventory
- track pantry/fridge/freezer inventory
- deduct on-hand ingredients from grocery output
- expiration dates
- package quantities
- pantry staples as actual stock

---

## Serving Scaling
- recipe base serving size
- automatic ingredient scaling by diner count
- per-date scale override

---

## Package Intelligence
Examples:
- 2 x 14 oz cans
- jar sizes
- bottle sizes
- package-level shopping optimization

Potential grocery output:
- "Buy 2 cans"

---

## Dietary Scheduling Rules
Optional user-configured randomization preferences:
- vegan frequency
- vegetarian frequency
- dietary restrictions
- allergy exclusions

---

## Advanced Variety Rules
Possible settings:
- same-protein lookback/lookahead distance
- cuisine frequency
- pasta frequency
- cooking method frequency
- beef/chicken caps per week
- meatless meal targets
- grilling/slow-cooker frequency

---

## Seasonal Weighting
- seasonal recipes
- winter/summer preferences
- optional month/season availability

---

## Holiday Models
Selectable regional holiday packs:
- US
- UK
- Australia
- others

Holiday packs should remain optional.

---

## AI Import Helper
Optional only.

Potential providers:
- local model
- OpenAI-compatible endpoint
- other configurable provider

Potential use:
- parse ambiguous ingredient strings
- suggest ingredient mappings
- clean instructions

Core product must remain usable without AI.

---

## Voice Integrations
- Alexa
- Google Home
- possibly Home Assistant

Example actions:
- what's for dinner today
- what's for dinner tomorrow
- add milk to groceries
- read grocery list
- reroll Friday
- set Saturday to tacos

---

## External API Credentials
Service-account/API credential model.

Scoped permissions:
- calendar:read
- calendar:write
- grocery:read
- grocery:write
- recipes:read

---

## 2FA
Potential:
- TOTP
- passkeys later

---

## Scheduled Backups
User-configurable schedule.

Targets:
- local folder
- SMB/NFS
- S3-compatible
- OneDrive
- Google Drive

---

## Advanced Duplicate Detection
Potential signals:
- ingredient overlap
- title similarity
- instruction similarity
- source-site canonical URL
- optional AI assistance

---

## Statistics Expansion
Potential:
- times planned
- average days between repeats
- favorite usage
- cuisine trends
- grocery ingredient frequency
- household preference trends

---

## Printing Enhancements
- list-style calendar print
- weekly print
- custom grocery print templates
- recipe packet printing

---

## Nutrition
Optional:
- calories
- macros
- nutrition imports
- dietary summary

Not required by core planner.

---

## Native Apps
Potential later:
- PWA install
- iOS/Android wrapper
- native client using same API
