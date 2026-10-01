import { screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { Department, Unit } from '../../api/referenceData'
import {
  adminSession,
  memberSession,
  setupCompleteStatus,
  singleUserSession,
} from '../../test/fixtures'
import { installMockFetch } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

const produce: Department = {
  id: 10,
  name: 'Produce',
  sort_order: 1,
  is_active: true,
  created_at: '2026-09-01T10:00:00Z',
  updated_at: '2026-09-01T10:00:00Z',
}

const gram: Unit = {
  id: 10,
  symbol: 'g',
  display_name: 'gram',
  dimension: 'mass',
  base_factor: 1,
  is_custom: false,
  is_active: true,
  aliases: [{ id: 1, alias: 'gram' }],
}

/** Member with `reference_data.edit` but no Admin role: no hard deletes. */
const editorSession = {
  ...memberSession,
  permissions: [...memberSession.permissions, 'reference_data.edit'],
}

function installShellMocks(session: unknown, departments: Department[] = [produce]) {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: session },
    { path: '/departments', body: departments },
  ])
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('reference data shell', () => {
  it('renders the section tabs and routes the index to Departments', async () => {
    installShellMocks(adminSession)

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    const tabs = await screen.findByRole('navigation', { name: 'Reference data sections' })
    for (const label of ['Departments', 'Units', 'Ingredients', 'Proteins', 'Tags']) {
      expect(within(tabs).getByRole('link', { name: label })).toBeInTheDocument()
    }
    expect(within(tabs).getByRole('link', { name: 'Departments' })).toHaveAttribute(
      'aria-current',
      'page',
    )
    expect(await screen.findByRole('heading', { name: 'Departments' })).toBeInTheDocument()
    expect(await screen.findByRole('table', { name: 'Grocery departments' })).toBeInTheDocument()
  })

  it('routes the Units tab to the units table instead of a placeholder', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/units', body: [gram] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/units' })

    expect(await screen.findByRole('heading', { name: 'Units' })).toBeInTheDocument()
    const table = await screen.findByRole('table', { name: 'Units of measure' })
    expect(within(table).getByRole('rowheader', { name: 'g' })).toBeInTheDocument()
    expect(screen.queryByText(/coming in this milestone/i)).not.toBeInTheDocument()
    expect(screen.getByRole('navigation', { name: 'Reference data sections' })).toBeInTheDocument()
  })

  it.each([
    { route: '/settings/reference-data/ingredients', title: 'Ingredients' },
    { route: '/settings/reference-data/proteins', title: 'Proteins' },
    { route: '/settings/reference-data/tags', title: 'Tags' },
  ])('renders the $title placeholder for later stages', async ({ route, title }) => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
    ])

    renderWithProviders(<App />, { route })

    expect(await screen.findByRole('heading', { name: title })).toBeInTheDocument()
    expect(screen.getByText(/coming in this milestone/i)).toBeInTheDocument()
    expect(screen.getByRole('navigation', { name: 'Reference data sections' })).toBeInTheDocument()
  })

  it('shows a read-only notice and no write controls without reference_data.edit', async () => {
    installShellMocks(memberSession)

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    expect(await screen.findByText(/read-only access to reference data/i)).toBeInTheDocument()
    expect(await screen.findByRole('table', { name: 'Grocery departments' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add department' })).not.toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Rename Produce' })).not.toBeInTheDocument()
  })

  it('gives Admins the write controls, including the Admin-only delete', async () => {
    installShellMocks(adminSession)

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    expect(await screen.findByRole('button', { name: 'Add department' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Rename Produce' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retire Produce' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Delete Produce' })).toBeInTheDocument()
    expect(screen.queryByText(/read-only access/i)).not.toBeInTheDocument()
  })

  it('gives the single-user system actor the write controls', async () => {
    installShellMocks(singleUserSession)

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    expect(await screen.findByRole('button', { name: 'Add department' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Delete Produce' })).toBeInTheDocument()
  })

  it('shows edit controls but no delete to a non-Admin editor', async () => {
    installShellMocks(editorSession)

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    expect(await screen.findByRole('button', { name: 'Add department' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Rename Produce' })).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: 'Actions' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete Produce' })).not.toBeInTheDocument()
  })
})
