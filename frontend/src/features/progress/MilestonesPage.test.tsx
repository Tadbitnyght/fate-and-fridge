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
    expect(screen.getByText('4 of 15 milestones complete (27%).')).toBeInTheDocument()

    const progressbar = screen.getByRole('progressbar', { name: 'Milestones complete' })
    expect(progressbar).toHaveAttribute('aria-valuenow', '27')
    expect(progressbar).toHaveAttribute('aria-valuetext', '27%')

    expect(screen.getAllByText('Complete', { exact: true })).toHaveLength(4)
    expect(screen.getAllByText('Planned', { exact: true }).length).toBeGreaterThan(9)

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
})
