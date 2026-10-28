import { useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type JSX } from 'react'

import type { CalendarEntry } from '../../api/calendar'
import { buildMonthGrid, monthEndIso, monthStartIso, weekdayHeaders } from './calendarDates'
import { printEntryText, printIconKey, printMonthHeading } from './calendarPrintFormat'
import { EventIcon } from './icons'
import {
  computePrintScale,
  printAvailableHeightPx,
  readPrintGeometry,
  shouldSpillPrintMonth,
} from './printScale'
import type { PrintMonth } from './printRange'
import { useCalendarRange } from './useCalendar'

/**
 * One printable calendar month. A non-interactive table
 * mirroring the calendar's month grid: a month/year heading plus day cells that
 * carry the day number and the recipe or special-event text. It deliberately
 * omits every interactive affordance and every excluded datum (meal photos, lock
 * icons, notes, diner count, restaurant/location text).
 * The month auto-shrinks to fit one page via a measurement-based font-size
 * scale (see `printScale.ts`); the whole month — month/year heading and grid —
 * is measured and scaled so the heading is part of the fit budget. When it
 * cannot fit even at the legibility floor it sets `data-spill="true"` so the
 * print stylesheet releases the height cap and the content flows onto a
 * following page.
 */
export function CalendarPrintMonth({
  month,
  weekStartsOn,
}: {
  month: PrintMonth
  weekStartsOn: number
}): JSX.Element {
  const heading = printMonthHeading(month)
  const headingId = `calendar-print-month-${month.key}`

  const days = useMemo(
    () => buildMonthGrid(month.year, month.monthIndex, weekStartsOn),
    [month.year, month.monthIndex, weekStartsOn],
  )
  const rangeStart = monthStartIso(month.year, month.monthIndex)
  const rangeEnd = monthEndIso(month.year, month.monthIndex)
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

  const monthRef = useRef<HTMLElement>(null)
  const appliedScaleRef = useRef(1)
  const measuredDataRef = useRef<unknown>(undefined)
  const [scale, setScale] = useState(1)
  const [spill, setSpill] = useState(false)

  // Deterministic auto-shrink. Measure the whole month's natural
  // height once per data change, dividing out the scale already applied so the
  // measurement is independent of it; setting an unchanged value is a no-op for
  // React, so this settles after a single pass. The available height comes from
  // the CSS geometry (single source of truth), so the JS budget and the CSS
  // height cap cannot drift. jsdom lays nothing out (height 0), so tests
  // exercise the pure scale math directly.
  useLayoutEffect(() => {
    const node = monthRef.current
    if (node === null || measuredDataRef.current === rangeQuery.data) {
      return
    }
    measuredDataRef.current = rangeQuery.data
    const available = printAvailableHeightPx(readPrintGeometry(node))
    const rendered = node.getBoundingClientRect().height
    if (rendered <= 0) {
      appliedScaleRef.current = 1
      setScale(1)
      setSpill(false)
      return
    }
    const natural = rendered / appliedScaleRef.current
    const nextScale = computePrintScale(natural, available)
    appliedScaleRef.current = nextScale
    setScale(nextScale)
    setSpill(shouldSpillPrintMonth(natural, available))
  }, [rangeQuery.data])

  return (
    <section
      ref={monthRef}
      className="calendar-print-month"
      data-spill={spill ? 'true' : 'false'}
      style={{ '--calendar-print-scale': String(scale) } as CSSProperties}
      aria-labelledby={headingId}
    >
      <h3 id={headingId} className="calendar-print-month__heading">
        {heading}
      </h3>
      <div className="calendar-print-month__content">
        <table className="calendar-print-month__table">
          <thead>
            <tr>
              {weekdayHeaders(weekStartsOn).map((weekday) => (
                <th key={weekday} scope="col" className="calendar-print-month__weekday">
                  {weekday}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {chunk(days, 7).map((week) => (
              <tr key={week[0].iso} className="calendar-print-month__row">
                {week.map((day) => {
                  // Outside-month days show only their number: the month's own
                  // read does not cover them, so no adjacent entry leaks in.
                  const entry = day.inMonth ? (entriesByDate.get(day.iso) ?? null) : null
                  const text = printEntryText(entry)
                  const iconKey = printIconKey(entry)
                  return (
                    <td
                      key={day.iso}
                      className={
                        day.inMonth
                          ? 'calendar-print-month__cell'
                          : 'calendar-print-month__cell calendar-print-month__cell--outside'
                      }
                    >
                      <span className="calendar-print-month__day">{day.dayOfMonth}</span>
                      {text !== null && (
                        <span className="calendar-print-month__entry">
                          {iconKey !== null && (
                            <EventIcon iconKey={iconKey} className="calendar-print-month__icon" />
                          )}
                          <span className="calendar-print-month__text">{text}</span>
                        </span>
                      )}
                    </td>
                  )
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function chunk<T>(items: T[], size: number): T[][] {
  const rows: T[][] = []
  for (let index = 0; index < items.length; index += size) {
    rows.push(items.slice(index, index + size))
  }
  return rows
}
