import type { GroceryDepartment, GroceryItem } from '../../api/groceries'
import { formatQuantityLabel, formatSourceDate, orderDepartments } from './groceryFormat'

function GroceryItemRow({
  item,
  canEdit,
  pending,
  onToggle,
  onEdit,
  onDelete,
}: {
  item: GroceryItem
  canEdit: boolean
  pending: boolean
  onToggle: (item: GroceryItem, checked: boolean) => void
  onEdit: (item: GroceryItem) => void
  onDelete: (item: GroceryItem) => void
}) {
  const quantity = formatQuantityLabel(item.quantity_text, item.unit)
  const checkboxLabel = item.checked
    ? `Mark ${item.name} as not bought`
    : `Mark ${item.name} as bought`

  return (
    <li className={item.checked ? 'grocery-item grocery-item--checked' : 'grocery-item'}>
      <label className="grocery-item__label">
        <input
          type="checkbox"
          className="grocery-item__checkbox"
          checked={item.checked}
          disabled={!canEdit || pending}
          aria-label={checkboxLabel}
          onChange={(event) => onToggle(item, event.currentTarget.checked)}
        />
        <span className="grocery-item__body">
          <span className="grocery-item__name">{item.name}</span>
          {quantity.length > 0 && <span className="grocery-item__quantity">{quantity}</span>}
          {item.source_type === 'manual' && (
            <span className="grocery-item__badge">
              <span aria-hidden="true">✎</span> Manual
            </span>
          )}
        </span>
      </label>

      {item.sources.length > 0 && (
        <ul className="grocery-item__sources" aria-label={`Meals using ${item.name}`}>
          {item.sources.map((source) => (
            <li key={`${source.recipe_id}-${source.date}`}>
              {source.recipe_name} &mdash; {formatSourceDate(source.date)}
            </li>
          ))}
        </ul>
      )}

      {canEdit && item.manual_item_id !== null && (
        <div className="grocery-item__actions">
          <button type="button" aria-label={`Edit ${item.name}`} onClick={() => onEdit(item)}>
            Edit
          </button>
          <button type="button" aria-label={`Delete ${item.name}`} onClick={() => onDelete(item)}>
            Delete
          </button>
        </div>
      )}
    </li>
  )
}

/**
 * The mobile-first grocery list: departments with sticky headers in server
 * order with the "Other" group last (docs/M8_PLAN.md §1.12). Server order is
 * preserved — checked lines already sit below unchecked ones within a
 * department — and a checked line is dimmed and struck through by CSS.
 */
export function GroceryList({
  departments,
  canEdit,
  pendingIdentities,
  onToggle,
  onEditManual,
  onDeleteManual,
}: {
  departments: GroceryDepartment[]
  canEdit: boolean
  pendingIdentities: ReadonlySet<string>
  onToggle: (item: GroceryItem, checked: boolean) => void
  onEditManual: (item: GroceryItem) => void
  onDeleteManual: (item: GroceryItem) => void
}) {
  const ordered = orderDepartments(departments)

  return (
    <ul className="grocery-departments" aria-label="Grocery items">
      {ordered.map((department) => {
        const key = department.department_id ?? 'other'
        const headingId = `grocery-department-${key}`
        return (
          <li key={String(key)} className="grocery-department">
            <h3 className="grocery-department__header" id={headingId}>
              {department.name}
            </h3>
            <ul className="grocery-items" aria-labelledby={headingId}>
              {department.items.map((item) => (
                <GroceryItemRow
                  key={item.identity}
                  item={item}
                  canEdit={canEdit}
                  pending={pendingIdentities.has(item.identity)}
                  onToggle={onToggle}
                  onEdit={onEditManual}
                  onDelete={onDeleteManual}
                />
              ))}
            </ul>
          </li>
        )
      })}
    </ul>
  )
}
