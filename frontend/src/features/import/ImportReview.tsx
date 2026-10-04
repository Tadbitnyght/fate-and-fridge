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
import { useIngredients, useProteins, useTags, useUnits } from '../reference-data/useReferenceData'
import { IngredientFields, type PickerOption } from '../recipes/IngredientFields'
import { InstructionFields } from '../recipes/InstructionFields'
import { ReferenceCheckboxes } from '../recipes/ReferenceCheckboxes'
import { buildRecipeRequest, validateRecipeForm, type RecipeFormState } from '../recipes/recipeForm'
import { useCreateRecipe } from '../recipes/useRecipes'
import {
  firstFieldError,
  extractServerFieldErrors,
  type FieldErrors,
} from '../../utils/fieldErrors'
import { fromImportDraft } from './importForm'
import { useRecipeImageFromUrl } from './useImport'

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

function MatchWarning({ label, matches }: { label: string; matches: ImportReference[] }) {
  if (matches.length === 0) {
    return null
  }
  const shown = matches.slice(0, MAX_NAME_MATCHES)
  return (
    <div className="import-review__warning" role="status">
      <p>{label} An existing recipe has a similar name:</p>
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

  const ingredientsQuery = useIngredients('', false)
  const unitsQuery = useUnits('', false)
  const proteinsQuery = useProteins('', false)
  const tagsQuery = useTags('')

  const selected =
    result.recipes.find((candidate) => candidate.index === selectedIndex) ?? result.recipes[0]

  const [state, setState] = useState<RecipeFormState>(() => fromImportDraft(selected))
  const [loadedKey, setLoadedKey] = useState(selected.index)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [imageWarning, setImageWarning] = useState<string | null>(null)
  const [useImage, setUseImage] = useState(selected.image_source_url !== null)
  const errorSummaryRef = useRef<HTMLDivElement>(null)

  // Re-seed the editable state when the reviewer picks a different candidate.
  // Adjusting state during render is React's documented derive-from-props
  // pattern and avoids a cascading effect render.
  if (loadedKey !== selected.index) {
    setLoadedKey(selected.index)
    setState(fromImportDraft(selected))
    setErrors({})
    setFormError(null)
    setImageWarning(null)
    setUseImage(selected.image_source_url !== null)
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
    setImageWarning(null)
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)
    setImageWarning(null)

    const validation = validateRecipeForm(state)
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
        setImageWarning(message)
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

      <MatchWarning
        label={
          result.source_url_match !== null
            ? 'This page has already been imported once.'
            : 'Check for duplicates.'
        }
        matches={selected.name_matches}
      />

      {selected.warnings.includes('ingredient_unknown') && (
        <p className="form__notice" role="status">
          Some ingredient lines could not be matched to your ingredient list. Choose an ingredient
          for each highlighted line before saving.
        </p>
      )}

      {imageWarning !== null && (
        <p className="form__notice" role="status">
          {imageWarning}
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
              onChange={(event) =>
                updateState((current) => ({ ...current, name: event.target.value }))
              }
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
