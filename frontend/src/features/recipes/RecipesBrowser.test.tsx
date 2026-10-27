import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { RecipeListItem, RecipeListResponse } from '../../api/recipes'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

// Vitest does not inject imported stylesheets, so the stretched-link contract is
// read straight from the source stylesheet (BUG-006).
const indexCss = readFileSync(resolve(process.cwd(), 'src/index.css'), 'utf8')

function listItem(
  overrides: Partial<RecipeListItem> & Pick<RecipeListItem, 'id' | 'name'>,
): RecipeListItem {
  return {
    status: 'normal',
    is_favorite: false,
    is_vegan: false,
    is_vegetarian: false,
    image: null,
    last_eaten: null,
    next_planned: null,
    times_eaten: null,
    created_at: '2026-10-01T12:00:00Z',
    updated_at: '2026-10-01T12:00:00Z',
    ...overrides,
  }
}

const alpha = listItem({
  id: 1,
  name: 'Alpha Chicken',
  status: 'normal',
  is_favorite: true,
  image: {
    id: 1,
    alt_text: 'A roast chicken',
    source_url: null,
    created_at: '2026-10-01T12:00:00Z',
    url: '/api/v1/recipes/1/image',
  },
})
const beta = listItem({ id: 2, name: 'Beta Beef', status: 'manual_only' })
const retired = listItem({ id: 3, name: 'Gamma Retired', status: 'retired', is_vegan: true })
const logged = listItem({
  id: 4,
  name: 'Delta Stew',
  status: 'retired',
  last_eaten: '2026-09-30',
  next_planned: '2026-10-20',
  times_eaten: 3,
})

const proteins = [
  { id: 5, name: 'Chicken', is_active: true },
  { id: 6, name: 'Beef', is_active: true },
]
const tags = [
  { id: 7, name: 'Dinner' },
  { id: 8, name: 'Quick' },
]

function baseMocks(routes: MockRoute[], session: unknown = adminSession): MockFetch {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: session },
    { path: '/health/db', body: { status: 'ok', database: 'connected' } },
    {
      path: '/health',
      body: { status: 'ok', app: 'Fate & Fridge', version: '0.1.0', environment: 'test' },
    },
    { path: '/proteins?', body: proteins },
    { path: '/tags?', body: tags },
    ...routes,
  ])
}

function listResponse(items: RecipeListItem[], total = items.length): RecipeListResponse {
  return { items, total, limit: 24, offset: 0 }
}

function requestsFor(mock: MockFetch, path: string): string[] {
  return mock.fetchMock.mock.calls
    .filter(([url]) => String(url).includes(path))
    .map(([url]) => String(url))
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('recipe browser', () => {
  it('renders the photo-card grid with text statuses and empty history states', async () => {
    baseMocks([{ path: '/recipes?', method: 'GET', body: listResponse([alpha, beta, retired]) }])

    renderWithProviders(<App />, { route: '/recipes' })

    expect(await screen.findByRole('heading', { level: 2, name: 'Recipes' })).toBeInTheDocument()
    expect(await screen.findByRole('link', { name: 'Alpha Chicken' })).toHaveAttribute(
      'href',
      '/recipes/1/edit',
    )
    const betaCard = screen.getByRole('link', { name: 'Beta Beef' }).closest('li')
    expect(betaCard).not.toBeNull()
    expect(within(betaCard as HTMLElement).getByText('Manual only')).toBeInTheDocument()
    const retiredCard = screen.getByRole('link', { name: 'Gamma Retired' }).closest('li')
    expect(retiredCard).not.toBeNull()
    expect(within(retiredCard as HTMLElement).getByText('Retired')).toBeInTheDocument()
    expect(screen.getAllByText('No history yet').length).toBeGreaterThanOrEqual(3)
    expect(screen.getByAltText('A roast chicken')).toHaveAttribute('src', '/api/v1/recipes/1/image')
    expect(screen.getByRole('status')).toHaveTextContent('3 recipes')
    expect(screen.getByRole('link', { name: 'Add recipe' })).toHaveAttribute('href', '/recipes/new')
  })

  it('applies search, status, and protein filters through the server list API', async () => {
    const mock = baseMocks([
      { path: '/recipes?', method: 'GET', body: listResponse([alpha, beta, retired]) },
    ])

    renderWithProviders(<App />, { route: '/recipes' })
    await screen.findByRole('link', { name: 'Alpha Chicken' })

    const search = screen.getByLabelText('Search recipes')
    fireEvent.change(search, { target: { value: 'chicken' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))
    await waitFor(() =>
      expect(requestsFor(mock, '/recipes?').some((url) => url.includes('search=chicken'))).toBe(
        true,
      ),
    )

    fireEvent.change(screen.getByLabelText('Status'), { target: { value: 'retired' } })
    await waitFor(() =>
      expect(requestsFor(mock, '/recipes?').some((url) => url.includes('status=retired'))).toBe(
        true,
      ),
    )

    fireEvent.change(screen.getByLabelText('Primary protein'), { target: { value: '5' } })
    await waitFor(() =>
      expect(requestsFor(mock, '/recipes?').some((url) => url.includes('protein_id=5'))).toBe(true),
    )
  })

  it('toggles to a compact list view', async () => {
    baseMocks([{ path: '/recipes?', method: 'GET', body: listResponse([alpha, beta]) }])

    renderWithProviders(<App />, { route: '/recipes' })
    await screen.findByRole('link', { name: 'Alpha Chicken' })

    fireEvent.click(screen.getByRole('button', { name: 'List' }))

    const table = screen.getByRole('table', { name: 'Recipes' })
    expect(within(table).getByRole('rowheader', { name: 'Beta Beef' })).toBeInTheDocument()
  })

  it('shows the three per-recipe metrics on cards with a no-history fallback', async () => {
    baseMocks([{ path: '/recipes?', method: 'GET', body: listResponse([alpha, logged]) }])

    renderWithProviders(<App />, { route: '/recipes' })
    await screen.findByRole('link', { name: 'Alpha Chicken' })

    // A retired recipe still displays its full history, with its status badge.
    const loggedCard = screen.getByRole('link', { name: 'Delta Stew' }).closest('li')
    expect(loggedCard).not.toBeNull()
    const loggedScope = within(loggedCard as HTMLElement)
    expect(loggedScope.getByText('Retired')).toBeInTheDocument()
    expect(loggedScope.getByText('Last eaten')).toBeInTheDocument()
    expect(loggedScope.getByText('09-30-26')).toBeInTheDocument()
    expect(loggedScope.getByText('Next planned')).toBeInTheDocument()
    expect(loggedScope.getByText('10-20-26')).toBeInTheDocument()
    expect(loggedScope.getByText('Times eaten')).toBeInTheDocument()
    expect(loggedScope.getByText('3')).toBeInTheDocument()

    // A recipe with no history falls back for every metric.
    const emptyCard = screen.getByRole('link', { name: 'Alpha Chicken' }).closest('li')
    expect(emptyCard).not.toBeNull()
    expect(within(emptyCard as HTMLElement).getAllByText('No history yet')).toHaveLength(3)
  })

  it('shows the metrics as columns in the compact list view, including Next planned', async () => {
    baseMocks([{ path: '/recipes?', method: 'GET', body: listResponse([alpha, logged]) }])

    renderWithProviders(<App />, { route: '/recipes' })
    await screen.findByRole('link', { name: 'Alpha Chicken' })
    fireEvent.click(screen.getByRole('button', { name: 'List' }))

    const table = screen.getByRole('table', { name: 'Recipes' })
    expect(within(table).getByRole('columnheader', { name: 'Next planned' })).toBeInTheDocument()

    const loggedRow = within(table)
      .getByRole('rowheader', { name: 'Delta Stew' })
      .closest('tr') as HTMLElement
    expect(within(loggedRow).getByText('Retired')).toBeInTheDocument()
    expect(within(loggedRow).getByText('09-30-26')).toBeInTheDocument()
    expect(within(loggedRow).getByText('10-20-26')).toBeInTheDocument()
    expect(within(loggedRow).getByText('3')).toBeInTheDocument()

    const emptyRow = within(table)
      .getByRole('rowheader', { name: /Alpha Chicken/ })
      .closest('tr') as HTMLElement
    expect(within(emptyRow).getAllByText('No history yet')).toHaveLength(3)
  })

  it('shows the empty state and hides write affordances from view-only members', async () => {
    baseMocks([{ path: '/recipes?', method: 'GET', body: listResponse([]) }], memberSession)

    renderWithProviders(<App />, { route: '/recipes' })

    expect(
      await screen.findByText('No recipes yet. Add the first one to start planning dinners.'),
    ).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Add recipe' })).not.toBeInTheDocument()
  })

  it('makes the whole card open the editor while keeping exactly one link', async () => {
    baseMocks([{ path: '/recipes?', method: 'GET', body: listResponse([alpha, beta]) }])

    renderWithProviders(<App />, { route: '/recipes' })

    const card = (await screen.findByRole('link', { name: 'Alpha Chicken' })).closest('li')
    expect(card).not.toBeNull()
    const scope = within(card as HTMLElement)

    // Exactly one accessible link (the name), and no nested interactive content.
    const links = scope.getAllByRole('link')
    expect(links).toHaveLength(1)
    expect(links[0]).toHaveAttribute('href', '/recipes/1/edit')
    expect(links[0]).toHaveClass('recipe-card__name-link')
    expect(scope.queryAllByRole('button')).toHaveLength(0)
    expect(scope.queryAllByRole('link')).toHaveLength(1)

    // The stretched-link pattern: the card is the positioning context and the
    // name link's ::after covers it (the real pointer behaviour is asserted in
    // the e2e card test).
    expect(card).toHaveClass('recipe-card')
    expect(indexCss).toMatch(/\.recipe-card\s*\{[^}]*position:\s*relative/)
    expect(indexCss).toMatch(/\.recipe-card__name-link::after\s*\{[^}]*inset:\s*0/)
    expect(indexCss).toMatch(/\.recipe-card__name-link::after\s*\{[^}]*z-index:\s*1/)
  })

  it('falls back to the no-photo placeholder when an image fails to load', async () => {
    baseMocks([{ path: '/recipes?', method: 'GET', body: listResponse([alpha]) }])

    renderWithProviders(<App />, { route: '/recipes' })

    const image = await screen.findByAltText('A roast chicken')
    fireEvent.error(image)

    expect(await screen.findByText('No photo')).toBeInTheDocument()
    expect(screen.queryByAltText('A roast chicken')).not.toBeInTheDocument()
  })
})
