import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { ImportParseResponse } from '../../api/importRecipes'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { useRecipeAccess } from '../recipes/access'
import { describeImportError } from './importForm'
import { useParseImportUrl } from './useImport'
import { ImportReview } from './ImportReview'

function retryAfterSeconds(error: unknown): number | null {
  if (!isApiError(error)) {
    return null
  }
  const raw = error.details['retry_after_seconds']
  if (typeof raw === 'number' && Number.isFinite(raw)) {
    return Math.max(0, Math.ceil(raw))
  }
  return null
}

/**
 * Import entry screen (M4-08, docs/PRODUCT_SPEC.md §28). Paste a recipe URL and
 * choose Import or Cancel; a successful parse hands off to the editable review.
 * All import routes require `recipes.edit` server-side; the UI hides the flow
 * from actors who cannot use it.
 */
export function ImportPage() {
  const { canEdit } = useRecipeAccess()
  const parse = useParseImportUrl()

  const [url, setUrl] = useState('')
  const [urlError, setUrlError] = useState<string | null>(null)
  const [formError, setFormError] = useState<string | null>(null)
  const [result, setResult] = useState<ImportParseResponse | null>(null)
  const [selectedIndex, setSelectedIndex] = useState(0)
  const headingRef = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    headingRef.current?.focus()
  }, [])

  function handleCancel(): void {
    parse.reset()
    setResult(null)
    setSelectedIndex(0)
    setUrl('')
    setUrlError(null)
    setFormError(null)
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setUrlError(null)
    setFormError(null)

    const trimmed = url.trim()
    if (trimmed.length === 0) {
      setUrlError('Paste a recipe link to import.')
      return
    }
    try {
      const parsed = new URL(trimmed)
      if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
        setUrlError('Enter a full http:// or https:// URL.')
        return
      }
    } catch {
      setUrlError('Enter a full http:// or https:// URL.')
      return
    }

    try {
      const response = await parse.mutateAsync(trimmed)
      setResult(response)
      setSelectedIndex(response.recipes[0]?.index ?? 0)
    } catch (caught) {
      if (isApiError(caught)) {
        if (caught.code === ErrorCodes.Forbidden) {
          setFormError('You do not have permission to import recipes.')
          return
        }
        if (caught.code === ErrorCodes.ValidationFailed) {
          setUrlError('Enter a full http:// or https:// URL.')
          return
        }
        setFormError(describeImportError(caught.code, retryAfterSeconds(caught)))
        return
      }
      setFormError('Import failed. Check the connection and try again.')
    }
  }

  if (!canEdit) {
    return (
      <section className="page import-page" aria-labelledby="import-heading">
        <h2 id="import-heading" tabIndex={-1}>
          Import from URL
        </h2>
        <p className="form__error" role="alert">
          You do not have permission to import recipes. Ask an Admin for the recipes.edit
          permission.
        </p>
        <p>
          <Link to="/recipes">Back to recipes</Link>
        </p>
      </section>
    )
  }

  if (result !== null) {
    return (
      <ImportReview
        result={result}
        selectedIndex={selectedIndex}
        onSelectIndex={setSelectedIndex}
        onCancel={handleCancel}
      />
    )
  }

  const pending = parse.isPending

  return (
    <section className="page import-page" aria-labelledby="import-heading">
      <h2 id="import-heading" tabIndex={-1} ref={headingRef}>
        Import from URL
      </h2>

      <p className="page-note">
        Paste the address of a recipe page. The page is fetched and parsed, then you review and edit
        everything before saving. Nothing is saved until you confirm.
      </p>

      {formError !== null && (
        <p className="form__error" role="alert">
          {formError}
        </p>
      )}

      <form
        className="form import-page__form"
        onSubmit={(event) => void handleSubmit(event)}
        noValidate
      >
        <Field
          id="import-url"
          label="Recipe URL"
          hint="A full http:// or https:// address."
          error={urlError ?? undefined}
        >
          <input
            {...fieldProps('import-url', { error: urlError ?? undefined, hint: true })}
            type="url"
            inputMode="url"
            autoComplete="off"
            placeholder="https://example.com/recipe"
            value={url}
            disabled={pending}
            onChange={(event) => {
              setUrl(event.target.value)
              setUrlError(null)
              setFormError(null)
            }}
          />
        </Field>

        <div className="import-page__actions">
          <button type="button" onClick={handleCancel} disabled={pending}>
            Cancel
          </button>
          <button type="submit" className="button--primary" disabled={pending}>
            {pending ? 'Fetching…' : 'Import'}
          </button>
        </div>
      </form>

      {pending && (
        <p className="page-note" role="status">
          Fetching and parsing the recipe…
        </p>
      )}
    </section>
  )
}
