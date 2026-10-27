import type { ReactNode } from 'react'

import {
  CALENDAR_ENTRY_LABELS,
  CALENDAR_ENTRY_TYPES,
  CALENDAR_ICONS,
  ICON_ENTRY_TYPES,
  type CalendarEntryType,
} from '../../api/calendar'
import { formatHouseholdDate } from '../../lib/dateFormat'
import { useRecipeList } from '../recipes/useRecipes'
import { entryTypeChangePatch, type CalendarEntryFieldsValue } from './calendarEntryFields'

const TYPE_LABELS = CALENDAR_ENTRY_TYPES.map((type) => ({
  value: type,
  label: CALENDAR_ENTRY_LABELS[type],
}))

/**
 * Renders the per-type entry fields. Presentational only: the caller owns the
 * value and decides what to do with it (save or continue to confirmation).
 * `repeatWarning` lets the ordinary editor inject its repeat notice without
 * coupling these fields to the repeat-check query.
 */
export function CalendarEntryFields({
  value,
  onChange,
  idPrefix = 'calendar',
  disabled = false,
  repeatWarning,
}: {
  value: CalendarEntryFieldsValue
  onChange: (patch: Partial<CalendarEntryFieldsValue>) => void
  idPrefix?: string
  disabled?: boolean
  repeatWarning?: ReactNode
}) {
  const recipeResults = useRecipeList(
    value.recipeSearch.trim().length >= 2 ? { search: value.recipeSearch, limit: 5 } : { limit: 5 },
  )

  return (
    <>
      <label className="calendar-detail__field" htmlFor={`${idPrefix}-entry-type`}>
        <span>Type</span>
        <select
          id={`${idPrefix}-entry-type`}
          value={value.entryType}
          disabled={disabled}
          onChange={(event) => {
            const nextType = event.target.value as CalendarEntryType
            onChange(entryTypeChangePatch(nextType))
          }}
        >
          {TYPE_LABELS.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </label>

      {(value.entryType === 'recipe' ||
        value.entryType === 'host_family_dinner' ||
        value.entryType === 'holiday' ||
        value.entryType === 'misc') && (
        <div className="calendar-detail__field">
          <label htmlFor={`${idPrefix}-recipe-search`}>
            {value.entryType === 'recipe' ? 'Recipe (required)' : 'Recipe (optional)'}
          </label>
          <input
            id={`${idPrefix}-recipe-search`}
            type="text"
            autoComplete="off"
            placeholder="Search recipes"
            value={value.recipeSearch}
            disabled={disabled}
            onChange={(event) => onChange({ recipeSearch: event.target.value })}
          />
          {value.recipeSearch.trim().length >= 2 &&
            (recipeResults.data?.items ?? []).length > 0 && (
              <ul className="calendar-detail__results">
                {(recipeResults.data?.items ?? []).map((candidate) => (
                  <li key={candidate.id}>
                    <button
                      type="button"
                      disabled={disabled}
                      onClick={() =>
                        onChange({
                          recipeId: candidate.id,
                          recipeName: candidate.name,
                          recipeSearch: '',
                        })
                      }
                    >
                      {candidate.image && (
                        <img className="calendar-detail__thumb" src={candidate.image.url} alt="" />
                      )}
                      <span className="calendar-detail__result-name">{candidate.name}</span>
                      <span className="calendar-detail__result-meta">
                        {candidate.last_eaten
                          ? `Last eaten ${formatHouseholdDate(candidate.last_eaten)}`
                          : 'Not eaten yet'}
                        {candidate.times_eaten != null && ` · ${candidate.times_eaten}×`}
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          {value.recipeId !== null && (
            <p className="form__hint">
              Selected: <strong>{value.recipeName}</strong>{' '}
              <button
                type="button"
                disabled={disabled}
                onClick={() => onChange({ recipeId: null, recipeName: null })}
              >
                Clear
              </button>
            </p>
          )}
          {repeatWarning}
        </div>
      )}

      {(value.entryType === 'host_family_dinner' ||
        value.entryType === 'holiday' ||
        value.entryType === 'misc') && (
        <label className="calendar-detail__field" htmlFor={`${idPrefix}-custom-label`}>
          <span>{value.entryType === 'misc' ? 'Label (required)' : 'Name (optional)'}</span>
          <input
            id={`${idPrefix}-custom-label`}
            type="text"
            autoComplete="off"
            value={value.customLabel}
            disabled={disabled}
            onChange={(event) => onChange({ customLabel: event.target.value })}
          />
        </label>
      )}

      {value.entryType === 'eat_out' && (
        <label className="calendar-detail__field" htmlFor={`${idPrefix}-restaurant`}>
          <span>Restaurant (optional)</span>
          <input
            id={`${idPrefix}-restaurant`}
            type="text"
            autoComplete="off"
            value={value.restaurantName}
            disabled={disabled}
            onChange={(event) => onChange({ restaurantName: event.target.value })}
          />
        </label>
      )}

      {value.entryType === 'go_to_family_dinner' && (
        <label className="calendar-detail__field" htmlFor={`${idPrefix}-location`}>
          <span>Host / location (optional)</span>
          <input
            id={`${idPrefix}-location`}
            type="text"
            autoComplete="off"
            value={value.locationText}
            disabled={disabled}
            onChange={(event) => onChange({ locationText: event.target.value })}
          />
        </label>
      )}

      {ICON_ENTRY_TYPES.includes(value.entryType) && (
        <label className="calendar-detail__field" htmlFor={`${idPrefix}-icon`}>
          <span>Icon (optional)</span>
          <select
            id={`${idPrefix}-icon`}
            value={value.iconKey}
            disabled={disabled}
            onChange={(event) => onChange({ iconKey: event.target.value })}
          >
            <option value="">No icon</option>
            {CALENDAR_ICONS.map((icon) => (
              <option key={icon} value={icon}>
                {icon}
              </option>
            ))}
          </select>
        </label>
      )}

      {value.entryType === 'host_family_dinner' && (
        <label className="calendar-detail__field" htmlFor={`${idPrefix}-diner-count`}>
          <span>Diner count (optional)</span>
          <input
            id={`${idPrefix}-diner-count`}
            type="text"
            inputMode="numeric"
            value={value.dinerCount}
            disabled={disabled}
            onChange={(event) => onChange({ dinerCount: event.target.value })}
          />
        </label>
      )}

      <label className="calendar-detail__field" htmlFor={`${idPrefix}-notes`}>
        <span>Notes (optional)</span>
        <textarea
          id={`${idPrefix}-notes`}
          rows={3}
          value={value.notes}
          disabled={disabled}
          onChange={(event) => onChange({ notes: event.target.value })}
        />
      </label>
    </>
  )
}
