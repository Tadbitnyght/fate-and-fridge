import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import {
  RECIPE_STATUS_LABELS,
  RECIPE_STATUSES,
  type RecipeHardDeleteResult,
  type RecipeStatus,
} from '../../api/recipes'
import {
  DangerousActionDialog,
  MAX_DANGEROUS_ACTION_REASON_LENGTH,
  type DangerousActionValues,
} from '../../components/DangerousActionDialog'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import {
  extractServerFieldErrors,
  firstFieldError,
  type FieldErrors,
} from '../../utils/fieldErrors'
import { useIngredients, useProteins, useTags, useUnits } from '../reference-data/useReferenceData'
import { useRecipeAccess } from './access'
import { IngredientFields, type PickerOption } from './IngredientFields'
import { InstructionFields } from './InstructionFields'
import { RecipeHistoryStats } from './RecipeHistoryStats'
import { RecipeImageField } from './RecipeImageField'
import { ReferenceCheckboxes } from './ReferenceCheckboxes'
import {
  buildRecipeRequest,
  emptyRecipeFormState,
  fromRecipe,
  validateRecipeForm,
  type RecipeFormState,
} from './recipeForm'
import { useCreateRecipe, useHardDeleteRecipe, useRecipe, useUpdateRecipe } from './useRecipes'

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

/**
 * Human-readable summary of a successful hard delete. It is reported on
 * `/recipes` (via router state) because the deleted recipe's editor is gone by
 * the time the mutation resolves.
 */
function hardDeleteNotice(result: RecipeHardDeleteResult): string {
  const planned = result.removed_planned_entries === 1 ? 'meal' : 'meals'
  const past = result.tombstoned_entries === 1 ? 'meal' : 'meals'
  return `Recipe deleted permanently. Removed ${result.removed_planned_entries} planned ${planned}; kept ${result.tombstoned_entries} past ${past} as read-only history.`
}

/**
 * Turns a hard-delete failure into a readable message. Server codes are the
 * contract (docs/API.md §4, §10); `details.field_errors` is surfaced first
 * because it is the most specific message the server can give.
 */
function describeHardDeleteError(caught: unknown): string {
  if (!isApiError(caught)) {
    return 'Deleting the recipe failed. Check the server and try again.'
  }

  const fieldMessage = firstFieldError(extractServerFieldErrors(caught.details))
  switch (caught.code) {
    case ErrorCodes.ConfirmationMismatch:
      return 'The typed confirmation did not match. Type the recipe name exactly to confirm.'
    case ErrorCodes.ReasonRequired:
      return fieldMessage ?? 'Enter a reason for deleting this recipe.'
    case ErrorCodes.ReasonTooLong:
      return `The reason must be at most ${MAX_DANGEROUS_ACTION_REASON_LENGTH} characters.`
    case ErrorCodes.ReasonInvalid:
      return fieldMessage ?? 'The reason contains characters the server rejects.'
    case ErrorCodes.NotFound:
      return 'This recipe no longer exists. It may already have been deleted.'
    case ErrorCodes.Forbidden:
      return 'Only an Admin can permanently delete a recipe.'
    default:
      return fieldMessage ?? caught.message
  }
}

/**
 * Accessible recipe create/edit form (M3-10, issue #37). Every collection is a
 * full replacement on save (docs/API.md §23). Picker selects only choose
 * existing references — no inline creation or free-text fallback (M3 §1.20).
 *
 * Admins additionally get the M12-09 danger zone (hard delete) on the
 * detail/editor route only; the server enforces the Admin role.
 */
export function RecipeFormPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const location = useLocation()
  const { canEdit, isAdmin } = useRecipeAccess()

  // The import review flow navigates here after saving; if the optional photo
  // download failed it passes a non-fatal warning through router state (M4-08).
  const imageWarning =
    typeof (location.state as { imageWarning?: unknown } | null)?.imageWarning === 'string'
      ? (location.state as { imageWarning: string }).imageWarning
      : null

  const recipeId = id === undefined ? undefined : Number(id)
  const isEdit = recipeId !== undefined

  const recipeQuery = useRecipe(isEdit ? recipeId : undefined)
  const createRecipe = useCreateRecipe()
  const updateRecipe = useUpdateRecipe()
  const hardDelete = useHardDeleteRecipe()

  const [state, setState] = useState<RecipeFormState>(emptyRecipeFormState)
  const [loadedRecipeId, setLoadedRecipeId] = useState<number | null>(null)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [savedNotice, setSavedNotice] = useState<string | null>(null)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const errorSummaryRef = useRef<HTMLDivElement>(null)

  const ingredientsQuery = useIngredients('', false)
  const unitsQuery = useUnits('', false)
  const proteinsQuery = useProteins('', false)
  const tagsQuery = useTags('')

  // Initialize the editable state once per loaded recipe. Adjusting state
  // during render (rather than in an effect) is React's documented pattern for
  // deriving state from a prop and avoids a cascading render.
  if (isEdit && recipeQuery.data && loadedRecipeId !== recipeQuery.data.id) {
    setLoadedRecipeId(recipeQuery.data.id)
    setState(fromRecipe(recipeQuery.data))
  }

  useEffect(() => {
    if (Object.keys(errors).length > 0) {
      errorSummaryRef.current?.focus()
    }
  }, [errors])

  function updateState(updater: (current: RecipeFormState) => RecipeFormState): void {
    setState(updater)
    setErrors({})
    setFormError(null)
    setSavedNotice(null)
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)
    setSavedNotice(null)

    const validation = validateRecipeForm(state)
    if (Object.keys(validation).length > 0) {
      setErrors(validation)
      return
    }

    const request = buildRecipeRequest(state)
    try {
      if (isEdit && recipeId !== undefined) {
        await updateRecipe.mutateAsync({ id: recipeId, request })
        setSavedNotice('Recipe saved.')
      } else {
        const created = await createRecipe.mutateAsync(request)
        navigate(`/recipes/${created.id}/edit`, { replace: true })
      }
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
    }
  }

  async function handleHardDelete(values: DangerousActionValues): Promise<void> {
    if (recipeId === undefined) {
      return
    }
    setDeleteError(null)
    try {
      const result = await hardDelete.mutateAsync({
        id: recipeId,
        request: { reason: values.reason, confirmation: values.confirmation ?? '' },
      })
      navigate('/recipes', { state: { notice: hardDeleteNotice(result) } })
    } catch (caught) {
      setDeleteError(describeHardDeleteError(caught))
    }
  }

  const headingText = isEdit ? 'Edit recipe' : 'New recipe'

  if (!canEdit) {
    return (
      <section className="page recipe-form" aria-labelledby="recipe-form-heading">
        <h2 id="recipe-form-heading" tabIndex={-1}>
          {headingText}
        </h2>
        <p className="form__error" role="alert">
          You do not have permission to edit recipes. Ask an Admin for the recipes.edit permission.
        </p>
        <p>
          <Link to="/recipes">Back to recipes</Link>
        </p>
      </section>
    )
  }

  if (isEdit && recipeQuery.isError) {
    return (
      <section className="page recipe-form" aria-labelledby="recipe-form-heading">
        <h2 id="recipe-form-heading" tabIndex={-1}>
          {headingText}
        </h2>
        <div role="alert">
          <p>Cannot load this recipe. Check the server and try again.</p>
          <button type="button" onClick={() => void recipeQuery.refetch()}>
            Try again
          </button>
        </div>
        <p>
          <Link to="/recipes">Back to recipes</Link>
        </p>
      </section>
    )
  }

  if (isEdit && recipeQuery.isPending) {
    return (
      <section className="page recipe-form" aria-labelledby="recipe-form-heading">
        <h2 id="recipe-form-heading" tabIndex={-1}>
          {headingText}
        </h2>
        <p className="page-note" role="status">
          Loading recipe…
        </p>
      </section>
    )
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

  const proteinOptions = (proteinsQuery.data ?? [])
    .filter((protein) => protein.is_active)
    .map((protein) => ({ id: protein.id, name: protein.name, isActive: protein.is_active }))
  for (const protein of recipeQuery.data?.proteins ?? []) {
    if (!proteinOptions.some((option) => option.id === protein.id)) {
      proteinOptions.push({ id: protein.id, name: protein.name, isActive: protein.is_active })
    }
  }

  const tagOptions = (tagsQuery.data ?? []).map((tag) => ({
    id: tag.id,
    name: tag.name,
    isActive: true,
  }))
  for (const tag of recipeQuery.data?.tags ?? []) {
    if (!tagOptions.some((option) => option.id === tag.id)) {
      tagOptions.push({ id: tag.id, name: tag.name, isActive: true })
    }
  }

  const pending = createRecipe.isPending || updateRecipe.isPending

  return (
    <section className="page recipe-form" aria-labelledby="recipe-form-heading">
      <h2 id="recipe-form-heading" tabIndex={-1}>
        {headingText}
      </h2>

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

        {formError && (
          <p className="form__error" role="alert">
            {formError}
          </p>
        )}
        {savedNotice && (
          <p className="form__notice" role="status">
            {savedNotice}
          </p>
        )}

        {imageWarning !== null && (
          <p className="form__notice" role="status">
            {imageWarning}
          </p>
        )}

        <fieldset className="form__fieldset">
          <legend>Basics</legend>

          <Field id="recipe-name" label="Name" error={errors['name']}>
            <input
              {...fieldProps('recipe-name', { error: errors['name'] })}
              type="text"
              autoComplete="off"
              value={state.name}
              onChange={(event) =>
                updateState((current) => ({ ...current, name: event.target.value }))
              }
            />
          </Field>

          <Field id="recipe-description" label="Description">
            <textarea
              {...fieldProps('recipe-description')}
              rows={3}
              value={state.description}
              onChange={(event) =>
                updateState((current) => ({ ...current, description: event.target.value }))
              }
            />
          </Field>

          <div className="form__row">
            <Field id="recipe-prep" label="Prep time (minutes)" error={errors['prep_minutes']}>
              <input
                {...fieldProps('recipe-prep', { error: errors['prep_minutes'] })}
                type="text"
                inputMode="numeric"
                autoComplete="off"
                value={state.prepMinutes}
                onChange={(event) =>
                  updateState((current) => ({ ...current, prepMinutes: event.target.value }))
                }
              />
            </Field>
            <Field id="recipe-cook" label="Cook time (minutes)" error={errors['cook_minutes']}>
              <input
                {...fieldProps('recipe-cook', { error: errors['cook_minutes'] })}
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

          <div className="form__field">
            <label htmlFor="recipe-status">Randomization status</label>
            <select
              id="recipe-status"
              value={state.status}
              onChange={(event) =>
                updateState((current) => ({
                  ...current,
                  status: event.target.value as RecipeStatus,
                }))
              }
            >
              {RECIPE_STATUSES.map((value) => (
                <option key={value} value={value}>
                  {RECIPE_STATUS_LABELS[value]}
                </option>
              ))}
            </select>
          </div>

          <div className="recipe-form__flags">
            {checkboxRow('recipe-favorite', 'Favorite', state.isFavorite, (checked) =>
              updateState((current) => ({ ...current, isFavorite: checked })),
            )}
            {checkboxRow('recipe-vegan', 'Vegan', state.isVegan, (checked) =>
              updateState((current) => ({ ...current, isVegan: checked })),
            )}
            {checkboxRow('recipe-vegetarian', 'Vegetarian', state.isVegetarian, (checked) =>
              updateState((current) => ({ ...current, isVegetarian: checked })),
            )}
          </div>
        </fieldset>

        <fieldset className="form__fieldset">
          <legend>Source</legend>
          <div className="form__row">
            <Field id="recipe-source-site" label="Source site">
              <input
                {...fieldProps('recipe-source-site')}
                type="text"
                autoComplete="off"
                value={state.sourceSite}
                onChange={(event) =>
                  updateState((current) => ({ ...current, sourceSite: event.target.value }))
                }
              />
            </Field>
            <Field id="recipe-author" label="Author">
              <input
                {...fieldProps('recipe-author')}
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
            id="recipe-source-url"
            label="Source URL"
            hint="Optional. http:// or https:// only; the M3 UI never fetches it."
            error={errors['source_url']}
          >
            <input
              {...fieldProps('recipe-source-url', { error: errors['source_url'], hint: true })}
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
        />

        <InstructionFields
          sections={state.sections}
          onChange={(sections) => updateState((current) => ({ ...current, sections }))}
        />

        <ReferenceCheckboxes
          legend="Primary proteins"
          idPrefix="recipe-proteins"
          options={proteinOptions}
          selected={state.proteinIds}
          onChange={(ids) => updateState((current) => ({ ...current, proteinIds: ids }))}
        />
        <ReferenceCheckboxes
          legend="Tags"
          idPrefix="recipe-tags"
          options={tagOptions}
          selected={state.tagIds}
          onChange={(ids) => updateState((current) => ({ ...current, tagIds: ids }))}
        />

        {isEdit && recipeId !== undefined ? (
          <RecipeImageField recipeId={recipeId} image={recipeQuery.data?.image ?? null} />
        ) : (
          <fieldset className="form__fieldset">
            <legend>Photo</legend>
            <p className="form__hint">
              Save the recipe first, then add a photo from the edit screen.
            </p>
          </fieldset>
        )}

        <Field id="recipe-notes" label="Notes">
          <textarea
            {...fieldProps('recipe-notes')}
            rows={4}
            value={state.notes}
            onChange={(event) =>
              updateState((current) => ({ ...current, notes: event.target.value }))
            }
          />
        </Field>

        {isEdit && recipeQuery.data && (
          <fieldset className="form__fieldset">
            <legend>History</legend>
            <p className="form__hint">
              Read-only metrics from the household calendar. Retiring a recipe does not remove the
              meals already eaten.
            </p>
            <RecipeHistoryStats metrics={recipeQuery.data.statistics} />
          </fieldset>
        )}

        <div className="recipe-form__actions">
          <Link className="recipes__new" to="/recipes">
            Cancel
          </Link>
          <button type="submit" className="button--primary" disabled={pending}>
            {pending ? 'Saving…' : isEdit ? 'Save changes' : 'Create recipe'}
          </button>
        </div>
      </form>

      {isEdit && isAdmin && recipeQuery.data && (
        <section className="recipe-form__danger" aria-labelledby="recipe-danger-heading">
          <h3 id="recipe-danger-heading">Danger zone</h3>
          <p>
            Permanently deleting a recipe removes it and its photo forever. Retire it instead if you
            only want to stop planning it.
          </p>
          <button
            type="button"
            className="button--danger"
            onClick={() => {
              setDeleteError(null)
              setDeleteOpen(true)
            }}
          >
            Delete this recipe permanently
          </button>
        </section>
      )}

      {deleteOpen && recipeQuery.data && (
        <DangerousActionDialog
          title="Delete recipe permanently"
          description={`Permanently delete "${recipeQuery.data.name}".`}
          warnings={[
            'This is irreversible. There is no undo and nothing can be restored.',
            "The recipe's photo is deleted.",
            'Planned meals for this recipe today and in the future are removed.',
            'Past meals are kept as read-only history: their statistics and meal history remain, but the meals no longer link to a recipe.',
          ]}
          reasonLabel="Reason"
          acknowledgementLabel="I understand this permanently deletes the recipe and cannot be undone."
          confirmation={{
            label: 'Type the recipe name to confirm',
            expected: recipeQuery.data.name,
            hint: 'The confirmation is case-sensitive and must match the recipe name exactly.',
          }}
          confirmLabel="Delete permanently"
          busy={hardDelete.isPending}
          error={deleteError}
          onConfirm={(values) => void handleHardDelete(values)}
          onClose={() => {
            if (!hardDelete.isPending) {
              setDeleteOpen(false)
            }
          }}
        />
      )}
    </section>
  )
}
