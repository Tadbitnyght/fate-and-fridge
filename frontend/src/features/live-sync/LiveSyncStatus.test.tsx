import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { LiveSyncContext, type LiveSyncStatus as LiveSyncState } from './LiveSyncContext'
import { LiveSyncStatus } from './LiveSyncStatus'

const CASES: Array<[LiveSyncState, string]> = [
  ['connected', 'Live'],
  ['connecting', 'Connecting…'],
  ['reconnecting', 'Reconnecting…'],
  ['disconnected', 'Offline'],
]

function renderStatus(status: LiveSyncState) {
  return render(
    <LiveSyncContext.Provider value={{ status }}>
      <LiveSyncStatus />
    </LiveSyncContext.Provider>,
  )
}

describe('LiveSyncStatus', () => {
  it.each(CASES)('renders the %s state as a polite live region', (status, label) => {
    renderStatus(status)

    const indicator = screen.getByTestId('live-sync-status')
    expect(indicator).toHaveAttribute('aria-live', 'polite')
    expect(indicator).toHaveAttribute('data-state', status)
    expect(indicator).toHaveTextContent(label)
    // The state never relies on colour alone: an aria-hidden glyph is present.
    expect(indicator.querySelector('[aria-hidden="true"]')).not.toBeNull()
  })

  it('is non-blocking: no interactive role and no focusable content', () => {
    renderStatus('connected')

    expect(screen.queryByRole('button')).not.toBeInTheDocument()
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
    expect(screen.getByTestId('live-sync-status')).not.toHaveAttribute('tabindex')
  })
})
