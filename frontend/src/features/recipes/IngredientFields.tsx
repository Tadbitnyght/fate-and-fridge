import { useState } from 'react'

import { fieldProps } from '../../components/fieldProps'
import type { UnitDimension } from '../../api/referenceData'
import type { FieldErrors } from '../../utils/fieldErrors'
import {
  UNIT_DIMENSIONS,
  UNIT_DIMENSION_LABELS,
  parseBaseFactorInput,
  requiresFactor,
} from '../reference-data/unitForm'
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

/**
 * Import-only resolution metadata for a row. `IngredientFields`
 * stays reusable for the normal recipe form, so every unknown-ingredient/unit
 * affordance is opt-in through {@link IngredientFieldsProps.unknownByRowKey}.
 */
export interface UnknownIngredientInfo {
  /** The original parsed line, shown verbatim. */
  raw: string
  /** Parsed canonical-name text, offered as the default create name. */
  name: string
  /** Draft suggestions the user can map the line to in one tap. */
  suggestions: PickerOption[]
  /** Unrecognized unit on the line, or `null` when the unit resolved. */
  unit: UnknownUnitInfo | null
}

/**
 * Import-only resolution metadata for a raw unit the parser could not match
 *The suggested dimension is present only when the parser could infer
 * it; otherwise the user must pick one. Package-size conversion is never
 * guessed.
 */
export interface UnknownUnitInfo {
  /** The original parsed unit text, shown verbatim. */
  raw: string
  /** Suggested dimension when obvious, otherwise `null`. */
  suggestedDimension: 'mass' | 'volume' | 'count' | null
  /** Existing units the user can map to in one tap. */
  suggestions: PickerOption[]
}

/** Inline "create ingredient" wiring, only supplied when the actor may do it. */
export interface IngredientCreateControls {
  /** Active grocery departments for the optional department picker. */
  departments: PickerOption[]
  /** Row key with a create request in flight, if any. */
  pendingRowKey: string | null
  /** Per-row create error message, keyed by row key. */
  errorByRowKey: Record<string, string>
  onCreate: (row: IngredientFormRow, canonicalName: string, departmentId: number | null) => void
}

/**
 * Inline "create unit" wiring, only supplied when the actor holds
 * `reference_data.edit`. The chosen dimension is always explicit: the user
 * selects it (defaulting to the parser's suggestion when there was one) and
 * supplies a base factor for mass/volume units rather than having one guessed.
 */
export interface UnitCreateControls {
  /** Row key with a unit-create request in flight, if any. */
  pendingRowKey: string | null
  /** Per-row unit-create error message, keyed by row key. */
  errorByRowKey: Record<string, string>
  onCreate: (
    row: IngredientFormRow,
    symbol: string,
    displayName: string,
    dimension: UnitDimension,
    baseFactor: number | undefined,
  ) => void
}

type RowUnknownByKey = Record<string, UnknownIngredientInfo>

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

/**
 * Permission-gated inline ingredient creation. Only rendered when the
 * draft reports `capabilities.can_create_reference_data`; the call itself is
 * still authorised server-side by the M2 `POST /ingredients` endpoint.
 */
function CreateIngredientInline({
  row,
  defaultName,
  createControls,
}: {
  row: IngredientFormRow
  defaultName: string
  createControls: IngredientCreateControls
}) {
  const [name, setName] = useState(defaultName)
  const [departmentId, setDepartmentId] = useState('')
  const pending = createControls.pendingRowKey === row.key
  const error = createControls.errorByRowKey[row.key]
  const nameFieldId = `${row.key}-create-name`
  const departmentFieldId = `${row.key}-create-department`

  return (
    <div className="ingredient-row__create">
      <p className="form__hint">No good match? Add it once and reuse it everywhere.</p>
      <div className="ingredient-row__create-fields">
        <div className="form__field">
          <label htmlFor={nameFieldId}>New ingredient name</label>
          <input
            id={nameFieldId}
            type="text"
            autoComplete="off"
            value={name}
            disabled={pending}
            onChange={(event) => setName(event.target.value)}
          />
        </div>
        <div className="form__field">
          <label htmlFor={departmentFieldId}>Department (optional)</label>
          <select
            id={departmentFieldId}
            value={departmentId}
            disabled={pending}
            onChange={(event) => setDepartmentId(event.target.value)}
          >
            <option value="">No department</option>
            {createControls.departments.map((department) => (
              <option key={department.id} value={department.id}>
                {department.label}
              </option>
            ))}
          </select>
        </div>
      </div>
      {error !== undefined && (
        <p className="form__error" role="alert">
          {error}
        </p>
      )}
      <button
        type="button"
        disabled={pending || name.trim().length === 0}
        onClick={() =>
          createControls.onCreate(
            row,
            name.trim(),
            departmentId === '' ? null : Number(departmentId),
          )
        }
      >
        {pending ? 'Adding…' : 'Add to ingredient list'}
      </button>
    </div>
  )
}

/**
 * Permission-gated inline unit creation. The dimension is never
 * inferred from packaging: it defaults to the parser's suggestion when one
 * exists, otherwise the user must pick, and mass/volume units additionally
 * require an explicit base factor. A duplicate create maps the row to the
 * existing unit server-side (`UNIT_EXISTS` with `details.existing_id`).
 */
function CreateUnitInline({
  row,
  defaultSymbol,
  suggestedDimension,
  createControls,
}: {
  row: IngredientFormRow
  defaultSymbol: string
  suggestedDimension: 'mass' | 'volume' | 'count' | null
  createControls: UnitCreateControls
}) {
  const [symbol, setSymbol] = useState(defaultSymbol)
  const [displayName, setDisplayName] = useState(defaultSymbol)
  // Never guess the dimension: preselect only when the parser was confident.
  const [dimension, setDimension] = useState<UnitDimension | ''>(suggestedDimension ?? '')
  const [factor, setFactor] = useState('')
  const [factorError, setFactorError] = useState<string | null>(null)
  const pending = createControls.pendingRowKey === row.key
  const error = createControls.errorByRowKey[row.key]
  const symbolFieldId = `${row.key}-create-unit-symbol`
  const displayFieldId = `${row.key}-create-unit-display`
  const dimensionFieldId = `${row.key}-create-unit-dimension`
  const factorFieldId = `${row.key}-create-unit-factor`
  const factorRequired = dimension !== '' && requiresFactor(dimension)

  function handleCreate(): void {
    if (dimension === '') {
      return
    }
    let baseFactor: number | undefined
    if (factorRequired) {
      const parsed = parseBaseFactorInput(factor)
      if (parsed.error !== undefined) {
        setFactorError(parsed.error)
        return
      }
      baseFactor = parsed.factor
    }
    setFactorError(null)
    createControls.onCreate(row, symbol.trim(), displayName.trim(), dimension, baseFactor)
  }

  return (
    <div className="ingredient-row__create ingredient-row__create-unit">
      <p className="form__hint">
        No matching unit? Add it once and reuse it everywhere. Choose the dimension yourself —
        package sizes are never converted automatically.
      </p>
      <div className="ingredient-row__create-fields">
        <div className="form__field">
          <label htmlFor={symbolFieldId}>New unit symbol</label>
          <input
            id={symbolFieldId}
            type="text"
            autoComplete="off"
            value={symbol}
            disabled={pending}
            onChange={(event) => setSymbol(event.target.value)}
          />
        </div>
        <div className="form__field">
          <label htmlFor={displayFieldId}>Display name</label>
          <input
            id={displayFieldId}
            type="text"
            autoComplete="off"
            value={displayName}
            disabled={pending}
            onChange={(event) => setDisplayName(event.target.value)}
          />
        </div>
        <div className="form__field">
          <label htmlFor={dimensionFieldId}>Dimension</label>
          <select
            id={dimensionFieldId}
            value={dimension}
            disabled={pending}
            onChange={(event) => {
              setDimension(event.target.value as UnitDimension | '')
              setFactorError(null)
            }}
          >
            <option value="">Choose a dimension…</option>
            {UNIT_DIMENSIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>
        {factorRequired && (
          <div className="form__field">
            <label htmlFor={factorFieldId}>Base factor</label>
            <input
              id={factorFieldId}
              type="text"
              inputMode="decimal"
              autoComplete="off"
              value={factor}
              disabled={pending}
              placeholder="e.g. 1"
              onChange={(event) => {
                setFactor(event.target.value)
                setFactorError(null)
              }}
            />
          </div>
        )}
      </div>
      {factorError !== null && (
        <p className="form__error" role="alert">
          {factorError}
        </p>
      )}
      {error !== undefined && (
        <p className="form__error" role="alert">
          {error}
        </p>
      )}
      <button
        type="button"
        disabled={
          pending ||
          symbol.trim().length === 0 ||
          displayName.trim().length === 0 ||
          dimension === ''
        }
        onClick={handleCreate}
      >
        {pending ? 'Adding…' : 'Add to unit list'}
      </button>
    </div>
  )
}

function IngredientRowEditor({
  row,
  ariaLabel,
  prefix,
  ingredientOptions,
  unitOptions,
  errors,
  unknown,
  createControls,
  unitCreateControls,
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
  unknown?: UnknownIngredientInfo
  createControls?: IngredientCreateControls
  unitCreateControls?: UnitCreateControls
  onRowChange: (row: IngredientFormRow) => void
  onRemove: () => void
  onMove: (delta: number) => void
  canMoveUp: boolean
  canMoveDown: boolean
}) {
  const ingredientError = errors[`${prefix}.ingredient_id`]
  const quantityError = errors[`${prefix}.quantity`]
  const unitError = errors[`${prefix}.unit_id`]
  const quantityHint =
    row.quantity.trim().length > 0 && Number.isFinite(Number(row.quantity))
      ? formatQuantity(row.quantity)
      : null
  const ingredientId = `${prefix}-ingredient`
  const quantityId = `${prefix}-quantity`
  const unitId = `${prefix}-unit`
  const noteId = `${prefix}-note`
  // A line is unresolved while it arrived unknown and still has no selected
  // reference; mapping it via the select, a suggestion, or inline creation
  // sets `ingredientId` and clears the state.
  const needsIngredient = unknown !== undefined && row.ingredientId === ''
  // A genuinely unrecognized raw unit blocks saving until the user
  // maps or creates a unit. Blank units (including a `raw: null` absent unit
  // from the server) and already-resolved units do not.
  const unknownUnit =
    unknown?.unit !== null && unknown !== undefined && (unknown.unit.raw ?? '').trim().length > 0
      ? unknown.unit
      : null
  const needsUnit = unknownUnit !== null && row.unitId === ''
  const rowUnresolved = needsIngredient || needsUnit
  const unknownNoticeId = `${ingredientId}-unknown`
  const ingredientDescribedBy = [
    ingredientError !== undefined ? `${ingredientId}-error` : null,
    needsIngredient ? unknownNoticeId : null,
  ]
    .filter((value): value is string => value !== null)
    .join(' ')
  const unitDescribedBy = [
    unitError !== undefined ? `${unitId}-error` : null,
    needsUnit ? `${unitId}-unknown` : null,
  ]
    .filter((value): value is string => value !== null)
    .join(' ')

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
    <li className={`ingredient-row${rowUnresolved ? ' ingredient-row--unknown' : ''}`}>
      <div className="ingredient-row__grid">
        <div className="form__field">
          <label htmlFor={ingredientId}>Ingredient</label>
          <select
            {...fieldProps(ingredientId, { error: ingredientError })}
            aria-describedby={ingredientDescribedBy.length > 0 ? ingredientDescribedBy : undefined}
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
          {needsIngredient && (
            <p className="ingredient-row__unknown-note" id={unknownNoticeId} role="status">
              Needs an ingredient before this recipe can be saved.
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
            aria-describedby={unitDescribedBy.length > 0 ? unitDescribedBy : undefined}
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
          {unitError && (
            <p className="form__error" id={`${unitId}-error`}>
              {unitError}
            </p>
          )}
          {needsUnit && (
            <p className="ingredient-row__unknown-note" id={`${unitId}-unknown`} role="status">
              Choose or create a unit before this recipe can be saved.
            </p>
          )}
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

      {needsIngredient && unknown !== undefined && (
        <div className="ingredient-row__unknown">
          <p className="ingredient-row__unknown-raw">
            Imported line: <q>{unknown.raw}</q>
          </p>
          {unknown.suggestions.length > 0 && (
            <div className="ingredient-row__suggestions">
              <span className="ingredient-row__suggestions-label">Suggestions:</span>
              {unknown.suggestions.map((suggestion) => (
                <button
                  key={suggestion.id}
                  type="button"
                  aria-label={`Use ${suggestion.label} for ${ariaLabel}`}
                  onClick={() =>
                    onRowChange({
                      ...row,
                      ingredientId: String(suggestion.id),
                      ingredientName: suggestion.label,
                      ingredientActive: true,
                    })
                  }
                >
                  {suggestion.label}
                </button>
              ))}
            </div>
          )}
          {createControls !== undefined ? (
            <CreateIngredientInline
              row={row}
              defaultName={row.ingredientName || unknown.name}
              createControls={createControls}
            />
          ) : (
            <p className="form__hint">
              You cannot create ingredients. Choose an existing ingredient, or ask an Admin to add
              this one.
            </p>
          )}
        </div>
      )}

      {needsUnit && unknownUnit !== null && (
        // `needsUnit` implies `unknownUnit` passed the non-empty check above, so
        // `unknownUnit.raw` is a non-null, non-blank string at both display sites.
        <div className="ingredient-row__unknown ingredient-row__unknown-unit">
          <p className="ingredient-row__unknown-raw">
            Unrecognized unit: <q>{unknownUnit.raw.trim()}</q>
            {unknownUnit.suggestedDimension !== null && (
              <> (looks like {UNIT_DIMENSION_LABELS[unknownUnit.suggestedDimension]})</>
            )}
          </p>
          {unknownUnit.suggestions.length > 0 && (
            <div className="ingredient-row__suggestions">
              <span className="ingredient-row__suggestions-label">Suggestions:</span>
              {unknownUnit.suggestions.map((suggestion) => (
                <button
                  key={suggestion.id}
                  type="button"
                  aria-label={`Use ${suggestion.label} for ${ariaLabel} unit`}
                  onClick={() =>
                    onRowChange({
                      ...row,
                      unitId: String(suggestion.id),
                      unitLabel: suggestion.label,
                      unitActive: suggestion.isActive,
                    })
                  }
                >
                  {suggestion.label}
                </button>
              ))}
            </div>
          )}
          {unitCreateControls !== undefined ? (
            <CreateUnitInline
              row={row}
              defaultSymbol={row.unitLabel || unknownUnit.raw.trim()}
              suggestedDimension={unknownUnit.suggestedDimension}
              createControls={unitCreateControls}
            />
          ) : (
            <p className="form__hint">
              You cannot create units. Choose an existing unit, set the line to no unit, or ask an
              Admin to add this one.
            </p>
          )}
        </div>
      )}

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
            aria-label={`Move up ${ariaLabel}`}
            disabled={!canMoveUp}
            onClick={() => onMove(-1)}
          >
            Move up
          </button>
          <button
            type="button"
            aria-label={`Move down ${ariaLabel}`}
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
  unknownByRowKey,
  createControls,
  unitCreateControls,
  onChange,
}: {
  rows: IngredientFormRow[]
  prefixBase: string
  labelBase: string
  ingredientOptions: PickerOption[]
  unitOptions: PickerOption[]
  errors: FieldErrors
  unknownByRowKey?: RowUnknownByKey
  createControls?: IngredientCreateControls
  unitCreateControls?: UnitCreateControls
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
          unknown={unknownByRowKey?.[row.key]}
          createControls={createControls}
          unitCreateControls={unitCreateControls}
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
 * Ungrouped ingredients plus optional ordered ingredient groups. Every
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
  unknownByRowKey,
  createControls,
  unitCreateControls,
}: {
  rows: IngredientFormRow[]
  groups: IngredientGroupFormRow[]
  onRowsChange: (rows: IngredientFormRow[]) => void
  onGroupsChange: (groups: IngredientGroupFormRow[]) => void
  ingredientOptions: PickerOption[]
  unitOptions: PickerOption[]
  errors: FieldErrors
  /** Import-only unknown-line resolution metadata. */
  unknownByRowKey?: RowUnknownByKey
  /** Import-only ingredient inline-create wiring, absent when not permitted. */
  createControls?: IngredientCreateControls
  /** Import-only unit inline-create wiring, absent when not permitted. */
  unitCreateControls?: UnitCreateControls
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
            unknownByRowKey={unknownByRowKey}
            createControls={createControls}
            unitCreateControls={unitCreateControls}
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
                      aria-label={`Move up group ${groupIndex + 1}`}
                      disabled={groupIndex === 0}
                      onClick={() => onGroupsChange(moveItem(groups, groupIndex, -1))}
                    >
                      Move up
                    </button>
                    <button
                      type="button"
                      aria-label={`Move down group ${groupIndex + 1}`}
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
                  // Group rows are never import-seeded: the import draft has no
                  // `ingredient_groups` (docs/API.md §15), so these rows carry no
                  // parsed line and no unknown-unit metadata. Do not pass
                  // `unknownByRowKey`/create controls here unless grouped import
                  // drafts (and their seeding) are added too — a half-wired
                  // pass-through would look like it resolved units when it could
                  // not (BUG-002).
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
