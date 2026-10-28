import { useState, type FormEvent, type JSX } from 'react'
import { Link } from 'react-router-dom'

import {
  CALENDAR_ENTRY_LABELS,
  CALENDAR_ENTRY_TYPES,
  type CalendarEntryType,
} from '../../api/calendar'
import type { MealHistoryDay, MealHistoryParams } from '../../api/statistics'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { useProteins } from '../reference-data/useReferenceData'
import { useAdminAccess } from '../settings/access'
import { useStatisticsAccess } from './access'
import { formatHouseholdDate, formatHouseholdMonth } from '../../lib/dateFormat'
import { HistoricalCorrectionDialog } from '../calendar/HistoricalCorrectionDialog'
import { useMealHistory } from './useStatistics'

/**
 * Meal-history browser: the read-only meal log, one month per page.
 * Every filter and month step is server-side — the component only builds the
 * query and renders what comes back, so paging stays stable and no household
 * date is ever reinterpreted client-side. Past dates are immutable in normal
 * use; the admin correction flow is the only write reachable from here
 * and its affordance is hidden from members (the server is the authority).
 */
export function MealHistoryBrowser(): JSX.Element {
  const { canEditRecipes } = useStatisticsAccess()
  const { isAdmin } = useAdminAccess()
  // Applied filter state (server-side) plus the unapplied recipe text input
  // mirroring the recipes browser: a select applies at once, free text on submit.
  const [entryType, setEntryType] = useState<'' | CalendarEntryType>('')
  const [proteinId, setProteinId] = useState('')
  const [recipeInput, setRecipeInput] = useState('')
  const [recipeFilter, setRecipeFilter] = useState('')
  const [month, setMonth] = useState<string | undefined>(undefined)
  const [correctionDate, setCorrectionDate] = useState<string | null>(null)
  const [correctionNotice, setCorrectionNotice] = useState<string | null>(null)

  const params: MealHistoryParams = {
    month,
    entry_type: entryType === '' ? undefined : entryType,
    protein: proteinId === '' ? undefined : Number(proteinId),
    recipe: recipeFilter.length > 0 ? recipeFilter : undefined,
  }

  const historyQuery = useMealHistory(params)
  // Retired proteins still label (and filter) historical meals, so the filter
  // lists them too.
  const proteinsQuery = useProteins('', true)

  const history = historyQuery.data
  const proteins = proteinsQuery.data ?? []

  /** Applying a filter returns to the default page, like the recipes browser. */
  function applyFilter(update: () => void): void {
    update()
    setMonth(undefined)
  }

  function handleRecipeSubmit(event: FormEvent<HTMLFormElement>): void {
    event.preventDefault()
    const next = recipeInput.trim()
    applyFilter(() => setRecipeFilter(next))
  }

  const newer = history?.paging.newer ?? null
  const older = history?.paging.older ?? null

  return (
    <div className="history-browser">
      <form className="history-filters" onSubmit={handleRecipeSubmit}>
        <div className="form__field">
          <label htmlFor="history-entry-type">Entry type</label>
          <select
            id="history-entry-type"
            value={entryType}
            onChange={(event) =>
              applyFilter(() => setEntryType(event.target.value as '' | CalendarEntryType))
            }
          >
            <option value="">Any type</option>
            {CALENDAR_ENTRY_TYPES.map((type) => (
              <option key={type} value={type}>
                {CALENDAR_ENTRY_LABELS[type]}
              </option>
            ))}
          </select>
        </div>

        <div className="form__field">
          <label htmlFor="history-protein">Primary protein</label>
          <select
            id="history-protein"
            value={proteinId}
            onChange={(event) => applyFilter(() => setProteinId(event.target.value))}
          >
            <option value="">Any protein</option>
            {proteins.map((protein) => (
              <option key={protein.id} value={protein.id}>
                {protein.is_active ? protein.name : `${protein.name} (retired)`}
              </option>
            ))}
          </select>
        </div>

        <div className="form__field history-filters__recipe">
          <label htmlFor="history-recipe">Recipe</label>
          <input
            id="history-recipe"
            type="search"
            value={recipeInput}
            placeholder="Recipe name or number…"
            onChange={(event) => setRecipeInput(event.target.value)}
          />
          <p className="form__hint">
            A recipe name as it appeared on the meal, or its number. Renamed and deleted recipes
            stay searchable.
          </p>
        </div>

        <div className="history-filters__actions">
          <button type="submit">Apply filters</button>
        </div>
      </form>

      {historyQuery.isPending && <LoadingScreen label="Loading meal history…" />}

      {historyQuery.isError && (
        <ErrorScreen
          title="Cannot load meal history"
          error={historyQuery.error}
          onRetry={() => void historyQuery.refetch()}
        />
      )}

      {history && (
        <section className="history-month" aria-labelledby="history-month-heading">
          <div className="history-month__head">
            <button
              type="button"
              onClick={() => {
                if (newer !== null) {
                  setMonth(newer)
                }
              }}
              disabled={newer === null}
              aria-label={
                newer === null ? 'Newer month' : `Newer month: ${formatHouseholdMonth(newer)}`
              }
            >
              Newer
            </button>

            <div className="history-month__title">
              <h3 id="history-month-heading">{history.month.label}</h3>
              <span className="history-month__range">
                {formatHouseholdDate(history.month.start)} &ndash;{' '}
                {formatHouseholdDate(history.month.end)}
              </span>
            </div>

            <button
              type="button"
              onClick={() => {
                if (older !== null) {
                  setMonth(older)
                }
              }}
              disabled={older === null}
              aria-label={
                older === null ? 'Older month' : `Older month: ${formatHouseholdMonth(older)}`
              }
            >
              Older
            </button>
          </div>

          <p className="page-note history-month__note">
            Showing {formatHouseholdDate(history.range.start)} &ndash;{' '}
            {formatHouseholdDate(history.range.end)} ({history.range.timezone}).
          </p>

          {history.days.length === 0 ? (
            <p className="stats-empty">No meals logged in this month.</p>
          ) : (
            <ol className="history-days">
              {history.days.map((day) => (
                <HistoryDayRow
                  key={day.date}
                  day={day}
                  canEditRecipes={canEditRecipes}
                  isAdmin={isAdmin}
                  onCorrect={setCorrectionDate}
                />
              ))}
            </ol>
          )}
        </section>
      )}

      {correctionNotice !== null && (
        <p className="form__notice" role="status">
          {correctionNotice}
        </p>
      )}

      {correctionDate !== null && (
        <HistoricalCorrectionDialog
          date={correctionDate}
          onClose={() => setCorrectionDate(null)}
          onCorrected={() => {
            setCorrectionNotice(
              `Corrected ${formatHouseholdDate(correctionDate)}. The audit log records the change.`,
            )
            setCorrectionDate(null)
          }}
        />
      )}
    </div>
  )
}

/**
 * One meal-log day. The recipe name links to the recipe's edit page only while
 * the reference is live **and** the caller may edit (`recipes.edit`); a
 * view-only member — and a tombstone — sees the same name as plain text, since
 * the edit page refuses a view-only member (issue #148). An Admin additionally
 * gets the "Correct" affordance; members never see it (the server is the
 * authority).
 */
function HistoryDayRow({
  day,
  canEditRecipes,
  isAdmin,
  onCorrect,
}: {
  day: MealHistoryDay
  canEditRecipes: boolean
  isAdmin: boolean
  onCorrect: (date: string) => void
}) {
  const label = formatHouseholdDate(day.date)
  return (
    <li className="history-day">
      <time className="history-day__date" dateTime={day.date}>
        {label}
      </time>
      <span className="history-day__type">{CALENDAR_ENTRY_LABELS[day.entry_type]}</span>
      <span className="history-day__name">
        {day.recipe_id !== null && canEditRecipes ? (
          <Link to={`/recipes/${day.recipe_id}/edit`}>{day.name}</Link>
        ) : (
          day.name
        )}
      </span>
      <span className="history-day__protein">
        {day.protein === null ? 'No primary protein' : day.protein}
      </span>
      {isAdmin && (
        <button
          type="button"
          className="history-day__correct"
          aria-label={`Correct the entry for ${label}`}
          onClick={() => onCorrect(day.date)}
        >
          Correct
        </button>
      )}
    </li>
  )
}
