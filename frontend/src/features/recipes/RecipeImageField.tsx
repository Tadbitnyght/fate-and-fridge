import { useEffect, useId, useRef, useState } from 'react'

import { isApiError } from '../../api/client'
import type { RecipeImage } from '../../api/recipes'
import {
  useDeleteRecipeImage,
  useUpdateRecipeImageAltText,
  useUploadRecipeImage,
} from './useRecipes'

const ACCEPTED_TYPES = ['image/jpeg', 'image/png', 'image/webp']
const MAX_BYTES = 5 * 1024 * 1024

function describeError(error: unknown): string {
  if (isApiError(error)) {
    const fileErrors = error.details['field_errors']
    if (typeof fileErrors === 'object' && fileErrors !== null) {
      const messages = (fileErrors as Record<string, unknown>)['file']
      if (Array.isArray(messages) && typeof messages[0] === 'string') {
        return messages[0]
      }
    }
    return error.message
  }
  return 'The photo could not be saved. Check the connection and try again.'
}

/**
 * One active local recipe photo per recipe (M3 §1.7): upload, replace, remove,
 * alt text, and a preview. The server re-validates the file signature, size,
 * and dimensions — the client checks are only a courtesy.
 */
export function RecipeImageField({
  recipeId,
  image,
}: {
  recipeId: number
  image: RecipeImage | null
}) {
  const inputId = useId()
  const upload = useUploadRecipeImage()
  const saveAltText = useUpdateRecipeImageAltText()
  const removeImage = useDeleteRecipeImage()

  const [file, setFile] = useState<File | null>(null)
  const [previewUrl, setPreviewUrl] = useState<string | null>(null)
  const [altText, setAltText] = useState(image?.alt_text ?? '')
  const [syncedImageKey, setSyncedImageKey] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const previewRef = useRef<string | null>(null)
  const pending = upload.isPending || saveAltText.isPending || removeImage.isPending

  // Sync the alt-text field when the active image changes. Adjusting state
  // during render is React's documented pattern for this and avoids a
  // cascading effect render.
  const imageKey = image === null ? null : `${image.id}:${image.alt_text ?? ''}`
  if (imageKey !== syncedImageKey) {
    setSyncedImageKey(imageKey)
    setAltText(image?.alt_text ?? '')
  }

  useEffect(() => {
    return () => {
      if (previewRef.current !== null) {
        URL.revokeObjectURL(previewRef.current)
      }
    }
  }, [])

  function releasePreview(): void {
    if (previewRef.current !== null) {
      URL.revokeObjectURL(previewRef.current)
      previewRef.current = null
    }
    setPreviewUrl(null)
  }

  function handleFileChange(selected: File | null): void {
    setError(null)
    setNotice(null)
    releasePreview()
    if (selected === null) {
      setFile(null)
      return
    }
    if (!ACCEPTED_TYPES.includes(selected.type)) {
      setFile(null)
      setError('Choose a JPEG, PNG, or WebP image.')
      return
    }
    if (selected.size > MAX_BYTES) {
      setFile(null)
      setError('Images must be 5 MB or smaller.')
      return
    }
    setFile(selected)
    if (typeof URL.createObjectURL === 'function') {
      const url = URL.createObjectURL(selected)
      previewRef.current = url
      setPreviewUrl(url)
    }
  }

  async function handleSave(): Promise<void> {
    setError(null)
    setNotice(null)
    try {
      if (file !== null) {
        await upload.mutateAsync({ id: recipeId, file, altText })
        setFile(null)
        releasePreview()
        setNotice('Photo saved.')
        return
      }
      await saveAltText.mutateAsync({
        id: recipeId,
        altText: altText.trim() === '' ? null : altText,
      })
      setNotice('Alt text saved.')
    } catch (caught) {
      setError(describeError(caught))
    }
  }

  async function handleRemove(): Promise<void> {
    setError(null)
    setNotice(null)
    try {
      await removeImage.mutateAsync(recipeId)
      setFile(null)
      releasePreview()
      setAltText('')
      setNotice('Photo removed.')
    } catch (caught) {
      setError(describeError(caught))
    }
  }

  const displayUrl = previewUrl ?? image?.url ?? null
  const hasPhoto = displayUrl !== null
  const canSave = file !== null || (image !== null && altText !== (image.alt_text ?? ''))

  return (
    <fieldset className="form__fieldset">
      <legend>Photo</legend>
      {error && (
        <p className="form__error" role="alert">
          {error}
        </p>
      )}
      {notice && (
        <p className="form__notice" role="status">
          {notice}
        </p>
      )}

      {hasPhoto ? (
        <img className="recipe-photo-preview" src={displayUrl} alt={altText} />
      ) : (
        <p className="recipe-muted">No photo yet.</p>
      )}

      <div className="form__field">
        <label htmlFor={inputId}>{image === null ? 'Upload photo' : 'Replace photo'}</label>
        <input
          id={inputId}
          type="file"
          accept={ACCEPTED_TYPES.join(',')}
          onChange={(event) => handleFileChange(event.target.files?.[0] ?? null)}
        />
        <p className="form__hint">JPEG, PNG, or WebP, up to 5 MB.</p>
      </div>

      <div className="form__field">
        <label htmlFor={`${inputId}-alt`}>Photo description (alt text)</label>
        <input
          id={`${inputId}-alt`}
          type="text"
          autoComplete="off"
          value={altText}
          onChange={(event) => {
            setAltText(event.target.value)
            setError(null)
            setNotice(null)
          }}
        />
      </div>

      <div className="recipe-photo-actions">
        <button
          type="button"
          className="button--primary"
          disabled={pending || !canSave}
          onClick={() => void handleSave()}
        >
          {upload.isPending ? 'Uploading…' : file !== null ? 'Upload photo' : 'Save alt text'}
        </button>
        {image !== null && (
          <button type="button" disabled={pending} onClick={() => void handleRemove()}>
            Remove photo
          </button>
        )}
      </div>
    </fieldset>
  )
}
