import { useLiveSync, type LiveSyncStatus as LiveSyncState } from './LiveSyncContext'

interface StatusPresentation {
  /** Visible text label (also the accessible name). */
  label: string
  /** Colour-independent glyph cue, hidden from assistive tech. */
  icon: string
  /** Tooltip/detail describing the state. */
  detail: string
}

/**
 * Presentation for each connection state. The label carries the meaning, so
 * the indicator never relies on colour alone (docs/M9_PLAN.md §1.9).
 */
const PRESENTATION: Record<LiveSyncState, StatusPresentation> = {
  connected: { label: 'Live', icon: '●', detail: 'Live updates are connected.' },
  connecting: { label: 'Connecting…', icon: '◌', detail: 'Connecting to live updates.' },
  reconnecting: { label: 'Reconnecting…', icon: '◌', detail: 'Reconnecting to live updates.' },
  disconnected: {
    label: 'Offline',
    icon: '○',
    detail: 'Live updates are offline; data stays usable.',
  },
}

/**
 * Accessible, non-blocking live-sync indicator (docs/M9_PLAN.md §1.9). It is
 * announced politely and never blocks the page: REST reads and writes keep
 * working while the socket is down.
 */
export function LiveSyncStatus() {
  const { status } = useLiveSync()
  const { label, icon, detail } = PRESENTATION[status]

  return (
    <span
      className={`live-sync-status live-sync-status--${status}`}
      aria-live="polite"
      aria-atomic="true"
      data-testid="live-sync-status"
      data-state={status}
      title={detail}
    >
      <span className="live-sync-status__icon" aria-hidden="true">
        {icon}
      </span>
      <span className="live-sync-status__label">{label}</span>
    </span>
  )
}
