import type { JSX } from 'react'
import { Link } from 'react-router-dom'

import {
  SPECIAL_EVENT_LABELS,
  SPECIAL_EVENT_TYPES,
  STATISTICS_RECIPE_STATUS_LABELS,
  type ProteinDistribution,
  type RankedRecipe,
  type SpecialEventCount,
  type StatisticsOverview,
} from '../../api/statistics'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { useStatisticsAccess } from './access'
import { useStatisticsOverview } from './useStatistics'

/**
 * Statistics overview (M11-04, docs/M11_PLAN.md §2.3, docs/API.md §14).
 *
 * `recipes.view` gates the content; a caller without it sees a permission
 * message instead of the page. Every figure (windows, rankings, month/year
 * counts, protein distribution, special-event counts) comes straight from the
 * server — no client-side aggregation, and no client-side date parsing that
 * could shift a household-local date across a timezone boundary.
 */
export function StatisticsPage(): JSX.Element {
  const { canViewOverview } = useStatisticsAccess()
  const overviewQuery = useStatisticsOverview(canViewOverview)

  if (!canViewOverview) {
    return (
      <section className="page statistics-page" aria-labelledby="statistics-heading">
        <h2 id="statistics-heading" tabIndex={-1}>
          Statistics
        </h2>
        <p className="form__error" role="alert">
          You do not have permission to view statistics. Ask an Admin for the recipes.view
          permission.
        </p>
      </section>
    )
  }

  const overview = overviewQuery.data

  return (
    <section className="page statistics-page" aria-labelledby="statistics-heading">
      <h2 id="statistics-heading" tabIndex={-1}>
        Statistics
      </h2>

      {overview && (
        <p className="page-note">
          Today is {formatHouseholdDate(overview.windows.today)} ({overview.windows.timezone}).
        </p>
      )}

      {overviewQuery.isPending && <LoadingScreen label="Loading statistics…" />}

      {overviewQuery.isError && (
        <ErrorScreen
          title="Cannot load statistics"
          error={overviewQuery.error}
          onRetry={() => void overviewQuery.refetch()}
        />
      )}

      {overview && <Overview overview={overview} />}
    </section>
  )
}

function Overview({ overview }: { overview: StatisticsOverview }) {
  const hasHistory =
    overview.meal_count_year > 0 ||
    overview.most_eaten.length > 0 ||
    overview.least_recently_eaten.length > 0 ||
    overview.special_event_counts.some((count) => count.count > 0)

  if (!hasHistory) {
    return (
      <p className="stats-empty">
        No meal history yet. Once your household has eaten some planned meals, the most-eaten
        rankings, protein distribution, and event counts appear here.
      </p>
    )
  }

  const neverEaten =
    overview.never_eaten === 1
      ? '1 recipe has never been eaten.'
      : `${overview.never_eaten} recipes have never been eaten.`

  return (
    <>
      <ul className="stats-cards">
        <MealCountCard
          label="Meals this month"
          windowLabel={overview.windows.month.label}
          count={overview.meal_count_month}
          start={overview.windows.month.start}
          end={overview.windows.month.end}
        />
        <MealCountCard
          label="Meals this year"
          windowLabel={overview.windows.year.label}
          count={overview.meal_count_year}
          start={overview.windows.year.start}
          end={overview.windows.year.end}
        />
      </ul>

      <section className="stats-section" aria-labelledby="stats-most-eaten">
        <h3 id="stats-most-eaten">Most eaten</h3>
        <RankedRecipeList
          items={overview.most_eaten}
          emptyLabel="No recipes have been eaten yet."
        />
      </section>

      <section className="stats-section" aria-labelledby="stats-least-recently-eaten">
        <h3 id="stats-least-recently-eaten">Least recently eaten</h3>
        <RankedRecipeList
          items={overview.least_recently_eaten}
          emptyLabel="No recipes have been eaten yet."
        />
        <p className="page-note stats-note">{neverEaten}</p>
      </section>

      <section className="stats-section" aria-labelledby="stats-protein">
        <h3 id="stats-protein">Primary protein distribution</h3>
        <ProteinBars distribution={overview.protein_distribution} />
      </section>

      <section className="stats-section" aria-labelledby="stats-events">
        <h3 id="stats-events">Special events</h3>
        <p className="page-note stats-note">
          Counts every special-event entry in {overview.windows.year.label}.
        </p>
        <SpecialEventList counts={overview.special_event_counts} />
      </section>
    </>
  )
}

function MealCountCard({
  label,
  windowLabel,
  count,
  start,
  end,
}: {
  label: string
  windowLabel: string
  count: number
  start: string
  end: string
}) {
  return (
    <li className="stats-card">
      <span className="stats-card__label">
        {label} &middot; {windowLabel}
      </span>
      <span className="stats-card__value">{count}</span>
      <span className="stats-card__range">
        {formatHouseholdDate(start)} &ndash; {formatHouseholdDate(end)}
      </span>
    </li>
  )
}

function StatusBadge({ status }: { status: RankedRecipe['status'] }) {
  return (
    <span className={`status-chip status-chip--${status}`}>
      {STATISTICS_RECIPE_STATUS_LABELS[status]}
    </span>
  )
}

function RankedRecipeList({ items, emptyLabel }: { items: RankedRecipe[]; emptyLabel: string }) {
  if (items.length === 0) {
    return <p className="page-note">{emptyLabel}</p>
  }

  return (
    <ol className="stats-list">
      {items.map((item, index) => (
        <li
          key={item.recipe_id !== null ? `recipe-${item.recipe_id}` : `snapshot-${item.name}`}
          className={item.status === 'deleted' ? 'stats-row stats-row--deleted' : 'stats-row'}
        >
          <span className="stats-row__rank" aria-hidden="true">
            {index + 1}.
          </span>
          <span className="stats-row__name">
            {item.recipe_id !== null ? (
              <Link to={`/recipes/${item.recipe_id}/edit`}>{item.name}</Link>
            ) : (
              item.name
            )}
          </span>
          <StatusBadge status={item.status} />
          <span className="stats-row__meta">{formatTimes(item.times_eaten)}</span>
          <span className="stats-row__meta">Last eaten {formatHouseholdDate(item.last_eaten)}</span>
        </li>
      ))}
    </ol>
  )
}

function ProteinBars({ distribution }: { distribution: ProteinDistribution }) {
  if (distribution.proteins.length === 0 && distribution.no_primary_protein === 0) {
    return <p className="page-note">No recipe meals with protein data yet.</p>
  }

  // Colour-free presentation: each row spells out its label and value, and the
  // bar itself is decorative so meaning never rests on the fill colour.
  const bars = [
    ...distribution.proteins.map((protein) => ({
      key: `protein-${protein.protein_id}`,
      label: protein.name,
      count: protein.count,
    })),
    { key: 'no-protein', label: 'No primary protein', count: distribution.no_primary_protein },
  ]
  const max = Math.max(...bars.map((bar) => bar.count), 1)

  return (
    <ul className="stats-bars">
      {bars.map((bar) => (
        <li key={bar.key} className="stats-bar-row">
          <span className="stats-bar-row__label">{bar.label}</span>
          <span className="stats-bar" aria-hidden="true">
            <span
              className="stats-bar__fill"
              style={{ width: `${Math.round((bar.count / max) * 100)}%` }}
            />
          </span>
          <span className="stats-bar-row__value">{formatMeals(bar.count)}</span>
        </li>
      ))}
    </ul>
  )
}

function SpecialEventList({ counts }: { counts: SpecialEventCount[] }) {
  const byType = new Map(counts.map((count) => [count.entry_type, count.count]))
  return (
    <ul className="stats-events">
      {SPECIAL_EVENT_TYPES.map((type) => (
        <li key={type} className="stats-event">
          <span className="stats-event__label">{SPECIAL_EVENT_LABELS[type]}</span>
          <span className="stats-event__count">{byType.get(type) ?? 0}</span>
        </li>
      ))}
    </ul>
  )
}

function formatTimes(times: number): string {
  return times === 1 ? 'Eaten once' : `Eaten ${times} times`
}

function formatMeals(count: number): string {
  return count === 1 ? '1 meal' : `${count} meals`
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/**
 * Renders a household-local `YYYY-MM-DD` string as `Mon D, YYYY` without ever
 * constructing a `Date`: `new Date('2026-11-01')` is UTC midnight and
 * `toLocaleDateString` could move it a day in a negative-offset timezone
 * (docs/M11_PLAN.md §1.26). A value that is not a bare date is shown verbatim.
 */
function formatHouseholdDate(value: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
  if (match === null) {
    return value
  }
  const month = MONTHS[Number(match[2]) - 1]
  if (month === undefined) {
    return value
  }
  return `${month} ${Number(match[3])}, ${match[1]}`
}
