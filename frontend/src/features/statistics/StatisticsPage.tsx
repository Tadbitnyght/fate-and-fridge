import { useRef, type JSX, type KeyboardEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

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
import { MealHistoryBrowser } from './MealHistoryBrowser'
import { useStatisticsAccess } from './access'
import { formatHouseholdDate } from './statisticsFormat'
import { useStatisticsOverview } from './useStatistics'

type StatisticsTab = 'overview' | 'history'

const TABS: { id: StatisticsTab; label: string }[] = [
  { id: 'overview', label: 'Overview' },
  { id: 'history', label: 'History' },
]

/**
 * Statistics page (M11-04 overview, M11-05 meal history; docs/M11_PLAN.md
 * §2.3, docs/API.md §14).
 *
 * Two tabs over one route. The URL (`?tab=history`) is the source of truth so
 * the history browser is deep-linkable; the History tab is offered only with
 * `calendar.view`, and a deep link without it explains that history is
 * unavailable. `recipes.view` gates the overview content. Every figure comes
 * straight from the server — no client-side aggregation, and no client-side
 * date parsing that could shift a household-local date across a timezone
 * boundary.
 */
export function StatisticsPage(): JSX.Element {
  const { canEditRecipes, canViewOverview, canViewHistory } = useStatisticsAccess()
  const [searchParams, setSearchParams] = useSearchParams()
  const tabRefs = useRef<Array<HTMLButtonElement | null>>([])

  const requestedHistory = searchParams.get('tab') === 'history'
  const showHistory = requestedHistory && canViewHistory
  // A History deep link the caller may not open has no tab to select: the
  // requested History tab is unavailable, so the tablist is omitted entirely.
  // Rendering it would claim `aria-controls="statistics-panel-overview"` for a
  // tabpanel that the branch below does not render (issue #148).
  const historyUnavailable = requestedHistory && !canViewHistory
  const tabs = canViewHistory ? TABS : TABS.filter((tab) => tab.id !== 'history')
  const requestedTab: StatisticsTab = requestedHistory ? 'history' : 'overview'
  // When the requested History tab is unavailable the tablist is not rendered
  // at all, so this fallback only keeps `activeTab` a valid member of `tabs`;
  // it never selects a tab for the user (see `historyUnavailable` below).
  const activeTab: StatisticsTab = tabs.some((tab) => tab.id === requestedTab)
    ? requestedTab
    : 'overview'

  // The overview is only fetched while its panel is the one on screen.
  const overviewQuery = useStatisticsOverview(canViewOverview && !requestedHistory)

  function selectTab(index: number): void {
    const clamped = (index + tabs.length) % tabs.length
    const next = tabs[clamped].id
    setSearchParams(next === 'history' ? { tab: 'history' } : {}, { replace: true })
    tabRefs.current[clamped]?.focus()
  }

  function handleTabKeyDown(event: KeyboardEvent<HTMLButtonElement>, index: number): void {
    switch (event.key) {
      case 'ArrowRight':
        event.preventDefault()
        selectTab(index + 1)
        break
      case 'ArrowLeft':
        event.preventDefault()
        selectTab(index - 1)
        break
      case 'Home':
        event.preventDefault()
        selectTab(0)
        break
      case 'End':
        event.preventDefault()
        selectTab(tabs.length - 1)
        break
      default:
        break
    }
  }

  const overview = overviewQuery.data

  return (
    <section className="page statistics-page" aria-labelledby="statistics-heading">
      <h2 id="statistics-heading" tabIndex={-1}>
        Statistics
      </h2>

      {!historyUnavailable && (
        <div className="stats-tabs" role="tablist" aria-label="Statistics views">
          {tabs.map((tab, index) => {
            const selected = activeTab === tab.id
            return (
              <button
                key={tab.id}
                type="button"
                role="tab"
                id={`statistics-tab-${tab.id}`}
                className={`stats-tab${selected ? ' stats-tab--active' : ''}`}
                aria-selected={selected}
                aria-controls={`statistics-panel-${tab.id}`}
                tabIndex={selected ? 0 : -1}
                ref={(element) => {
                  tabRefs.current[index] = element
                }}
                onClick={() => selectTab(index)}
                onKeyDown={(event) => handleTabKeyDown(event, index)}
              >
                {tab.label}
              </button>
            )
          })}
        </div>
      )}

      {showHistory ? (
        <div
          className="stats-tabpanel"
          id="statistics-panel-history"
          role="tabpanel"
          aria-labelledby="statistics-tab-history"
          tabIndex={0}
        >
          <MealHistoryBrowser />
        </div>
      ) : requestedHistory ? (
        <section className="stats-section" aria-labelledby="statistics-history-heading">
          <h3 id="statistics-history-heading">Meal history</h3>
          <p className="form__error" role="alert">
            Meal history is not available to your account. Ask an Admin for the calendar.view
            permission.
          </p>
        </section>
      ) : (
        <div
          className="stats-tabpanel"
          id="statistics-panel-overview"
          role="tabpanel"
          aria-labelledby="statistics-tab-overview"
          tabIndex={0}
        >
          {!canViewOverview && (
            <p className="form__error" role="alert">
              You do not have permission to view statistics. Ask an Admin for the recipes.view
              permission.
            </p>
          )}

          {overview && (
            <p className="page-note">
              Today is {formatHouseholdDate(overview.windows.today)} ({overview.windows.timezone}).
            </p>
          )}

          {/* `isLoading` is `isPending && isFetching`: a disabled query (no
              `recipes.view`, so the fetch never starts) is pending forever but
              never fetching, and must not show a spinner beside the permission
              alert (issue #148). */}
          {overviewQuery.isLoading && <LoadingScreen label="Loading statistics…" />}

          {overviewQuery.isError && (
            <ErrorScreen
              title="Cannot load statistics"
              error={overviewQuery.error}
              onRetry={() => void overviewQuery.refetch()}
            />
          )}

          {overview && <Overview overview={overview} canEditRecipes={canEditRecipes} />}
        </div>
      )}
    </section>
  )
}

function Overview({
  overview,
  canEditRecipes,
}: {
  overview: StatisticsOverview
  canEditRecipes: boolean
}) {
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
          canEditRecipes={canEditRecipes}
        />
      </section>

      <section className="stats-section" aria-labelledby="stats-least-recently-eaten">
        <h3 id="stats-least-recently-eaten">Least recently eaten</h3>
        <RankedRecipeList
          items={overview.least_recently_eaten}
          emptyLabel="No recipes have been eaten yet."
          canEditRecipes={canEditRecipes}
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

/**
 * One ranked row. A live recipe links to its edit page only for a caller who
 * may edit (`recipes.edit`); a view-only member sees the same name as plain
 * text, because the edit page refuses them (issue #148). A tombstone keeps its
 * snapshot name as plain text either way.
 */
function RankedRecipeList({
  items,
  emptyLabel,
  canEditRecipes,
}: {
  items: RankedRecipe[]
  emptyLabel: string
  canEditRecipes: boolean
}) {
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
            {item.recipe_id !== null && canEditRecipes ? (
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
      {SPECIAL_EVENT_TYPES.map((type) => {
        // A type the server omitted is shown as an explicit absence marker, not
        // as `0`: a real zero is a count, while a missing key means the response
        // did not honour the contract and must stay visible (issue #148).
        const count = byType.get(type)
        return (
          <li key={type} className="stats-event">
            <span className="stats-event__label">{SPECIAL_EVENT_LABELS[type]}</span>
            <span className="stats-event__count">{count === undefined ? '—' : count}</span>
          </li>
        )
      })}
    </ul>
  )
}

function formatTimes(times: number): string {
  return times === 1 ? 'Eaten once' : `Eaten ${times} times`
}

function formatMeals(count: number): string {
  return count === 1 ? '1 meal' : `${count} meals`
}
