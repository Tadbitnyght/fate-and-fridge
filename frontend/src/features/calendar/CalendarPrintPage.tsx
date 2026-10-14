import { useMemo, useState, type JSX } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

import { useHouseholdSettings } from '../settings/useHouseholdSettings'
import { CalendarPrintMonth } from './CalendarPrintMonth'
import { useCalendarAccess } from './access'
import {
  PRINT_MONTH_MAX,
  PRINT_MONTH_MIN,
  clampMonthCount,
  formatMonthKey,
  resolvePrintMonths,
  resolveStartMonth,
} from './printRange'

/**
 * Calendar print view (M10-01/M10-06, docs/M10_PLAN.md §1.3). A dedicated,
 * deep-linkable client route (`/calendar/print`) mirroring `/groceries/print`:
 * screen-only controls (start month, month count 1–12, Print, Back) and one
 * printable month per page in chronological order, each with a month/year
 * heading.
 *
 * The start month defaults to the month being viewed: the calendar toolbar
 * navigates here with `?month=YYYY-MM`; without the param the current month is
 * used. Reads reuse the existing month-scoped calendar range endpoint — no new
 * API (docs/M10_PLAN.md §2.3). All print styling lives in `index.css`.
 */
export function CalendarPrintPage(): JSX.Element {
  const { canView } = useCalendarAccess()
  const [searchParams, setSearchParams] = useSearchParams()
  const monthParam = searchParams.get('month')
  // The URL is the source of truth for the start month, so browser
  // back/forward between `?month=` values updates the view (M10-06) and the
  // month control writes the URL rather than shadowing it in local state.
  const start = useMemo(() => resolveStartMonth(monthParam), [monthParam])
  const [monthCount, setMonthCount] = useState(1)

  const householdSettings = useHouseholdSettings()
  const weekStartsOn = householdSettings.data?.week_starts_on ?? 0

  const months = useMemo(
    () => resolvePrintMonths(start.year, start.monthIndex, monthCount),
    [start, monthCount],
  )

  if (!canView) {
    return (
      <section className="page calendar-print-page" aria-labelledby="calendar-print-heading">
        <h2 id="calendar-print-heading" tabIndex={-1}>
          Print calendar
        </h2>
        <p className="form__error" role="alert">
          You do not have permission to view the calendar. Ask an Admin for the calendar.view
          permission.
        </p>
      </section>
    )
  }

  return (
    <section className="page calendar-print-page" aria-labelledby="calendar-print-heading">
      <div className="calendar-print-controls">
        <Link to="/calendar" className="calendar-print-controls__back">
          Back to calendar
        </Link>
        <label className="calendar-print-controls__field">
          Start month
          <input
            type="month"
            value={formatMonthKey(start.year, start.monthIndex)}
            onChange={(event) => {
              const next = event.currentTarget.value
              if (next.length > 0) {
                setSearchParams({ month: next }, { replace: true })
              }
            }}
          />
        </label>
        <label className="calendar-print-controls__field">
          Months
          <input
            type="number"
            min={PRINT_MONTH_MIN}
            max={PRINT_MONTH_MAX}
            value={monthCount}
            onChange={(event) => setMonthCount(clampMonthCount(Number(event.currentTarget.value)))}
          />
        </label>
        <button type="button" className="button--primary" onClick={() => window.print()}>
          Print
        </button>
      </div>

      <h2 id="calendar-print-heading" className="calendar-print-heading" tabIndex={-1}>
        Print calendar
      </h2>

      <div className="calendar-print-months">
        {months.map((month) => (
          <CalendarPrintMonth key={month.key} month={month} weekStartsOn={weekStartsOn} />
        ))}
      </div>
    </section>
  )
}
