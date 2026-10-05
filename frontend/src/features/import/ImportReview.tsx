import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type {
  ImportParseResponse,
  ImportRecipeDraft,
  ImportReference,
} from '../../api/importRecipes'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { Modal } from '../../components/Modal'
import {
  useCreateIngredient,
  useCreateUnit,
  useDepartments,
  useIngredients,
  useProteins,
  useTags,
  useUnits,
} from '../reference-data/useReferenceData'
import type { UnitDimension } from '../../api/referenceData'
import {
  IngredientFields,
  type IngredientCreateControls,
  type PickerOption,
  type UnitCreateControls,
  type UnknownIngredientInfo,
  type UnknownUnitInfo,
} from '../recipes/IngredientFields'
import { InstructionFields } from '../recipes/InstructionFields'
import { ReferenceCheckboxes } from '../recipes/ReferenceCheckboxes'
import {
  buildRecipeRequest,
  validateRecipeForm,
  type IngredientFormRow,
  type RecipeFormState,
} from '../recipes/recipeForm'
import { useCreateRecipe } from '../recipes/useRecipes'
import {
  firstFieldError,
  extractServerFieldErrors,
  type FieldErrors,
} from '../../utils/fieldErrors'
import {
  fromImportDraft,
  seedUnknownLines,
  validateUnknownUnits,
  type UnknownIngredientLine,
} from './importForm'
import { useCheckImport, useRecipeImageFromUrl } from './useImport'

const MAX_NAME_MATCHES = 5

function checkboxRow(
  id: string,
  label: string,
  checked: boolean,
  onChange: (checked: boolean) => void,
) {
  return (
    <label className="recipes__checkbox" htmlFor={id}>
      <input
        id={id}
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
      />
      {label}
    </label>
  )
}

function CandidatePicker({
  recipes,
  selectedIndex,
  onSelect,
}: {
  recipes: ImportRecipeDraft[]
  selectedIndex: number
  onSelect: (index: number) => void
}) {
  return (
    <section
      className="form__fieldset import-review__candidates"
      aria-labelledby="import-candidates"
    >
      <h3 id="import-candidates">This page has {recipes.length} recipes</h3>
      <p className="form__hint">Choose the one you want to import.</p>
      <ul className="import-review__candidate-list">
        {recipes.map((recipe) => (
          <li key={recipe.index}>
            <label className="recipes__checkbox">
              <input
                type="radio"
                name="import-candidate"
                value={recipe.index}
                checked={selectedIndex === recipe.index}
                onChange={() => onSelect(recipe.index)}
              />
              {recipe.name}
            </label>
          </li>
        ))}
      </ul>
    </section>
  )
}

function MatchWarning({ matches }: { matches: ImportReference[] }) {
  if (matches.length === 0) {
    return null
  }
  const shown = matches.slice(0, MAX_NAME_MATCHES)
  return (
    <div className="import-review__warning" role="status">
      <p>An existing recipe has a similar name:</p>
      <ul>
        {shown.map((match) => (
          <li key={match.id}>
            <Link to={`/recipes/${match.id}/edit`}>{match.name}</Link>
          </li>
        ))}
      </ul>
    </div>
  )
}

/**
 * Strong duplicate warning for an exact normalized source-URL match (M4-11,
 * docs/M4_PLAN.md §1.9, docs/PRODUCT_SPEC.md §29). Rendered with the shared
 * accessible `Modal`, so the rest of the page is inert until the reviewer picks
 * one of the three approved actions: open the existing recipe, import anyway, or
 * cancel. Escape/backdrop also cancel the import, which discards the draft.
 */
function DuplicateSourceDialog({
  existing,
  onImportAnyway,
  onCancel,
}: {
  existing: ImportReference
  onImportAnyway: () => void
  onCancel: () => void
}) {
  return (
    <Modal title="This page has already been imported" onClose={onCancel}>
      <div className="modal__body">
        <p>
          A recipe with this exact source URL already exists: <strong>{existing.name}</strong>.
        </p>
        <p>Open the existing recipe, or import this page as a new recipe anyway.</p>
      </div>
      <div className="modal__actions">
        <button type="button" onClick={onCancel}>
          Cancel
        </button>
        <button type="button" onClick={onImportAnyway}>
          Import anyway
        </button>
        <Link className="button--primary" to={`/recipes/${existing.id}/edit`}>
          Open existing recipe
        </Link>
      </div>
    </Modal>
  )
}

/**
 * Editable import draft review (M4-08, docs/M4_PLAN.md §2.3). Mirrors the
 * RecipeFormPage controls: name/description/source/author/prep/cook/notes,
 * flags, ingredient lines (with parsed quantity/unit/name/prep note),
 * instruction sections/steps, proteins/tags, and an optional photo preview.
 * Saving reuses the normal `POST /recipes` contract; a chosen image source is
 * downloaded afterwards and a failure is a non-fatal warning.
 */
export function ImportReview({
  result,
  selectedIndex,
  onSelectIndex,
  onCancel,
}: {
  result: ImportParseResponse
  selectedIndex: number
  onSelectIndex: (index: number) => void
  onCancel: () => void
}) {
  const navigate = useNavigate()
  const createRecipe = useCreateRecipe()
  const imageFromUrl = useRecipeImageFromUrl()
  const checkImport = useCheckImport()
  const createIngredient = useCreateIngredient()
  const createUnit = useCreateUnit()

  const ingredientsQuery = useIngredients('', false)
  const unitsQuery = useUnits('', false)
  const proteinsQuery = useProteins('', false)
  const tagsQuery = useTags('')
  const departmentsQuery = useDepartments('')

  const selected =
    result.recipes.find((candidate) => candidate.index === selectedIndex) ?? result.recipes[0]

  const [seed] = useState(() => {
    const seeded = fromImportDraft(selected)
    return { seeded, unknown: seedUnknownLines(selected, seeded.ingredients) }
  })
  const [state, setState] = useState<RecipeFormState>(seed.seeded)
  const [loadedKey, setLoadedKey] = useState(selected.index)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [useImage, setUseImage] = useState(selected.image_source_url !== null)
  const [acknowledgedDuplicate, setAcknowledgedDuplicate] = useState(false)
  const [nameMatches, setNameMatches] = useState<ImportReference[]>(selected.name_matches)
  const [nameCheckStatus, setNameCheckStatus] = useState<'idle' | 'checking' | 'error'>('idle')
  const [unknownLines, setUnknownLines] = useState<Record<string, UnknownIngredientLine>>(
    seed.unknown,
  )
  const [createPendingRowKey, setCreatePendingRowKey] = useState<string | null>(null)
  const [createErrorByRowKey, setCreateErrorByRowKey] = useState<Record<string, string>>({})
  const [unitCreatePendingRowKey, setUnitCreatePendingRowKey] = useState<string | null>(null)
  const [unitCreateErrorByRowKey, setUnitCreateErrorByRowKey] = useState<Record<string, string>>({})
  const errorSummaryRef = useRef<HTMLDivElement>(null)

  // Rename-recheck state (M4-12): the name the server last checked, the latest
  // typed name (so a slow response for a superseded name is discarded), and the
  // pending debounce timer.
  const lastCheckedNameRef = useRef(selected.name)
  const latestNameRef = useRef(selected.name)
  const checkImportRef = useRef(checkImport.mutateAsync)
  const nameCheckTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  useEffect(() => {
    checkImportRef.current = checkImport.mutateAsync
  })

  // Re-seed the editable state when the reviewer picks a different candidate.
  // Adjusting state during render is React's documented derive-from-props
  // pattern and avoids a cascading effect render.
  if (loadedKey !== selected.index) {
    const seeded = fromImportDraft(selected)
    setLoadedKey(selected.index)
    setState(seeded)
    setUnknownLines(seedUnknownLines(selected, seeded.ingredients))
    setCreateErrorByRowKey({})
    setCreatePendingRowKey(null)
    setUnitCreateErrorByRowKey({})
    setUnitCreatePendingRowKey(null)
    setErrors({})
    setFormError(null)
    setUseImage(selected.image_source_url !== null)
    setAcknowledgedDuplicate(false)
    setNameMatches(selected.name_matches)
    setNameCheckStatus('idle')
  }

  // Keep the rename-recheck baseline in sync when the reviewer switches
  // candidate and drop any timer left over from the previous candidate.
  // Refs cannot be written during render, so this runs after commit.
  useEffect(() => {
    lastCheckedNameRef.current = selected.name
    latestNameRef.current = selected.name
    if (nameCheckTimerRef.current !== null) {
      clearTimeout(nameCheckTimerRef.current)
      nameCheckTimerRef.current = null
    }
  }, [selected])

  useEffect(() => {
    if (Object.keys(errors).length > 0) {
      errorSummaryRef.current?.focus()
    }
  }, [errors])

  // Clear any pending rename recheck when the review unmounts.
  useEffect(
    () => () => {
      if (nameCheckTimerRef.current !== null) {
        clearTimeout(nameCheckTimerRef.current)
      }
    },
    [],
  )

  function updateState(updater: (current: RecipeFormState) => RecipeFormState): void {
    setState(updater)
    setErrors({})
    setFormError(null)
  }

  /**
   * Debounced rename recheck (M4-12). Editing the name re-runs the server's
   * similar-name scoring through `POST /import/check` and refreshes the warning.
   * The draft's own matches are the seed, so no request runs until the name
   * actually changes, and a late response for a superseded name is discarded.
   */
  function handleNameChange(value: string): void {
    updateState((current) => ({ ...current, name: value }))
    latestNameRef.current = value

    if (nameCheckTimerRef.current !== null) {
      clearTimeout(nameCheckTimerRef.current)
      nameCheckTimerRef.current = null
    }

    const trimmed = value.trim()
    if (trimmed.length === 0) {
      setNameMatches([])
      setNameCheckStatus('idle')
      return
    }
    if (trimmed === lastCheckedNameRef.current) {
      setNameCheckStatus('idle')
      return
    }

    setNameCheckStatus('checking')
    nameCheckTimerRef.current = setTimeout(() => {
      nameCheckTimerRef.current = null
      void checkImportRef
        .current({ name: trimmed })
        .then((response) => {
          if (latestNameRef.current.trim() !== trimmed) {
            return
          }
          lastCheckedNameRef.current = trimmed
          setNameMatches(response.name_matches)
          setNameCheckStatus('idle')
        })
        .catch(() => {
          if (latestNameRef.current.trim() !== trimmed) {
            return
          }
          setNameCheckStatus('error')
        })
    }, 400)
  }

  function mapRowToIngredient(rowKey: string, id: number, name: string): void {
    setState((current) => ({
      ...current,
      ingredients: current.ingredients.map((row) =>
        row.key === rowKey
          ? { ...row, ingredientId: String(id), ingredientName: name, ingredientActive: true }
          : row,
      ),
    }))
    setErrors({})
    setFormError(null)
  }

  /**
   * Permission-gated inline creation (M4-09). The draft's
   * `capabilities.can_create_reference_data` decides whether this is reachable;
   * the M2 endpoint still authorises the actor server-side. A duplicate create
   * maps the line to the existing ingredient instead of erroring.
   */
  async function handleCreateIngredient(
    row: IngredientFormRow,
    canonicalName: string,
    departmentId: number | null,
  ): Promise<void> {
    setCreatePendingRowKey(row.key)
    setCreateErrorByRowKey((current) => {
      const next = { ...current }
      delete next[row.key]
      return next
    })
    try {
      const created = await createIngredient.mutateAsync({
        canonical_name: canonicalName,
        department_id: departmentId,
      })
      mapRowToIngredient(row.key, created.id, created.canonical_name)
    } catch (caught) {
      if (isApiError(caught) && caught.code === ErrorCodes.IngredientExists) {
        const existingId = caught.details['existing_id']
        if (typeof existingId === 'number') {
          mapRowToIngredient(row.key, existingId, canonicalName)
          return
        }
      }
      if (isApiError(caught) && caught.code === ErrorCodes.Forbidden) {
        setCreateErrorByRowKey((current) => ({
          ...current,
          [row.key]: 'You do not have permission to create ingredients.',
        }))
        return
      }
      if (isApiError(caught) && caught.code === ErrorCodes.ValidationFailed) {
        setCreateErrorByRowKey((current) => ({
          ...current,
          [row.key]: 'Enter a valid ingredient name.',
        }))
        return
      }
      setCreateErrorByRowKey((current) => ({
        ...current,
        [row.key]: 'Creating the ingredient failed. Check the server and try again.',
      }))
    } finally {
      setCreatePendingRowKey(null)
    }
  }

  function mapRowToUnit(rowKey: string, id: number, label: string): void {
    setState((current) => ({
      ...current,
      ingredients: current.ingredients.map((row) =>
        row.key === rowKey
          ? { ...row, unitId: String(id), unitLabel: label, unitActive: true }
          : row,
      ),
    }))
    setErrors({})
    setFormError(null)
  }

  /**
   * Permission-gated inline unit creation (M4-10). The draft's
   * `capabilities.can_create_reference_data` decides whether this is reachable;
   * the M2 `POST /units` endpoint still authorises the actor server-side. The
   * dimension and base factor are always explicit — package sizes are never
   * guessed. A duplicate create (`UNIT_EXISTS`) maps the line to the existing
   * unit through `details.existing_id`.
   */
  async function handleCreateUnit(
    row: IngredientFormRow,
    symbol: string,
    displayName: string,
    dimension: UnitDimension,
    baseFactor: number | undefined,
  ): Promise<void> {
    setUnitCreatePendingRowKey(row.key)
    setUnitCreateErrorByRowKey((current) => {
      const next = { ...current }
      delete next[row.key]
      return next
    })
    try {
      const created = await createUnit.mutateAsync({
        symbol,
        display_name: displayName,
        dimension,
        ...(baseFactor === undefined ? {} : { base_factor: baseFactor }),
      })
      mapRowToUnit(row.key, created.id, created.symbol)
    } catch (caught) {
      if (isApiError(caught) && caught.code === ErrorCodes.UnitExists) {
        const existingId = caught.details['existing_id']
        if (typeof existingId === 'number') {
          mapRowToUnit(row.key, existingId, symbol)
          return
        }
      }
      if (isApiError(caught) && caught.code === ErrorCodes.Forbidden) {
        setUnitCreateErrorByRowKey((current) => ({
          ...current,
          [row.key]: 'You do not have permission to create units.',
        }))
        return
      }
      if (isApiError(caught) && caught.code === ErrorCodes.ValidationFailed) {
        setUnitCreateErrorByRowKey((current) => ({
          ...current,
          [row.key]: 'Enter a valid unit symbol, display name, and dimension.',
        }))
        return
      }
      setUnitCreateErrorByRowKey((current) => ({
        ...current,
        [row.key]: 'Creating the unit failed. Check the server and try again.',
      }))
    } finally {
      setUnitCreatePendingRowKey(null)
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)

    const validation = {
      ...validateRecipeForm(state),
      ...validateUnknownUnits(state, unknownLines),
    }
    if (Object.keys(validation).length > 0) {
      setErrors(validation)
      return
    }

    const request = buildRecipeRequest(state)
    let created
    try {
      created = await createRecipe.mutateAsync(request)
    } catch (caught) {
      if (isApiError(caught) && caught.code === ErrorCodes.ValidationFailed) {
        const serverErrors = extractServerFieldErrors(caught.details)
        setErrors(serverErrors)
        setFormError(serverErrors['name'] ? null : firstFieldError(serverErrors))
        return
      }
      if (isApiError(caught) && caught.code === ErrorCodes.RecipeExists) {
        setErrors({ name: 'A recipe with this name already exists.' })
        return
      }
      setFormError(
        isApiError(caught) ? caught.message : 'Saving failed. Check the server and try again.',
      )
      return
    }

    // The recipe is safely saved. Downloading the photo is best-effort: a
    // failure must never lose the recipe, so surface a non-fatal warning and
    // continue to the recipe.
    if (useImage && selected.image_source_url !== null) {
      try {
        await imageFromUrl.mutateAsync({ id: created.id, sourceUrl: selected.image_source_url })
      } catch (caught) {
        const message =
          isApiError(caught) && caught.code === ErrorCodes.ImportUrlBlocked
            ? 'The recipe was saved, but its photo could not be downloaded for security reasons.'
            : 'The recipe was saved, but its photo could not be downloaded.'
        navigate(`/recipes/${created.id}/edit`, { replace: true, state: { imageWarning: message } })
        return
      }
    }

    navigate(`/recipes/${created.id}/edit`, { replace: true })
  }

  const ingredientOptions: PickerOption[] = (ingredientsQuery.data ?? []).map((ingredient) => ({
    id: ingredient.id,
    label: ingredient.canonical_name,
    isActive: ingredient.is_active,
  }))
  const unitOptions: PickerOption[] = (unitsQuery.data ?? []).map((unit) => ({
    id: unit.id,
    label: unit.symbol,
    isActive: unit.is_active,
  }))

  // Draft suggestions can reference ingredients outside the first page of the
  // list, so merge them into the picker options (active, exact label) to keep
  // every suggested id selectable.
  const optionById = new Map(ingredientOptions.map((option) => [option.id, option]))
  const unitOptionById = new Map(unitOptions.map((option) => [option.id, option]))
  for (const line of Object.values(unknownLines)) {
    for (const suggestion of line.suggestions) {
      if (!optionById.has(suggestion.id)) {
        const option: PickerOption = {
          id: suggestion.id,
          label: suggestion.name,
          isActive: true,
        }
        optionById.set(suggestion.id, option)
        ingredientOptions.push(option)
      }
    }
    for (const suggestion of line.unit?.suggestions ?? []) {
      if (!unitOptionById.has(suggestion.id)) {
        const option: PickerOption = {
          id: suggestion.id,
          label: suggestion.symbol,
          isActive: true,
        }
        unitOptionById.set(suggestion.id, option)
        unitOptions.push(option)
      }
    }
  }

  const unknownByRowKey: Record<string, UnknownIngredientInfo> = {}
  for (const row of state.ingredients) {
    const line = unknownLines[row.key]
    if (line === undefined) {
      continue
    }
    const unit: UnknownUnitInfo | null =
      line.unit === null
        ? null
        : {
            raw: line.unit.raw,
            suggestedDimension: line.unit.suggestedDimension,
            suggestions: line.unit.suggestions.map(
              (suggestion) =>
                unitOptionById.get(suggestion.id) ?? {
                  id: suggestion.id,
                  label: suggestion.symbol,
                  isActive: true,
                },
            ),
          }
    unknownByRowKey[row.key] = {
      raw: line.raw,
      name: line.name,
      suggestions: line.suggestions.map(
        (suggestion) =>
          optionById.get(suggestion.id) ?? {
            id: suggestion.id,
            label: suggestion.name,
            isActive: true,
          },
      ),
      unit,
    }
  }

  const departmentOptions: PickerOption[] = (departmentsQuery.data ?? [])
    .filter((department) => department.is_active)
    .map((department) => ({
      id: department.id,
      label: department.name,
      isActive: department.is_active,
    }))

  // Only offer inline create when the server said the actor holds
  // `reference_data.edit`; otherwise the UI shows ask-an-admin guidance.
  const createControls: IngredientCreateControls | undefined = result.capabilities
    .can_create_reference_data
    ? {
        departments: departmentOptions,
        pendingRowKey: createPendingRowKey,
        errorByRowKey: createErrorByRowKey,
        onCreate: (row, canonicalName, departmentId) =>
          void handleCreateIngredient(row, canonicalName, departmentId),
      }
    : undefined

  const unitCreateControls: UnitCreateControls | undefined = result.capabilities
    .can_create_reference_data
    ? {
        pendingRowKey: unitCreatePendingRowKey,
        errorByRowKey: unitCreateErrorByRowKey,
        onCreate: (row, symbol, displayName, dimension, baseFactor) =>
          void handleCreateUnit(row, symbol, displayName, dimension, baseFactor),
      }
    : undefined

  const proteinOptions = (proteinsQuery.data ?? [])
    .filter((protein) => protein.is_active)
    .map((protein) => ({ id: protein.id, name: protein.name, isActive: protein.is_active }))
  const tagOptions = (tagsQuery.data ?? []).map((tag) => ({
    id: tag.id,
    name: tag.name,
    isActive: true,
  }))

  const pending = createRecipe.isPending

  return (
    <section className="page recipe-form import-review" aria-labelledby="import-review-heading">
      <h2 id="import-review-heading" tabIndex={-1}>
        Review imported recipe
      </h2>

      <p className="page-note">
        Imported from <strong>{result.source.site}</strong>
        {result.source.author !== null && <> by {result.source.author}</>}. Check every field before
        saving — parsing is best-effort.
      </p>

      {result.recipes.length > 1 && (
        <CandidatePicker
          recipes={result.recipes}
          selectedIndex={selected.index}
          onSelect={onSelectIndex}
        />
      )}

      {result.source_url_match !== null && !acknowledgedDuplicate && (
        <DuplicateSourceDialog
          existing={result.source_url_match}
          onImportAnyway={() => setAcknowledgedDuplicate(true)}
          onCancel={onCancel}
        />
      )}

      <MatchWarning matches={nameMatches} />
      {nameCheckStatus === 'checking' && (
        <p className="form__hint" role="status">
          Checking for similar names…
        </p>
      )}
      {nameCheckStatus === 'error' && (
        <p className="form__hint" role="status">
          Could not check for similar names. You can still save the recipe.
        </p>
      )}

      {selected.warnings.includes('ingredient_unknown') && (
        <p className="form__notice" role="status">
          Some ingredient lines could not be matched to your ingredient list. Choose an ingredient
          for each highlighted line before saving.
        </p>
      )}

      <form
        className="form recipe-form__form"
        onSubmit={(event) => void handleSubmit(event)}
        noValidate
      >
        {Object.keys(errors).length > 0 && (
          <div className="recipe-form__errors" role="alert" tabIndex={-1} ref={errorSummaryRef}>
            <h3>Fix these problems before saving</h3>
            <ul>
              {Object.entries(errors).map(([field, message]) => (
                <li key={field}>{message}</li>
              ))}
            </ul>
          </div>
        )}

        {formError !== null && (
          <p className="form__error" role="alert">
            {formError}
          </p>
        )}

        <fieldset className="form__fieldset">
          <legend>Basics</legend>

          <Field id="import-name" label="Name" error={errors['name']}>
            <input
              {...fieldProps('import-name', { error: errors['name'] })}
              type="text"
              autoComplete="off"
              value={state.name}
              onChange={(event) => handleNameChange(event.target.value)}
            />
          </Field>

          <Field id="import-description" label="Description">
            <textarea
              {...fieldProps('import-description')}
              rows={3}
              value={state.description}
              onChange={(event) =>
                updateState((current) => ({ ...current, description: event.target.value }))
              }
            />
          </Field>

          <div className="form__row">
            <Field id="import-prep" label="Prep time (minutes)" error={errors['prep_minutes']}>
              <input
                {...fieldProps('import-prep', { error: errors['prep_minutes'] })}
                type="text"
                inputMode="numeric"
                autoComplete="off"
                value={state.prepMinutes}
                onChange={(event) =>
                  updateState((current) => ({ ...current, prepMinutes: event.target.value }))
                }
              />
            </Field>
            <Field id="import-cook" label="Cook time (minutes)" error={errors['cook_minutes']}>
              <input
                {...fieldProps('import-cook', { error: errors['cook_minutes'] })}
                type="text"
                inputMode="numeric"
                autoComplete="off"
                value={state.cookMinutes}
                onChange={(event) =>
                  updateState((current) => ({ ...current, cookMinutes: event.target.value }))
                }
              />
            </Field>
          </div>

          <div className="recipe-form__flags">
            {checkboxRow('import-favorite', 'Favorite', state.isFavorite, (checked) =>
              updateState((current) => ({ ...current, isFavorite: checked })),
            )}
            {checkboxRow('import-vegan', 'Vegan', state.isVegan, (checked) =>
              updateState((current) => ({ ...current, isVegan: checked })),
            )}
            {checkboxRow('import-vegetarian', 'Vegetarian', state.isVegetarian, (checked) =>
              updateState((current) => ({ ...current, isVegetarian: checked })),
            )}
          </div>
        </fieldset>

        <fieldset className="form__fieldset">
          <legend>Source</legend>
          <div className="form__row">
            <Field id="import-source-site" label="Source site">
              <input
                {...fieldProps('import-source-site')}
                type="text"
                autoComplete="off"
                value={state.sourceSite}
                onChange={(event) =>
                  updateState((current) => ({ ...current, sourceSite: event.target.value }))
                }
              />
            </Field>
            <Field id="import-author" label="Author">
              <input
                {...fieldProps('import-author')}
                type="text"
                autoComplete="off"
                value={state.author}
                onChange={(event) =>
                  updateState((current) => ({ ...current, author: event.target.value }))
                }
              />
            </Field>
          </div>
          <Field
            id="import-source-url"
            label="Source URL"
            hint="Optional. http:// or https:// only."
            error={errors['source_url']}
          >
            <input
              {...fieldProps('import-source-url', { error: errors['source_url'], hint: true })}
              type="text"
              autoComplete="off"
              value={state.sourceUrl}
              onChange={(event) =>
                updateState((current) => ({ ...current, sourceUrl: event.target.value }))
              }
            />
          </Field>
        </fieldset>

        <IngredientFields
          rows={state.ingredients}
          groups={state.groups}
          onRowsChange={(rows) => updateState((current) => ({ ...current, ingredients: rows }))}
          onGroupsChange={(groups) => updateState((current) => ({ ...current, groups }))}
          ingredientOptions={ingredientOptions}
          unitOptions={unitOptions}
          errors={errors}
          unknownByRowKey={unknownByRowKey}
          createControls={createControls}
          unitCreateControls={unitCreateControls}
        />

        <InstructionFields
          sections={state.sections}
          onChange={(sections) => updateState((current) => ({ ...current, sections }))}
        />

        <ReferenceCheckboxes
          legend="Primary proteins"
          idPrefix="import-proteins"
          options={proteinOptions}
          selected={state.proteinIds}
          onChange={(ids) => updateState((current) => ({ ...current, proteinIds: ids }))}
        />
        <ReferenceCheckboxes
          legend="Tags"
          idPrefix="import-tags"
          options={tagOptions}
          selected={state.tagIds}
          onChange={(ids) => updateState((current) => ({ ...current, tagIds: ids }))}
        />

        {selected.image_source_url !== null && (
          <fieldset className="form__fieldset">
            <legend>Photo</legend>
            <img
              className="recipe-photo-preview"
              src={selected.image_source_url}
              alt={`Preview of the photo for ${selected.name}`}
              onError={(event) => {
                event.currentTarget.style.display = 'none'
              }}
            />
            <label className="recipes__checkbox">
              <input
                type="checkbox"
                checked={useImage}
                onChange={(event) => setUseImage(event.target.checked)}
              />
              Download this photo after saving
            </label>
            <p className="form__hint">
              If the photo cannot be downloaded, the recipe is still saved without it.
            </p>
          </fieldset>
        )}

        <Field id="import-notes" label="Notes">
          <textarea
            {...fieldProps('import-notes')}
            rows={4}
            value={state.notes}
            onChange={(event) =>
              updateState((current) => ({ ...current, notes: event.target.value }))
            }
          />
        </Field>

        <div className="recipe-form__actions">
          <button type="button" onClick={onCancel} disabled={pending}>
            Cancel
          </button>
          <button type="submit" className="button--primary" disabled={pending}>
            {pending ? 'Saving…' : 'Save recipe'}
          </button>
        </div>
      </form>
    </section>
  )
}
