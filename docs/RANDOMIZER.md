# Fate & Fridge — Randomization Specification

## 1. Purpose
The randomizer should create varied dinner plans while avoiding repetitive exact recipes and reducing back-to-back protein repetition.

It must feel random without behaving carelessly.

---

## 2. Eligible Recipes
Only recipes with:

```text
status = Normal
```

are eligible for automatic selection.

Never randomly select:
- Manual Only
- Retired

Those recipes may still influence historical/statistical context where specified.

---

## 3. Date Context Window
For candidate date D:

### Exact Recipe Window
Inspect:
- 7 calendar days before D
- 14 calendar days after D

Include:
- historical meals
- today's meal
- future scheduled meals

Do not automatically modify meals outside the active reroll range.

---

## 4. Locked Dates
Locks do not change scoring weight.

A locked planned occurrence is simply a planned occurrence.

Locks control mutation eligibility, not how much an occurrence "counts."

---

## 5. Special-Day Recipe Occurrences
If a special event contains a recipe:
- count normally for recipe statistics
- give it an extremely reduced exact-repeat penalty contribution

Keep statistics and randomization weighting separate.

---

## 6. Protein Context
V1 checks direct neighbors only:
- previous day
- next day

If candidate shares any Primary Protein with either neighbor, apply a soft penalty.

Default configurable neighbor distance:
```text
1
```

Design code so later settings can expand this.

Vegetarian/Vegan metadata does not affect V1 scoring.

---

## 7. Candidate Scoring Priority

### Phase A — Hard Eligibility
Reject if:
- recipe not Normal
- recipe otherwise explicitly unavailable due to future rules

### Phase B — Exact Repeat Penalty
A clean candidate has normal weight.

Candidate with exact occurrence inside protected window should have roughly 10% relative selection weight compared with an otherwise equivalent clean candidate.

Interpretation:
- strongly avoided
- not absolutely forbidden

If clean candidates exist, inside-window repeats should rarely win.

### Phase C — Protein Variety
Apply moderate penalty for shared direct-neighbor protein.

This is weaker than exact-recipe spacing.

### Phase D — Spacing / Recency
Prefer recipes with greater distance to nearest occurrence in either direction.

When forced to repeat, maximize minimum spacing.

Example:

Candidate A nearest occurrence = 5 days
Candidate B nearest occurrence = 10 days

Prefer B.

### Phase E — Favorite Bonus
Normal:
```text
1.00
```

Favorite:
```text
1.25
```

Favorite bonus is subordinate to:
- exact repeat penalty
- protein penalty
- spacing preference

### Phase F — Randomness
Randomize among similarly suitable candidates.

Use cryptographically strong/random OS entropy.

Do not derive deterministic user-facing seed from:
- date
- recipe count
- database id

Each generation should receive a unique generation/operation identifier.

For debugging, record enough metadata to reproduce or analyze unexpected outcomes when feasible.

---

## 8. Suggested Scoring Model
Exact numeric implementation may evolve, but relative behavior must hold.

One possible structure:

```text
score = base_random_weight
score *= repeat_multiplier
score *= protein_multiplier
score *= favorite_multiplier
score *= spacing_factor
```

Example conceptual values:
- clean repeat multiplier: 1.0
- repeat-window multiplier: 0.10
- special-day repeat contribution: near-negligible
- shared-protein multiplier: moderate reduction, e.g. 0.6-0.8
- favorite: 1.25

Do not adopt these numbers blindly without tests.

Behavior matters more than one exact formula.

---

## 9. Batch Generation
For multi-day generation:
- generate within active range only
- account for meals already chosen earlier in the same preview
- account for immutable/locked dates
- account for future meals outside range
- never persist preview state until Apply

"Try Another" creates a fresh proposal.

---

## 10. Seven-Day Reroll
Range:
- selected date
- next 6 dates

Locked dates remain unchanged.

Preview contains all seven days.

Apply atomically.

---

## 11. Rest-of-Month Reroll
Range:
- selected date
- final date of same calendar month

Same rules as seven-day reroll.

---

## 12. Fill Empty Days
Only blank eligible dates are filled.

Do not alter:
- existing meals
- special events
- vacation days
- locked entries

---

## 13. Single-Day Reroll
Immediate.

No confirmation.

Must:
- respect lock
- respect same scoring rules
- emit operation id for temporary undo
- broadcast live update

---

## 14. Manual Selection
Manual selection is not scored.

User may choose a recipe even when inside protected repeat window.

UI warning:
- exact recipe repeat only

Do not warn merely for same protein.

---

## 15. Tests Required

### Eligibility
- Normal allowed
- Manual Only never auto-selected
- Retired never auto-selected

### Exact Window
- 7 days backward included
- 8 days backward outside backward window
- 14 days forward included
- 15 days forward outside forward window

### Future Plan
- future occurrence affects scoring
- future occurrence outside reroll range is never modified

### Locks
- locked dates remain untouched
- locked meals still influence context normally

### Protein
- previous day match penalized
- next day match penalized
- multiple protein overlap penalized
- unrelated proteins not penalized

### Favorites
- favorite slightly favored among equivalent candidates
- favorite does not beat strong repeat penalty

### Forced Repeat
- chooses larger nearest-occurrence spacing

### Small Recipe Pool
- still produces result when every candidate is within repeat window

### Batch
- preview does not persist
- Try Another changes proposal
- Apply is atomic
- partial failure rolls back

### Randomness
- repeated runs with same DB are not trivially identical
- secure randomness API used
