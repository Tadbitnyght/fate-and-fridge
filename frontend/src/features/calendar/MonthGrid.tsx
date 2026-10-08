import { useRef, useState, type KeyboardEvent } from 'react'

import type { CalendarDay, CalendarEntry } from '../../api/calendar'
import { fullDateLabel, weekdayHeaders, type MonthGridDay } from './calendarDates'
import { DayActionMenu } from './DayActionMenu'
import { EventIcon } from './icons'
import { rerollAvailability } from './rerollAvailability'

/**
 * Accessible month grid (M5-09). An ARIA grid of day buttons with
 * roving-tabindex arrow-key navigation; today is highlighted, historical days
 * are dimmed, and locked entries show a lock marker.
 *
 * M6-10 adds a compact Reroll affordance as a context menu (right-click,
 * `ContextMenu` key, or `Shift+F10`) opened from the focused cell. It is not an
 * inline button because a `gridcell` is itself a button here; nesting another
 * interactive element would be invalid HTML and break the grid's ARIA and
 * roving-tabindex semantics.
 */
export function MonthGrid({
  days,
  entriesByDate,
  dayDataByDate,
  canEdit,
  onReroll,
  today,
  selectedDate,
  onSelect,
  weekStartsOn,
  label,
}: {
  days: MonthGridDay[]
  entriesByDate: Map<string, CalendarEntry>
  dayDataByDate: Map<string, CalendarDay>
  canEdit: boolean
  onReroll: (iso: string) => void
  today: string
  selectedDate: string | null
  onSelect: (iso: string) => void
  weekStartsOn: number
  label: string
}) {
  const [focusedIso, setFocusedIso] = useState(selectedDate ?? today)
  const [menu, setMenu] = useState<{ iso: string; rect: DOMRect } | null>(null)
  const buttonRefs = useRef(new Map<string, HTMLButtonElement>())

  // Derive the effective roving-tabindex date during render so month
  // navigation never needs a state-syncing effect.
  const fallbackIso = days.find((day) => day.inMonth)?.iso ?? days[0]?.iso ?? today
  const effectiveFocusedIso = days.some((day) => day.iso === focusedIso) ? focusedIso : fallbackIso

  function focusAt(index: number): void {
    const clamped = Math.max(0, Math.min(days.length - 1, index))
    const target = days[clamped]
    setFocusedIso(target.iso)
    buttonRefs.current.get(target.iso)?.focus()
  }

  function openMenu(iso: string): void {
    const node = buttonRefs.current.get(iso)
    if (!node) {
      return
    }
    setMenu({ iso, rect: node.getBoundingClientRect() })
  }

  function handleKeyDown(event: KeyboardEvent<HTMLDivElement>): void {
    const index = days.findIndex((day) => day.iso === effectiveFocusedIso)
    if (index < 0) {
      return
    }

    if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
      event.preventDefault()
      openMenu(effectiveFocusedIso)
      return
    }

    switch (event.key) {
      case 'ArrowRight':
        focusAt(index + 1)
        break
      case 'ArrowLeft':
        focusAt(index - 1)
        break
      case 'ArrowDown':
        focusAt(index + 7)
        break
      case 'ArrowUp':
        focusAt(index - 7)
        break
      case 'Home':
        focusAt(index - (index % 7))
        break
      case 'End':
        focusAt(index + 6 - (index % 7))
        break
      case 'PageUp':
        focusAt(index - 28)
        break
      case 'PageDown':
        focusAt(index + 28)
        break
      default:
        return
    }

    event.preventDefault()
  }

  return (
    <div className="calendar-grid" role="grid" aria-label={label} onKeyDown={handleKeyDown}>
      <div className="calendar-grid__row calendar-grid__row--head" role="row">
        {weekdayHeaders(weekStartsOn).map((weekday) => (
          <div
            key={weekday}
            className="calendar-grid__weekday"
            role="columnheader"
            aria-label={weekday}
          >
            {weekday}
          </div>
        ))}
      </div>
      {chunk(days, 7).map((week) => (
        <div key={week[0].iso} className="calendar-grid__row" role="row">
          {week.map((day) => {
            const entry = entriesByDate.get(day.iso) ?? null
            return (
              <button
                key={day.iso}
                type="button"
                role="gridcell"
                ref={(node) => {
                  if (node) {
                    buttonRefs.current.set(day.iso, node)
                  } else {
                    buttonRefs.current.delete(day.iso)
                  }
                }}
                className={cellClassName(day, entry, today)}
                tabIndex={day.iso === effectiveFocusedIso ? 0 : -1}
                aria-selected={day.iso === selectedDate}
                aria-label={cellLabel(day, entry, today)}
                onClick={() => onSelect(day.iso)}
                onContextMenu={(event) => {
                  event.preventDefault()
                  openMenu(day.iso)
                }}
              >
                <span className="calendar-grid__daynumber">{day.dayOfMonth}</span>
                {entry && (
                  <span className="calendar-grid__entry">
                    {entry.icon_key && (
                      <EventIcon iconKey={entry.icon_key} className="calendar-grid__icon" />
                    )}
                    <span className="calendar-grid__text">{entry.display_text}</span>
                    {entry.is_locked && (
                      <span className="calendar-grid__lock" aria-label="Locked" title="Locked">
                        🔒
                      </span>
                    )}
                  </span>
                )}
              </button>
            )
          })}
        </div>
      ))}
      {menu && (
        <DayActionMenu
          dateLabel={fullDateLabel(menu.iso)}
          rect={menu.rect}
          disabled={rerollAvailability(dayDataByDate.get(menu.iso) ?? null, canEdit).disabled}
          reason={rerollAvailability(dayDataByDate.get(menu.iso) ?? null, canEdit).reason}
          onReroll={() => {
            const iso = menu.iso
            setMenu(null)
            onReroll(iso)
          }}
          onClose={() => setMenu(null)}
          onRestoreFocus={() => buttonRefs.current.get(menu.iso)?.focus()}
        />
      )}
    </div>
  )
}

function cellClassName(day: MonthGridDay, entry: CalendarEntry | null, today: string): string {
  const classes = ['calendar-grid__cell']
  if (!day.inMonth) {
    classes.push('calendar-grid__cell--outside')
  }
  if (day.iso === today) {
    classes.push('calendar-grid__cell--today')
  }
  if (entry?.blocks_automation) {
    classes.push('calendar-grid__cell--vacation')
  }
  return classes.join(' ')
}

function cellLabel(day: MonthGridDay, entry: CalendarEntry | null, today: string): string {
  const parts = [fullDateLabel(day.iso)]
  if (day.iso === today) {
    parts.push('today')
  }
  if (entry) {
    parts.push(entry.display_text)
    if (entry.is_locked) {
      parts.push('locked')
    }
  } else {
    parts.push('no entry')
  }
  return parts.join(', ')
}

function chunk<T>(items: T[], size: number): T[][] {
  const rows: T[][] = []
  for (let index = 0; index < items.length; index += size) {
    rows.push(items.slice(index, index + size))
  }
  return rows
}
