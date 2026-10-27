import { formatHouseholdDate } from '../../lib/dateFormat'
import { NO_HISTORY_LABEL, type RecipeHistoryMetrics } from './recipeMetrics'

function MetricRow({
  label,
  value,
  format,
}: {
  label: string
  value: string | null
  format?: (value: string) => string
}) {
  const text = value === null ? null : format === undefined ? value : format(value)
  return (
    <div>
      <dt>{label}</dt>
      <dd>{text === null ? <span className="recipe-muted">{NO_HISTORY_LABEL}</span> : text}</dd>
    </div>
  )
}

/**
 * Read-only Last eaten / Next planned / Times eaten block (M11-06). Renders on
 * the recipe card (default `recipe-card__stats` styling) and the recipe
 * detail/editor's History section. Null metrics show the shared no-history
 * fallback, and a retired recipe keeps showing its past meals.
 */
export function RecipeHistoryStats({
  metrics,
  className = 'recipe-card__stats',
}: {
  metrics: RecipeHistoryMetrics
  className?: string
}) {
  return (
    <dl className={className}>
      <MetricRow label="Last eaten" value={metrics.last_eaten} format={formatHouseholdDate} />
      <MetricRow label="Next planned" value={metrics.next_planned} format={formatHouseholdDate} />
      <MetricRow
        label="Times eaten"
        value={metrics.times_eaten === null ? null : String(metrics.times_eaten)}
      />
    </dl>
  )
}
