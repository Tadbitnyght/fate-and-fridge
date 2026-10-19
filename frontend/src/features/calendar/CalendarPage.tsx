import { useMemo, useState, type JSX } from 'react'
import { useNavigate } from 'react-router-dom'

import type {
  BatchRerollMode,
  CalendarDay,
  CalendarEntry,
  CalendarOperation,
} from '../../api/calendar'
import { useHouseholdSettings } from '../settings/useHouseholdSettings'
import { formatHouseholdDate } from '../../lib/dateFormat'
import {
  buildMonthGrid,
  isoDate,
  monthEndIso,
  monthLabel,
  monthStartIso,
  parseIsoDate,
} from './calendarDates'
import { AgendaList } from './AgendaList'
import { BatchPreviewDialog } from './BatchPreviewDialog'
import { DateDetailDialog } from './DateDetailDialog'
import { MonthGrid } from './MonthGrid'
import { RerollMenu } from './RerollMenu'
import { UndoBanner } from './UndoBanner'
import { useCalendarAccess } from './access'
import { describeRerollError } from './calendarErrors'
import { formatMonthKey } from './printRange'
import { useCalendarRange, useRerollCalendarDay } from './useCalendar'

type CalendarView = 'month' | 'agenda'

/** The operation the Undo banner is currently offering to revert. */
type UndoTarget =
  { kind: 'single'; operation: CalendarOperation } | { kind: 'batch'; operation: CalendarOperation }

/**
 * Calendar page (M5-09/M5-10/M5-12): a desktop month grid and a mobile
 * Month/Agenda toggle, with a date-detail dialog, locked/today/historical
 * styling, and a print button. All writes are enforced server-side.
 */
export function CalendarPage(): JSX.Element {
  const { canView, canEdit, isAdmin } = useCalendarAccess()
  const navigate = useNavigate()
  const now = new Date()
  const [year, setYear] = useState(now.getFullYear())
  const [monthIndex, setMonthIndex] = useState(now.getMonth())
  const [view, setView] = useState<CalendarView>('month')
  const [selectedDate, setSelectedDate] = useState<string | null>(null)
  const [undoTarget, setUndoTarget] = useState<UndoTarget | null>(null)
  const [batchMode, setBatchMode] = useState<BatchRerollMode | null>(null)
  const [batchNotice, setBatchNotice] = useState<string | null>(null)
  const [correctionNotice, setCorrectionNotice] = useState<string | null>(null)
  const [rerollError, setRerollError] = useState<string | null>(null)
  const [undoError, setUndoError] = useState<string | null>(null)

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

  const dayDataByDate = useMemo(() => {
    const map = new Map<string, CalendarDay>()
    for (const day of rangeQuery.data?.entries ?? []) {
      map.set(day.date, day)
    }
    return map
  }, [rangeQuery.data])

  const rerollMutation = useRerollCalendarDay()

  const householdToday = rangeQuery.data?.today ?? today

  function handleReroll(date: string): void {
    setRerollError(null)
    setUndoError(null)
    rerollMutation.mutate(date, {
      onSuccess: (result) => {
        setRerollError(null)
        setUndoTarget({ kind: 'single', operation: result.operation })
      },
      onError: (caught) => setRerollError(describeRerollError(caught)),
    })
  }

  function handleRerolled(nextOperation: CalendarOperation): void {
    setRerollError(null)
    setUndoError(null)
    setUndoTarget({ kind: 'single', operation: nextOperation })
  }

  function handleBatchApplied(result: { operation: CalendarOperation | null }): void {
    setRerollError(null)
    setUndoError(null)
    if (result.operation) {
      setBatchNotice(null)
      setUndoTarget({ kind: 'batch', operation: result.operation })
    } else {
      setBatchNotice('Nothing to change.')
    }
  }

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
          <button
            type="button"
            onClick={() => navigate(`/calendar/print?month=${formatMonthKey(year, monthIndex)}`)}
          >
            Print
          </button>
          {canEdit && (
            <RerollMenu
              onChoose={(mode) => {
                setBatchNotice(null)
                setBatchMode(mode)
              }}
            />
          )}
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

      {undoTarget && (
        <UndoBanner
          operation={undoTarget.operation}
          kind={undoTarget.kind}
          onDismiss={() => setUndoTarget(null)}
          onError={(message) => setUndoError(message)}
        />
      )}
      {batchNotice !== null && (
        <p className="form__notice" role="status">
          {batchNotice}
        </p>
      )}
      {correctionNotice !== null && (
        <p className="form__notice" role="status">
          {correctionNotice}
        </p>
      )}
      {undoError !== null && (
        <p className="form__error" role="alert">
          {undoError}
        </p>
      )}
      {rerollError !== null && (
        <p className="form__error" role="alert">
          {rerollError}
        </p>
      )}

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
              dayDataByDate={dayDataByDate}
              canEdit={canEdit}
              onReroll={handleReroll}
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
          isAdmin={isAdmin}
          onClose={() => setSelectedDate(null)}
          onRerolled={handleRerolled}
          onCorrected={() =>
            setCorrectionNotice(
              `Corrected ${formatHouseholdDate(selectedDate)}. The audit log records the change.`,
            )
          }
        />
      )}

      {batchMode !== null && (
        <BatchPreviewDialog
          mode={batchMode}
          startDate={selectedDate ?? householdToday}
          onClose={() => setBatchMode(null)}
          onApplied={(result) => {
            setBatchMode(null)
            handleBatchApplied(result)
          }}
        />
      )}
    </section>
  )
}
