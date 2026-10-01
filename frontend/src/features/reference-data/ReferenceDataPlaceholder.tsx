import { useId } from 'react'

/**
 * Placeholder for reference-data tabs owned by later stages (units,
 * ingredients, proteins, tags). Stages B/C replace these with real tabs.
 */
export function ReferenceDataPlaceholder({
  title,
  description,
}: {
  title: string
  description: string
}) {
  const headingId = useId()
  return (
    <section className="settings-subsection" aria-labelledby={headingId}>
      <h4 id={headingId}>{title}</h4>
      <p className="page-note">Coming in this milestone. {description}</p>
    </section>
  )
}
