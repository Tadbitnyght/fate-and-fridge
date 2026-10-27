import { describe, expect, it } from 'vitest'

import type { CalendarEntry, CalendarEntryType } from '../../api/calendar'
import { printEntryText, printIconKey, printMonthHeading } from './calendarPrintFormat'

function entry(overrides: Partial<CalendarEntry> = {}): CalendarEntry {
  return {
    id: 1,
    entry_type: 'recipe',
    recipe: { id: 5, name: 'Lasagna', image_url: null, status: 'normal' },
    display_text: 'Lasagna',
    is_locked: false,
    diner_count: null,
    effective_diner_count: 4,
    restaurant_name: null,
    location_text: null,
    custom_label: null,
    notes: null,
    icon_key: null,
    include_groceries: true,
    blocks_automation: false,
    created_at: '2026-11-01T00:00:00+00:00',
    updated_at: '2026-11-01T00:00:00+00:00',
    ...overrides,
  }
}

describe('calendar print formatting', () => {
  it('renders the month/year heading without the household name', () => {
    expect(
      printMonthHeading({ year: 2026, monthIndex: 9, key: '2026-10', label: 'October 2026' }),
    ).toBe('October 2026')
  })

  it('prints the recipe name for a recipe entry', () => {
    expect(printEntryText(entry({ entry_type: 'recipe', display_text: 'Lasagna' }))).toBe('Lasagna')
  })

  it('prints the special-event text for icon-bearing types', () => {
    expect(
      printEntryText(
        entry({ entry_type: 'holiday', display_text: 'Thanksgiving', icon_key: 'turkey' }),
      ),
    ).toBe('Thanksgiving')
    expect(printEntryText(entry({ entry_type: 'misc', display_text: 'Game night' }))).toBe(
      'Game night',
    )
  })

  it('prints eat out and fend for yourself as fixed text, never restaurant/location', () => {
    const eatOut = entry({
      entry_type: 'eat_out',
      recipe: null,
      display_text: 'Pizza Place',
      restaurant_name: 'Pizza Place',
      location_text: 'Downtown',
    })
    expect(printEntryText(eatOut)).toBe('Eat out')
    expect(printEntryText(eatOut)).not.toContain('Pizza Place')
    expect(printEntryText(eatOut)).not.toContain('Downtown')

    expect(printEntryText(entry({ entry_type: 'fend_for_yourself', recipe: null }))).toBe(
      'Fend for yourself',
    )
  })

  it('falls back to the type label when display_text is empty', () => {
    expect(printEntryText(entry({ entry_type: 'vacation', display_text: '' }))).toBe('Vacation')
  })

  it('prints nothing for an empty day', () => {
    expect(printEntryText(null)).toBeNull()
  })

  it('prints a curated icon only for the four icon-bearing types', () => {
    expect(printIconKey(entry({ entry_type: 'holiday', icon_key: 'turkey' }))).toBe('turkey')
    expect(printIconKey(entry({ entry_type: 'host_family_dinner', icon_key: 'heart' }))).toBe(
      'heart',
    )
    expect(printIconKey(entry({ entry_type: 'go_to_family_dinner', icon_key: 'star' }))).toBe(
      'star',
    )
    expect(printIconKey(entry({ entry_type: 'misc', icon_key: 'cake' }))).toBe('cake')

    // A non-icon type never prints an icon, even if one is present.
    expect(
      printIconKey(entry({ entry_type: 'recipe' as CalendarEntryType, icon_key: 'cake' })),
    ).toBeNull()
    // No icon key, or no entry at all.
    expect(printIconKey(entry({ entry_type: 'holiday', icon_key: null }))).toBeNull()
    expect(printIconKey(null)).toBeNull()
  })
})
