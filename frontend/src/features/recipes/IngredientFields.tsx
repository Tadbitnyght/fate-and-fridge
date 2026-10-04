import { fieldProps } from '../../components/fieldProps'
import type { FieldErrors } from '../../utils/fieldErrors'
import { formatQuantity } from './quantity'
import {
  emptyIngredientGroup,
  emptyIngredientRow,
  type IngredientFormRow,
  type IngredientGroupFormRow,
} from './recipeForm'

/** An option rendered by the ingredient/unit pickers. */
export interface PickerOption {
  id: number
  label: string
  isActive: boolean
}

/** Keeps a retired currently-selected reference visible and selectable. */
function mergeCurrentOption(
  base: PickerOption[],
  idText: string,
  label: string,
  isActive: boolean,
): PickerOption[] {
  if (idText === '') {
    return base
  }
  const id = Number(idText)
  if (base.some((option) => option.id === id)) {
    return base
  }
  return [...base, { id, label: isActive ? label : `${label} (retired)`, isActive }]
}

function moveItem<T>(items: T[], index: number, delta: number): T[] {
  const target = index + delta
  if (target < 0 || target >= items.length) {
    return items
  }
  const next = [...items]
  const [item] = next.splice(index, 1)
  next.splice(target, 0, item)
  return next
}

function replaceAt<T>(items: T[], index: number, value: T): T[] {
  return items.map((item, current) => (current === index ? value : item))
}

function IngredientRowEditor({
  row,
  ariaLabel,
  prefix,
  ingredientOptions,
  unitOptions,
  errors,
  onRowChange,
  onRemove,
  onMove,
  canMoveUp,
  canMoveDown,
}: {
  row: IngredientFormRow
  ariaLabel: string
  prefix: string
  ingredientOptions: PickerOption[]
  unitOptions: PickerOption[]
  errors: FieldErrors
  onRowChange: (row: IngredientFormRow) => void
  onRemove: () => void
  onMove: (delta: number) => void
  canMoveUp: boolean
  canMoveDown: boolean
}) {
  const ingredientError = errors[`${prefix}.ingredient_id`]
  const quantityError = errors[`${prefix}.quantity`]
  const quantityHint =
    row.quantity.trim().length > 0 && Number.isFinite(Number(row.quantity))
      ? formatQuantity(row.quantity)
      : null
  const ingredientId = `${prefix}-ingredient`
  const quantityId = `${prefix}-quantity`
  const unitId = `${prefix}-unit`
  const noteId = `${prefix}-note`

  const currentIngredientOptions = mergeCurrentOption(
    ingredientOptions,
    row.ingredientId,
    row.ingredientName,
    row.ingredientActive,
  )
  const currentUnitOptions = mergeCurrentOption(
    unitOptions,
    row.unitId,
    row.unitLabel,
    row.unitActive,
  )

  return (
    <li className="ingredient-row">
      <div className="ingredient-row__grid">
        <div className="form__field">
          <label htmlFor={ingredientId}>Ingredient</label>
          <select
            {...fieldProps(ingredientId, { error: ingredientError })}
            value={row.ingredientId}
            onChange={(event) =>
              onRowChange({ ...row, ingredientId: event.target.value, ingredientName: '' })
            }
          >
            <option value="">Choose ingredient…</option>
            {currentIngredientOptions.map((option) => (
              <option key={option.id} value={option.id}>
                {option.label}
              </option>
            ))}
          </select>
          {ingredientError && (
            <p className="form__error" id={`${ingredientId}-error`}>
              {ingredientError}
            </p>
          )}
        </div>

        <div className="form__field">
          <label htmlFor={quantityId}>Quantity</label>
          <input
            {...fieldProps(quantityId, { error: quantityError })}
            type="text"
            inputMode="decimal"
            autoComplete="off"
            placeholder="1.5 or blank"
            value={row.quantity}
            onChange={(event) => onRowChange({ ...row, quantity: event.target.value })}
          />
          {quantityError && (
            <p className="form__error" id={`${quantityId}-error`}>
              {quantityError}
            </p>
          )}
          {!quantityError && quantityHint !== null && quantityHint !== row.quantity.trim() && (
            <p className="form__hint">Shows as {quantityHint}</p>
          )}
        </div>

        <div className="form__field">
          <label htmlFor={unitId}>Unit</label>
          <select
            id={unitId}
            value={row.unitId}
            onChange={(event) => onRowChange({ ...row, unitId: event.target.value, unitLabel: '' })}
          >
            <option value="">No unit</option>
            {currentUnitOptions.map((option) => (
              <option key={option.id} value={option.id}>
                {option.label}
              </option>
            ))}
          </select>
        </div>

        <div className="form__field">
          <label htmlFor={noteId}>Preparation note</label>
          <input
            id={noteId}
            type="text"
            autoComplete="off"
            placeholder="e.g. diced"
            value={row.preparationNote}
            onChange={(event) => onRowChange({ ...row, preparationNote: event.target.value })}
          />
        </div>
      </div>

      <div className="ingredient-row__footer">
        <label className="recipes__checkbox">
          <input
            type="checkbox"
            checked={row.isOptional}
            onChange={(event) => onRowChange({ ...row, isOptional: event.target.checked })}
          />
          Optional ingredient
        </label>
        <div className="ingredient-row__actions">
          <button
            type="button"
            aria-label={`Move ${ariaLabel} up`}
            disabled={!canMoveUp}
            onClick={() => onMove(-1)}
          >
            Move up
          </button>
          <button
            type="button"
            aria-label={`Move ${ariaLabel} down`}
            disabled={!canMoveDown}
            onClick={() => onMove(1)}
          >
            Move down
          </button>
          <button type="button" aria-label={`Remove ${ariaLabel}`} onClick={onRemove}>
            Remove
          </button>
        </div>
      </div>
    </li>
  )
}

function IngredientRowList({
  rows,
  prefixBase,
  labelBase,
  ingredientOptions,
  unitOptions,
  errors,
  onChange,
}: {
  rows: IngredientFormRow[]
  prefixBase: string
  labelBase: string
  ingredientOptions: PickerOption[]
  unitOptions: PickerOption[]
  errors: FieldErrors
  onChange: (rows: IngredientFormRow[]) => void
}) {
  return (
    <ul className="ingredient-list">
      {rows.map((row, index) => (
        <IngredientRowEditor
          key={row.key}
          row={row}
          ariaLabel={`${labelBase} ${index + 1}`}
          prefix={`${prefixBase}[${index}]`}
          ingredientOptions={ingredientOptions}
          unitOptions={unitOptions}
          errors={errors}
          onRowChange={(next) => onChange(replaceAt(rows, index, next))}
          onRemove={() => onChange(rows.filter((_, current) => current !== index))}
          onMove={(delta) => onChange(moveItem(rows, index, delta))}
          canMoveUp={index > 0}
          canMoveDown={index < rows.length - 1}
        />
      ))}
    </ul>
  )
}

/**
 * Ungrouped ingredients plus optional ordered ingredient groups (M3-10). Every
 * picker selects an existing reference — no free-text creation (M3 §1.20).
 */
export function IngredientFields({
  rows,
  groups,
  onRowsChange,
  onGroupsChange,
  ingredientOptions,
  unitOptions,
  errors,
}: {
  rows: IngredientFormRow[]
  groups: IngredientGroupFormRow[]
  onRowsChange: (rows: IngredientFormRow[]) => void
  onGroupsChange: (groups: IngredientGroupFormRow[]) => void
  ingredientOptions: PickerOption[]
  unitOptions: PickerOption[]
  errors: FieldErrors
}) {
  return (
    <>
      <fieldset className="form__fieldset">
        <legend>Ingredients</legend>
        {rows.length === 0 ? (
          <p className="form__hint">No ungrouped ingredients yet.</p>
        ) : (
          <IngredientRowList
            rows={rows}
            prefixBase="ingredients"
            labelBase="ingredient"
            ingredientOptions={ingredientOptions}
            unitOptions={unitOptions}
            errors={errors}
            onChange={onRowsChange}
          />
        )}
        <button type="button" onClick={() => onRowsChange([...rows, emptyIngredientRow()])}>
          Add ingredient
        </button>
      </fieldset>

      <fieldset className="form__fieldset">
        <legend>Ingredient groups</legend>
        {groups.length === 0 ? (
          <p className="form__hint">No ingredient groups yet.</p>
        ) : (
          <ul className="group-list">
            {groups.map((group, groupIndex) => (
              <li key={group.key} className="group-card">
                <div className="group-card__head">
                  <div className="form__field">
                    <label htmlFor={`${group.key}-title`}>Group title</label>
                    <input
                      id={`${group.key}-title`}
                      type="text"
                      autoComplete="off"
                      placeholder="e.g. Sauce"
                      value={group.title}
                      onChange={(event) =>
                        onGroupsChange(
                          replaceAt(groups, groupIndex, { ...group, title: event.target.value }),
                        )
                      }
                    />
                  </div>
                  <div className="group-card__actions">
                    <button
                      type="button"
                      aria-label={`Move group ${groupIndex + 1} up`}
                      disabled={groupIndex === 0}
                      onClick={() => onGroupsChange(moveItem(groups, groupIndex, -1))}
                    >
                      Move up
                    </button>
                    <button
                      type="button"
                      aria-label={`Move group ${groupIndex + 1} down`}
                      disabled={groupIndex === groups.length - 1}
                      onClick={() => onGroupsChange(moveItem(groups, groupIndex, 1))}
                    >
                      Move down
                    </button>
                    <button
                      type="button"
                      aria-label={`Remove group ${groupIndex + 1}`}
                      onClick={() =>
                        onGroupsChange(groups.filter((_, current) => current !== groupIndex))
                      }
                    >
                      Remove group
                    </button>
                  </div>
                </div>

                <IngredientRowList
                  rows={group.ingredients}
                  prefixBase={`ingredient_groups[${groupIndex}].ingredients`}
                  labelBase={`ingredient in group ${groupIndex + 1}`}
                  ingredientOptions={ingredientOptions}
                  unitOptions={unitOptions}
                  errors={errors}
                  onChange={(next) =>
                    onGroupsChange(replaceAt(groups, groupIndex, { ...group, ingredients: next }))
                  }
                />
                <button
                  type="button"
                  onClick={() =>
                    onGroupsChange(
                      replaceAt(groups, groupIndex, {
                        ...group,
                        ingredients: [...group.ingredients, emptyIngredientRow()],
                      }),
                    )
                  }
                >
                  Add ingredient to group
                </button>
              </li>
            ))}
          </ul>
        )}
        <button type="button" onClick={() => onGroupsChange([...groups, emptyIngredientGroup()])}>
          Add group
        </button>
      </fieldset>
    </>
  )
}
