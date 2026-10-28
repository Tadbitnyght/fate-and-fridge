import type { CalendarEntry } from '../../api/calendar'
import { formatHouseholdWeekday } from '../../lib/dateFormat'
import type { MonthGridDay } from './calendarDates'
import { EventIcon } from './icons'

/** Agenda (mobile) list of the visible month's days. */
export function AgendaList({
  days,
  entriesByDate,
  onSelect,
}: {
  days: MonthGridDay[]
  entriesByDate: Map<string, CalendarEntry>
  onSelect: (iso: string) => void
}) {
  const visible = days.filter((day) => day.inMonth)

  return (
    <ul className="calendar-agenda" aria-label="Agenda">
      {visible.map((day) => {
        const entry = entriesByDate.get(day.iso)
        return (
          <li key={day.iso}>
            <button
              type="button"
              className="calendar-agenda__item"
              onClick={() => onSelect(day.iso)}
            >
              <span className="calendar-agenda__date">
                <span className="calendar-agenda__weekday">{formatHouseholdWeekday(day.iso)}</span>
                <span className="calendar-agenda__day">{day.dayOfMonth}</span>
              </span>
              <span className="calendar-agenda__content">
                {entry?.icon_key && (
                  <EventIcon iconKey={entry.icon_key} className="calendar-agenda__icon" />
                )}
                <span className="calendar-agenda__text">
                  {entry ? (
                    entry.display_text
                  ) : (
                    <span className="calendar-agenda__empty">Nothing planned</span>
                  )}
                </span>
                {entry?.is_locked && <span aria-label="Locked">🔒</span>}
              </span>
            </button>
          </li>
        )
      })}
    </ul>
  )
}
