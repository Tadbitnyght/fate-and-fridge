import { useState, type JSX } from 'react'
import { Link } from 'react-router-dom'

import type { GroceryDepartment, GroceryItem } from '../../api/groceries'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { useGroceriesAccess } from './access'
import {
  formatGeneratedAt,
  formatQuantityLabel,
  formatRangeSummary,
  formatSourceDate,
  orderDepartments,
} from './groceryFormat'
import { useGroceryList } from './useGroceries'

/**
 * One printable line: a decorative, screen-reader-hidden paper checkbox the
 * shopper ticks by hand, the name, the quantity/unit text, the manual badge
 * (the same icon+text badge as the list — never colour alone), and one
 * attribution row per contributing meal.
 */
function GroceryPrintItem({ item }: { item: GroceryItem }) {
  const quantity = formatQuantityLabel(item.quantity_text, item.unit)

  return (
    <li className="grocery-print__item">
      <div className="grocery-print__line">
        <span className="grocery-print__box" aria-hidden="true" />
        <span className="grocery-print__name">{item.name}</span>
        {quantity.length > 0 && <span className="grocery-print__quantity">{quantity}</span>}
        {item.source_type === 'manual' && (
          <span className="grocery-item__badge">
            <span aria-hidden="true">✎</span> Manual
          </span>
        )}
      </div>

      {item.sources.length > 0 && (
        <ul className="grocery-print__sources" aria-label={`Meals using ${item.name}`}>
          {item.sources.map((source) => (
            <li key={`${source.recipe_id}-${source.date}`}>
              {source.recipe_name} &mdash; {formatSourceDate(source.date)}
            </li>
          ))}
        </ul>
      )}
    </li>
  )
}

/**
 * Print-optimized grocery view (M8-08, docs/M8_PLAN.md §1.14). It renders the
 * same live projection as the M8-07 list (`GET /groceries`, no query → the
 * caller's remembered range) grouped by department in server order with the
 * "Other" group last, and excludes checked items by default. The
 * "Include checked items" toggle is the only control here and is independent of
 * the per-user `hide_checked` preference. The screen-only controls and app
 * chrome are hidden by `@media print` in `index.css`.
 */
export function GroceryPrintPage(): JSX.Element {
  const { canView } = useGroceriesAccess()
  const listQuery = useGroceryList(canView)
  const [includeChecked, setIncludeChecked] = useState(false)
  // Capture the stamp once so re-rendering the toggle does not move the clock.
  const [generatedAt] = useState(() => new Date())

  if (!canView) {
    return (
      <section className="page grocery-print-page" aria-labelledby="grocery-print-heading">
        <h2 id="grocery-print-heading" tabIndex={-1}>
          Print grocery list
        </h2>
        <p className="form__error" role="alert">
          You do not have permission to view groceries. Ask an Admin for the groceries.view
          permission.
        </p>
      </section>
    )
  }

  const range = listQuery.data?.range ?? null
  const departments = orderDepartments(listQuery.data?.departments ?? [])
    .map((department): GroceryDepartment => ({
      ...department,
      items: includeChecked ? department.items : department.items.filter((item) => !item.checked),
    }))
    .filter((department) => department.items.length > 0)

  const checkedHidden =
    !includeChecked &&
    (listQuery.data?.departments ?? []).some((department) =>
      department.items.some((item) => item.checked),
    )
  const headingSuffix = range ? `: ${formatRangeSummary(range.start, range.end)}` : ''

  return (
    <section className="page grocery-print-page" aria-labelledby="grocery-print-heading">
      <div className="grocery-print-controls">
        <Link to="/groceries" className="grocery-print-controls__back">
          Back to grocery list
        </Link>
        <label className="reference-data__checkbox grocery-print-controls__include">
          <input
            type="checkbox"
            checked={includeChecked}
            onChange={(event) => setIncludeChecked(event.currentTarget.checked)}
          />
          Include checked items
        </label>
        <button type="button" className="button--primary" onClick={() => window.print()}>
          Print
        </button>
      </div>

      <article className="grocery-print">
        <h2 id="grocery-print-heading" tabIndex={-1}>
          Grocery list{headingSuffix}
        </h2>
        <p className="grocery-print__generated">Generated {formatGeneratedAt(generatedAt)}</p>

        {listQuery.isPending && <LoadingScreen label="Loading groceries…" />}

        {listQuery.isError && (
          <ErrorScreen
            title="Cannot load groceries"
            error={listQuery.error}
            onRetry={() => void listQuery.refetch()}
          />
        )}

        {listQuery.data &&
          (departments.length === 0 ? (
            <p className="page-note">
              {checkedHidden
                ? 'No unchecked items to print. Turn on “Include checked items” to include checked items.'
                : 'No groceries for this range. Add a manual item or plan meals with recipes.'}
            </p>
          ) : (
            <ul className="grocery-print__departments" aria-label="Grocery items">
              {departments.map((department) => {
                const key = department.department_id ?? 'other'
                const headingId = `grocery-print-department-${key}`
                return (
                  <li key={String(key)} className="grocery-print__department">
                    <h3 className="grocery-print__department-header" id={headingId}>
                      {department.name}
                    </h3>
                    <ul className="grocery-print__items" aria-labelledby={headingId}>
                      {department.items.map((item) => (
                        <GroceryPrintItem key={item.identity} item={item} />
                      ))}
                    </ul>
                  </li>
                )
              })}
            </ul>
          ))}
      </article>
    </section>
  )
}
