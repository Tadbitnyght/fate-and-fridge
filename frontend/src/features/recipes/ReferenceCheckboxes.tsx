/** Checkbox multi-select for recipe proteins and tags (M3-10, issue #37). */

export interface ReferenceCheckboxOption {
  id: number
  name: string
  isActive: boolean
}

export function ReferenceCheckboxes({
  legend,
  idPrefix,
  options,
  selected,
  onChange,
  error,
}: {
  legend: string
  idPrefix: string
  options: ReferenceCheckboxOption[]
  selected: number[]
  onChange: (ids: number[]) => void
  error?: string
}) {
  function toggle(id: number, checked: boolean): void {
    onChange(checked ? [...selected, id] : selected.filter((candidate) => candidate !== id))
  }

  return (
    <fieldset className="form__fieldset" aria-describedby={error ? `${idPrefix}-error` : undefined}>
      <legend>{legend}</legend>
      {options.length === 0 ? (
        <p className="form__hint">No options yet. Add them under Settings → Reference Data.</p>
      ) : (
        <ul className="reference-checkboxes">
          {options.map((option) => (
            <li key={option.id}>
              <label className="recipes__checkbox">
                <input
                  type="checkbox"
                  checked={selected.includes(option.id)}
                  onChange={(event) => toggle(option.id, event.target.checked)}
                />
                {option.name}
                {!option.isActive && <span className="recipe-muted"> (retired)</span>}
              </label>
            </li>
          ))}
        </ul>
      )}
      {error && (
        <p className="form__error" id={`${idPrefix}-error`} role="alert">
          {error}
        </p>
      )}
    </fieldset>
  )
}
