import { useState, type FormEvent } from 'react'

import {
  GROCERY_RANGE_PRESETS,
  type GroceryRange,
  type GroceryRangePreset,
} from '../../api/groceries'
import { formatRangeSummary } from './groceryFormat'

type FixedPreset = Exclude<GroceryRangePreset, 'custom'>

const DEFAULT_DAYS = '7'

/**
 * Range selector (docs/M8_PLAN.md §1.7): one-tap presets plus a Custom
 * start/day-count form. The last choice is persisted server-side through
 * preferences, so the server — not this component — remembers the range.
 *
 * The custom fields seed from the resolved range but are user-editable; local
 * overrides (not effects) keep them in sync so no state is set during render
 * side effects.
 */
export function RangeSelector({
  range,
  pending,
  onSelectPreset,
  onSelectCustom,
}: {
  range: GroceryRange | null
  pending: boolean
  onSelectPreset: (preset: FixedPreset) => void
  onSelectCustom: (start: string, days: number) => void
}) {
  const [customOpen, setCustomOpen] = useState(false)
  const [startOverride, setStartOverride] = useState<string | null>(null)
  const [daysOverride, setDaysOverride] = useState<string | null>(null)

  const start = startOverride ?? range?.start ?? ''
  const days = daysOverride ?? (range ? String(range.day_count) : DEFAULT_DAYS)

  const selected = range?.preset ?? null
  const controlsDisabled = pending

  function handleCustomSubmit(event: FormEvent<HTMLFormElement>): void {
    event.preventDefault()
    const parsedDays = Number(days)
    if (start.length === 0 || !Number.isInteger(parsedDays) || parsedDays < 1) {
      return
    }
    onSelectCustom(start, parsedDays)
  }

  return (
    <div className="grocery-range" aria-label="Grocery range">
      <div className="grocery-range__presets" role="group" aria-label="Range presets">
        {GROCERY_RANGE_PRESETS.map((preset) => (
          <button
            key={preset.value}
            type="button"
            className="grocery-range__preset"
            aria-pressed={selected === preset.value}
            disabled={controlsDisabled}
            onClick={() => onSelectPreset(preset.value)}
          >
            {preset.label}
          </button>
        ))}
        <button
          type="button"
          className="grocery-range__preset"
          aria-pressed={selected === 'custom'}
          aria-expanded={customOpen}
          aria-controls="grocery-range-custom"
          disabled={controlsDisabled}
          onClick={() => setCustomOpen((open) => !open)}
        >
          Custom
        </button>
      </div>

      {customOpen && (
        <form
          id="grocery-range-custom"
          className="grocery-range__custom"
          aria-label="Custom range"
          noValidate
          onSubmit={handleCustomSubmit}
        >
          <div className="form__field">
            <label htmlFor="grocery-range-start">Start date</label>
            <input
              id="grocery-range-start"
              type="date"
              value={start}
              disabled={controlsDisabled}
              onChange={(event) => setStartOverride(event.target.value)}
            />
          </div>
          <div className="form__field">
            <label htmlFor="grocery-range-days">Days</label>
            <input
              id="grocery-range-days"
              type="number"
              min={1}
              max={366}
              value={days}
              disabled={controlsDisabled}
              onChange={(event) => setDaysOverride(event.target.value)}
            />
          </div>
          <button type="submit" className="button--primary" disabled={controlsDisabled}>
            Apply
          </button>
        </form>
      )}

      <p className="grocery-range__summary" aria-live="polite">
        {range
          ? `${formatRangeSummary(range.start, range.end)} · ${range.day_count} ${
              range.day_count === 1 ? 'day' : 'days'
            }`
          : 'Loading range…'}
      </p>
    </div>
  )
}
