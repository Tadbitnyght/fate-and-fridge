import { useMemo, useState, type JSX } from 'react'

import type { CalendarEntry } from '../../api/calendar'
import { useHouseholdSettings } from '../settings/useHouseholdSettings'
import {
  buildMonthGrid,
  isoDate,
  monthEndIso,
  monthLabel,
  monthStartIso,
  parseIsoDate,
} from './calendarDates'
import { AgendaList } from './AgendaList'
import { DateDetailDialog } from './DateDetailDialog'
import { MonthGrid } from './MonthGrid'
import { useCalendarAccess } from './access'
import { useCalendarRange } from './useCalendar'

type CalendarView = 'month' | 'agenda'

/**
 * Calendar page (M5-09/M5-10/M5-12): a desktop month grid and a mobile
 * Month/Agenda toggle, with a date-detail dialog, locked/today/historical
 * styling, and a print button. All writes are enforced server-side.
 */
export function CalendarPage(): JSX.Element {
  const { canView, canEdit } = useCalendarAccess()
  const now = new Date()
  const [year, setYear] = useState(now.getFullYear())
  const [monthIndex, setMonthIndex] = useState(now.getMonth())
  const [view, setView] = useState<CalendarView>('month')
  const [selectedDate, setSelectedDate] = useState<string | null>(null)

  const householdSettings = useHouseholdSettings()
  const weekStartsOn = householdSettings.data?.week_starts_on ?? 0

  const today = isoDate(now)
  const days = useMemo(
    () => buildMonthGrid(year, monthIndex, weekStartsOn),
    [year, monthIndex, weekStartsOn],
  )
  const rangeStart = days[0]?.iso ?? monthStartIso(year, monthIndex)
  const rangeEnd = days[days.length - 1]?.iso ?? monthEndIso(year, monthIndex)

  const rangeQuery = useCalendarRange(rangeStart, rangeEnd)

  const entriesByDate = useMemo(() => {
    const map = new Map<string, CalendarEntry>()
    for (const day of rangeQuery.data?.entries ?? []) {
      if (day.entry) {
        map.set(day.date, day.entry)
      }
    }
    return map
  }, [rangeQuery.data])

  const householdToday = rangeQuery.data?.today ?? today

  function goToMonth(delta: number): void {
    const next = new Date(year, monthIndex + delta, 1)
    setYear(next.getFullYear())
    setMonthIndex(next.getMonth())
  }

  function goToToday(): void {
    const current = parseIsoDate(householdToday)
    setYear(current.getFullYear())
    setMonthIndex(current.getMonth())
  }

  if (!canView) {
    return (
      <section className="page calendar-page" aria-labelledby="calendar-heading">
        <h2 id="calendar-heading" tabIndex={-1}>
          Calendar
        </h2>
        <p className="form__error" role="alert">
          You do not have permission to view the calendar. Ask an Admin for the calendar.view
          permission.
        </p>
      </section>
    )
  }

  return (
    <section className="page calendar-page" aria-labelledby="calendar-heading">
      <div className="calendar-toolbar">
        <h2 id="calendar-heading" tabIndex={-1}>
          Calendar
        </h2>
        <div className="calendar-toolbar__nav">
          <button type="button" onClick={() => goToMonth(-1)} aria-label="Previous month">
            ‹
          </button>
          <span className="calendar-toolbar__month" aria-live="polite">
            {monthLabel(year, monthIndex)}
          </span>
          <button type="button" onClick={() => goToMonth(1)} aria-label="Next month">
            ›
          </button>
          <button type="button" onClick={goToToday}>
            Today
          </button>
          <button type="button" onClick={() => window.print()}>
            Print
          </button>
        </div>
      </div>

      <div className="calendar-toggle" role="group" aria-label="Calendar view">
        <button
          type="button"
          className={view === 'month' ? 'calendar-toggle__active' : undefined}
          aria-pressed={view === 'month'}
          onClick={() => setView('month')}
        >
          Month
        </button>
        <button
          type="button"
          className={view === 'agenda' ? 'calendar-toggle__active' : undefined}
          aria-pressed={view === 'agenda'}
          onClick={() => setView('agenda')}
        >
          Agenda
        </button>
      </div>

      {rangeQuery.isLoading && <p className="page-note">Loading the calendar…</p>}
      {rangeQuery.isError && (
        <p className="form__error" role="alert">
          Could not load the calendar. Try again.
        </p>
      )}

      {rangeQuery.data && (
        <div className={`calendar-views calendar-views--${view}`}>
          <div className="calendar-views__month">
            <MonthGrid
              days={days}
              entriesByDate={entriesByDate}
              today={householdToday}
              selectedDate={selectedDate}
              onSelect={setSelectedDate}
              weekStartsOn={weekStartsOn}
              label={monthLabel(year, monthIndex)}
            />
          </div>
          <div className="calendar-views__agenda">
            <AgendaList days={days} entriesByDate={entriesByDate} onSelect={setSelectedDate} />
          </div>
        </div>
      )}

      {selectedDate !== null && (
        <DateDetailDialog
          date={selectedDate}
          canEdit={canEdit}
          onClose={() => setSelectedDate(null)}
        />
      )}
    </section>
  )
}
