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
      <p role="alert">{detail ?? 'Something went wrong while loading this page.'}</p>
      <p className="page-note">
        {onRetry
          ? 'Try again. If it keeps happening, check that Fate & Fridge is running and reachable.'
          : 'Refresh the page and try again.'}
      </p>
      {onRetry && (
        <button type="button" onClick={onRetry}>
          Try again
        </button>
      )}
    </div>
  )
}
