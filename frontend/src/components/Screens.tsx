import type { ReactNode } from 'react'

export function LoadingScreen({ label = 'Loading…' }: { label?: string }) {
  return (
    <div className="screen" role="status">
      <p>{label}</p>
    </div>
  )
}

export function ErrorScreen({
  title,
  error,
  onRetry,
}: {
  title: string
  error?: unknown
  onRetry?: () => void
}) {
  const detail = error instanceof Error ? error.message : null
  return (
    <div className="screen">
      <h1>{title}</h1>
      <p role="alert">{detail ?? 'An unexpected error occurred.'}</p>
      {onRetry && (
        <button type="button" onClick={onRetry}>
          Try again
        </button>
      )}
    </div>
  )
}

export function PagePlaceholder({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <section className="page" aria-labelledby={`page-${title.toLowerCase().replace(/\s+/g, '-')}`}>
      <h2 id={`page-${title.toLowerCase().replace(/\s+/g, '-')}`} tabIndex={-1}>
        {title}
      </h2>
      <p className="page-note">
        This area is planned for a later milestone and is not implemented yet.
      </p>
      {children}
    </section>
  )
}
