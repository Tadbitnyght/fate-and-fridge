import { fireEvent, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { installMockFetch, type MockFetch } from '../../test/mockFetch'
import { adminSession, setupCompleteStatus } from '../../test/fixtures'
import { renderWithProviders } from '../../test/utils'

function installMocks(): MockFetch {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: adminSession },
    { path: '/health/db', body: { status: 'ok', database: 'connected' } },
    {
      path: '/health',
      body: { status: 'ok', app: 'Fate & Fridge', version: '0.1.0', environment: 'test' },
    },
  ])
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('progress page', () => {
  it('renders the summary, statuses, and roadmap entries from the manifest', async () => {
    installMocks()

    renderWithProviders(<App />, { route: '/progress' })

    expect(await screen.findByRole('heading', { level: 2, name: 'Progress' })).toBeInTheDocument()
    expect(screen.getByText('11 of 15 milestones complete (73%).')).toBeInTheDocument()

    const progressbar = screen.getByRole('progressbar', { name: 'Milestones complete' })
    expect(progressbar).toHaveAttribute('aria-valuenow', '73')
    expect(progressbar).toHaveAttribute('aria-valuetext', '73%')

    expect(screen.getAllByText('Complete', { exact: true })).toHaveLength(11)
    // Each of the four planned milestones renders "Planned" twice: the status
    // chip and the dates line. Every milestone through M10 is complete.
    expect(screen.getAllByText('Planned', { exact: true })).toHaveLength(8)

    const m1Card = screen
      .getByRole('heading', { level: 3, name: 'M1 — Setup and Authentication' })
      .closest('li')!
    expect(within(m1Card).getByText(/Completed 2026-09-30/)).toBeInTheDocument()
    // The meta span nests inside the dates paragraph, so both match.
    expect(within(m1Card).getAllByText(/m1-complete/).length).toBeGreaterThan(0)
    expect(within(m1Card).getAllByText(/f0c5dc7/).length).toBeGreaterThan(0)

    const m2Card = screen
      .getByRole('heading', { level: 3, name: 'M2 — Reference Data' })
      .closest('li')!
    expect(within(m2Card).getByText('Complete', { exact: true })).toBeInTheDocument()
    expect(within(m2Card).getByText(/Completed 2026-10-01/)).toBeInTheDocument()
    expect(within(m2Card).getByText(/m2-complete/)).toBeInTheDocument()
    expect(within(m2Card).getByText(/3e25a24/)).toBeInTheDocument()
    expect(within(m2Card).getByText('independent review + full battery')).toBeInTheDocument()

    const m4Card = screen.getByRole('heading', { level: 3, name: 'M4 — URL Import' }).closest('li')!
    expect(within(m4Card).getByText('Complete', { exact: true })).toBeInTheDocument()
    expect(within(m4Card).getByText(/Completed 2026-10-05/)).toBeInTheDocument()
    expect(within(m4Card).getByText(/m4-complete/)).toBeInTheDocument()

    const m5Card = screen
      .getByRole('heading', { level: 3, name: 'M5 — Calendar Core' })
      .closest('li')!
    expect(within(m5Card).getByText('Complete', { exact: true })).toBeInTheDocument()
    expect(within(m5Card).getByText(/Completed 2026-10-06/)).toBeInTheDocument()
    expect(within(m5Card).getByText(/m5-complete/)).toBeInTheDocument()

    const m6Card = screen
      .getByRole('heading', { level: 3, name: 'M6 — Randomization Engine' })
      .closest('li')!
    expect(within(m6Card).getByText('Complete', { exact: true })).toBeInTheDocument()
    expect(within(m6Card).getByText(/Completed 2026-10-08/)).toBeInTheDocument()
    expect(within(m6Card).getByText(/m6-complete/)).toBeInTheDocument()

    const m7Card = screen
      .getByRole('heading', { level: 3, name: 'M7 — Batch Planning' })
      .closest('li')!
    expect(within(m7Card).getByText('Complete', { exact: true })).toBeInTheDocument()
    expect(within(m7Card).getByText(/Completed 2026-10-09/)).toBeInTheDocument()
    expect(within(m7Card).getAllByText(/m7-complete/).length).toBeGreaterThan(0)

    const m8Card = screen.getByRole('heading', { level: 3, name: 'M8 — Groceries' }).closest('li')!
    expect(within(m8Card).getByText('Complete', { exact: true })).toBeInTheDocument()
    expect(within(m8Card).getByText(/Completed 2026-10-12/)).toBeInTheDocument()
    // The tag is cut after the live redeploy is verified.
    expect(within(m8Card).getAllByText(/m8-complete/).length).toBeGreaterThan(0)

    const m9Card = screen.getByRole('heading', { level: 3, name: 'M9 — Live Sync' }).closest('li')!
    expect(within(m9Card).getByText('Complete', { exact: true })).toBeInTheDocument()
    expect(within(m9Card).getByText(/Completed 2026-10-13/)).toBeInTheDocument()
    // The tag is cut after the live redeploy is verified.
    expect(within(m9Card).getAllByText(/m9-complete/).length).toBeGreaterThan(0)

    const m10Card = screen.getByRole('heading', { level: 3, name: 'M10 — Printing' }).closest('li')!
    expect(within(m10Card).getByText('Complete', { exact: true })).toBeInTheDocument()
    expect(within(m10Card).getByText(/Completed 2026-10-14/)).toBeInTheDocument()
    // The tag and commit are recorded only after the live redeploy is verified.
    expect(within(m10Card).queryByText(/m10-complete/)).not.toBeInTheDocument()

    expect(screen.getByRole('heading', { level: 3, name: 'M14 — Hardening' })).toBeInTheDocument()
  })

  it('shows test evidence chips and GitHub issue links without network calls', async () => {
    const mock = installMocks()

    renderWithProviders(<App />, { route: '/progress' })
    await screen.findByRole('heading', { level: 2, name: 'Progress' })

    const m1Card = screen
      .getByRole('heading', { level: 3, name: 'M1 — Setup and Authentication' })
      .closest('li')!
    const evidence = within(m1Card).getByText('Backend tests').closest('li')!
    expect(within(evidence).getByText('643')).toBeInTheDocument()
    expect(within(m1Card).getByText('Vitest').closest('li')).toHaveTextContent('94')

    const issueLink = screen.getAllByRole('link', { name: '#11' })[0]
    expect(issueLink).toHaveAttribute(
      'href',
      'https://github.com/Tadbitnyght/fate-and-fridge/issues/11',
    )
    expect(issueLink).toHaveAttribute('target', '_blank')
    expect(issueLink).toHaveAttribute('rel', 'noreferrer')

    const requestedUrls = mock.fetchMock.mock.calls.map(([url]) => String(url))
    expect(requestedUrls.some((url) => url.includes('github.com'))).toBe(false)
    expect(requestedUrls.every((url) => url.startsWith('/api/'))).toBe(true)
  })

  it('is reachable from the Dashboard card and the footer', async () => {
    installMocks()

    renderWithProviders(<App />, { route: '/dashboard' })

    expect(await screen.findByRole('heading', { level: 2, name: 'Dashboard' })).toBeInTheDocument()

    const cardLink = screen.getByRole('link', { name: 'Open the milestone roadmap' })
    expect(cardLink).toHaveAttribute('href', '/progress')
    expect(screen.getByRole('link', { name: 'Milestone progress' })).toHaveAttribute(
      'href',
      '/progress',
    )

    fireEvent.click(cardLink)
    expect(await screen.findByRole('heading', { level: 2, name: 'Progress' })).toBeInTheDocument()
  })

  it('defaults to the Milestones tab and switches to Future & ideas on click', async () => {
    installMocks()

    renderWithProviders(<App />, { route: '/progress' })
    await screen.findByRole('heading', { level: 2, name: 'Progress' })

    const milestonesTab = screen.getByRole('tab', { name: 'Milestones' })
    const futureTab = screen.getByRole('tab', { name: 'Future & ideas' })

    // The milestone roadmap is the default view and occupies the single tab stop.
    expect(milestonesTab).toHaveAttribute('aria-selected', 'true')
    expect(milestonesTab).toHaveAttribute('tabindex', '0')
    expect(futureTab).toHaveAttribute('aria-selected', 'false')
    expect(futureTab).toHaveAttribute('tabindex', '-1')
    expect(screen.getByText('11 of 15 milestones complete (73%).')).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { level: 4, name: 'Pantry / inventory' }),
    ).not.toBeInTheDocument()

    fireEvent.click(futureTab)

    expect(futureTab).toHaveAttribute('aria-selected', 'true')
    expect(milestonesTab).toHaveAttribute('aria-selected', 'false')
    expect(
      screen.getByRole('heading', { level: 4, name: 'Pantry / inventory' }),
    ).toBeInTheDocument()
    expect(screen.getByText(/approved or scheduled/i)).toBeInTheDocument()
    // The registry-backed panel groups items and keeps V2 first.
    const v2Heading = screen.getByRole('heading', { level: 3, name: /V2/ })
    expect(v2Heading).toBeInTheDocument()
    // The milestone roadmap is unmounted while the future tab is active.
    expect(
      screen.queryByRole('heading', { level: 3, name: 'M1 — Setup and Authentication' }),
    ).not.toBeInTheDocument()
    expect(screen.queryByText('11 of 15 milestones complete (73%).')).not.toBeInTheDocument()
  })

  it('supports Left/Right/Home/End keyboard navigation with roving focus', async () => {
    installMocks()

    renderWithProviders(<App />, { route: '/progress' })
    await screen.findByRole('heading', { level: 2, name: 'Progress' })

    const milestonesTab = screen.getByRole('tab', { name: 'Milestones' })
    const futureTab = screen.getByRole('tab', { name: 'Future & ideas' })

    milestonesTab.focus()
    fireEvent.keyDown(milestonesTab, { key: 'ArrowRight' })
    expect(futureTab).toHaveAttribute('aria-selected', 'true')
    expect(document.activeElement).toBe(futureTab)

    fireEvent.keyDown(futureTab, { key: 'ArrowLeft' })
    expect(milestonesTab).toHaveAttribute('aria-selected', 'true')
    expect(document.activeElement).toBe(milestonesTab)

    // Arrows wrap around the ends of the tablist.
    fireEvent.keyDown(milestonesTab, { key: 'ArrowLeft' })
    expect(futureTab).toHaveAttribute('aria-selected', 'true')

    fireEvent.keyDown(futureTab, { key: 'Home' })
    expect(milestonesTab).toHaveAttribute('aria-selected', 'true')
    expect(document.activeElement).toBe(milestonesTab)

    fireEvent.keyDown(milestonesTab, { key: 'End' })
    expect(futureTab).toHaveAttribute('aria-selected', 'true')
    expect(document.activeElement).toBe(futureTab)
  })
})
