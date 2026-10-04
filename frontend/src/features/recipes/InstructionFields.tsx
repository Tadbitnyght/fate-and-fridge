import { fieldProps } from '../../components/fieldProps'
import { emptyInstructionSection, type InstructionSectionFormRow } from './recipeForm'

function replaceAt<T>(items: T[], index: number, value: T): T[] {
  return items.map((item, current) => (current === index ? value : item))
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

/**
 * Ordered instruction sections and steps (M3-10). Sections and steps may be
 * moved, and blank steps are dropped when the request is built.
 */
export function InstructionFields({
  sections,
  onChange,
}: {
  sections: InstructionSectionFormRow[]
  onChange: (sections: InstructionSectionFormRow[]) => void
}) {
  return (
    <fieldset className="form__fieldset">
      <legend>Instructions</legend>
      {sections.length === 0 ? (
        <p className="form__hint">No instruction sections yet.</p>
      ) : (
        <ol className="section-list">
          {sections.map((section, sectionIndex) => (
            <li key={section.key} className="section-card">
              <div className="section-card__head">
                <div className="form__field">
                  <label htmlFor={`${section.key}-title`}>Section {sectionIndex + 1} title</label>
                  <input
                    id={`${section.key}-title`}
                    type="text"
                    autoComplete="off"
                    placeholder="e.g. Prep (optional)"
                    value={section.title}
                    onChange={(event) =>
                      onChange(
                        replaceAt(sections, sectionIndex, {
                          ...section,
                          title: event.target.value,
                        }),
                      )
                    }
                  />
                </div>
                <div className="section-card__actions">
                  <button
                    type="button"
                    aria-label={`Move section ${sectionIndex + 1} up`}
                    disabled={sectionIndex === 0}
                    onClick={() => onChange(moveItem(sections, sectionIndex, -1))}
                  >
                    Move up
                  </button>
                  <button
                    type="button"
                    aria-label={`Move section ${sectionIndex + 1} down`}
                    disabled={sectionIndex === sections.length - 1}
                    onClick={() => onChange(moveItem(sections, sectionIndex, 1))}
                  >
                    Move down
                  </button>
                  <button
                    type="button"
                    aria-label={`Remove section ${sectionIndex + 1}`}
                    onClick={() =>
                      onChange(sections.filter((_, current) => current !== sectionIndex))
                    }
                  >
                    Remove section
                  </button>
                </div>
              </div>

              <ol className="step-list">
                {section.steps.map((step, stepIndex) => {
                  const stepId = `${section.key}-step-${stepIndex}`
                  return (
                    <li key={stepId} className="step-row">
                      <div className="form__field">
                        <label htmlFor={stepId}>Step {stepIndex + 1}</label>
                        <textarea
                          {...fieldProps(stepId)}
                          rows={2}
                          value={step}
                          onChange={(event) =>
                            onChange(
                              replaceAt(sections, sectionIndex, {
                                ...section,
                                steps: replaceAt(section.steps, stepIndex, event.target.value),
                              }),
                            )
                          }
                        />
                      </div>
                      <button
                        type="button"
                        aria-label={`Remove step ${stepIndex + 1}`}
                        onClick={() =>
                          onChange(
                            replaceAt(sections, sectionIndex, {
                              ...section,
                              steps: section.steps.filter((_, current) => current !== stepIndex),
                            }),
                          )
                        }
                      >
                        Remove step
                      </button>
                    </li>
                  )
                })}
              </ol>

              <button
                type="button"
                onClick={() =>
                  onChange(
                    replaceAt(sections, sectionIndex, {
                      ...section,
                      steps: [...section.steps, ''],
                    }),
                  )
                }
              >
                Add step
              </button>
            </li>
          ))}
        </ol>
      )}
      <button type="button" onClick={() => onChange([...sections, emptyInstructionSection()])}>
        Add section
      </button>
    </fieldset>
  )
}
