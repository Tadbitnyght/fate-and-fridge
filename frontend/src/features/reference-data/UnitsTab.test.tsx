import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { Unit, UnitDimension } from '../../api/referenceData'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

function unit(
  overrides: Partial<Unit> & Pick<Unit, 'id' | 'symbol' | 'display_name' | 'dimension'>,
): Unit {
  return {
    base_factor: null,
    is_custom: false,
    is_active: true,
    aliases: [],
    ...overrides,
  }
}

const gram = unit({
  id: 10,
  symbol: 'g',
  display_name: 'gram',
  dimension: 'mass',
  base_factor: 1,
  aliases: [{ id: 1, alias: 'grams' }],
})
const dozen = unit({
  id: 11,
  symbol: 'dozen',
  display_name: 'dozen',
  dimension: 'count',
  aliases: [{ id: 2, alias: 'dozens' }],
})
const pinch = unit({
  id: 12,
  symbol: 'pinch',
  display_name: 'pinch',
  dimension: 'nonconvertible',
  aliases: [{ id: 3, alias: 'pinches' }],
})
const kilogram = unit({
  id: 13,
  symbol: 'kg',
  display_name: 'kilogram',
  dimension: 'mass',
  base_factor: 1000,
  is_active: false,
  aliases: [{ id: 4, alias: 'kilograms' }],
})

/** Member with `reference_data.edit` but no Admin role: no hard deletes. */
const editorSession = {
  ...memberSession,
  permissions: [...memberSession.permissions, 'reference_data.edit'],
}

function installAdminMocks(routes: MockRoute[]): MockFetch {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: adminSession },
    ...routes,
  ])
}

/** Recorded requests whose URL contains `path`, mirroring the departments tests. */
function requestsFor(mock: MockFetch, path: string): Array<{ method: string; body?: unknown }> {
  return mock.fetchMock.mock.calls
    .filter(([url]) => String(url).includes(path))
    .map(([, init]) => {
      const request = init as RequestInit | undefined
      const method = (request?.method ?? 'GET').toUpperCase()
      return request?.body === undefined
        ? { method }
        : { method, body: JSON.parse(String(request.body)) as unknown }
    })
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('units tab', () => {
  it('lists active units with dimensions, factors, and aliases, and reveals retired rows', async () => {
    const mock = installAdminMocks([{ path: '/units', body: [gram, dozen, pinch, kilogram] }])

    renderWithProviders(<App />, { route: '/settings/reference-data/units' })

    const table = await screen.findByRole('table', { name: 'Units of measure' })

    const gramRow = within(table).getByRole('rowheader', { name: 'g' }).closest('tr')
    expect(gramRow).not.toBeNull()
    expect(within(gramRow as HTMLElement).getByText('gram')).toBeInTheDocument()
    expect(within(gramRow as HTMLElement).getByText('Mass')).toBeInTheDocument()
    expect(within(gramRow as HTMLElement).getByText('1')).toBeInTheDocument()
    expect(within(gramRow as HTMLElement).getByText('grams')).toBeInTheDocument()
    expect(within(gramRow as HTMLElement).getByText('Active')).toBeInTheDocument()

    const dozenRow = within(table).getByRole('rowheader', { name: 'dozen' }).closest('tr')
    expect(dozenRow).not.toBeNull()
    expect(within(dozenRow as HTMLElement).getByText('Count')).toBeInTheDocument()
    expect(within(dozenRow as HTMLElement).getByText('—')).toBeInTheDocument()

    const pinchRow = within(table).getByRole('rowheader', { name: 'pinch' }).closest('tr')
    expect(pinchRow).not.toBeNull()
    expect(within(pinchRow as HTMLElement).getByText('Non-convertible')).toBeInTheDocument()
    expect(within(pinchRow as HTMLElement).getByText('—')).toBeInTheDocument()

    expect(within(table).queryByRole('rowheader', { name: 'kg' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByLabelText('Include retired'))

    const kgHeader = await screen.findByRole('rowheader', { name: 'kg' })
    const kgRow = kgHeader.closest('tr')
    expect(kgRow).not.toBeNull()
    expect(within(kgRow as HTMLElement).getByText('Retired')).toBeInTheDocument()
    expect(within(kgRow as HTMLElement).getByText('1000')).toBeInTheDocument()

    const listCalls = mock.fetchMock.mock.calls.filter(([url]) => String(url).includes('/units'))
    expect(String(listCalls[listCalls.length - 1]?.[0])).toContain('include_retired=true')
  })

  const createCases: Array<{
    dimension: UnitDimension
    symbol: string
    displayName: string
    factor: string | null
    expected: Record<string, unknown>
  }> = [
    {
      dimension: 'mass',
      symbol: 'kg',
      displayName: 'kilogram',
      factor: '1000',
      expected: {
        symbol: 'kg',
        display_name: 'kilogram',
        dimension: 'mass',
        base_factor: 1000,
      },
    },
    {
      dimension: 'volume',
      symbol: 'tsp',
      displayName: 'teaspoon',
      factor: '4.92892159375',
      expected: {
        symbol: 'tsp',
        display_name: 'teaspoon',
        dimension: 'volume',
        base_factor: 4.92892159375,
      },
    },
    {
      dimension: 'count',
      symbol: 'bunch',
      displayName: 'bunch',
      factor: null,
      expected: { symbol: 'bunch', display_name: 'bunch', dimension: 'count' },
    },
    {
      dimension: 'nonconvertible',
      symbol: 'to taste',
      displayName: 'to taste',
      factor: null,
      expected: { symbol: 'to taste', display_name: 'to taste', dimension: 'nonconvertible' },
    },
  ]

  it.each(createCases)(
    'creates a $dimension unit with the right payload',
    async ({ dimension, symbol, displayName, factor, expected }) => {
      const mock = installAdminMocks([
        { path: '/units', body: [gram] },
        {
          path: '/units',
          method: 'POST',
          status: 201,
          body: unit({ id: 20, symbol, display_name: displayName, dimension }),
        },
      ])

      renderWithProviders(<App />, { route: '/settings/reference-data/units' })

      fireEvent.click(await screen.findByRole('button', { name: 'Add unit' }))
      const dialog = await screen.findByRole('dialog', { name: 'Add unit' })
      fireEvent.change(within(dialog).getByLabelText('Symbol'), { target: { value: symbol } })
      fireEvent.change(within(dialog).getByLabelText('Display name'), {
        target: { value: displayName },
      })
      fireEvent.change(within(dialog).getByLabelText('Dimension'), {
        target: { value: dimension },
      })

      if (factor === null) {
        expect(within(dialog).queryByLabelText('Base factor')).not.toBeInTheDocument()
      } else {
        fireEvent.change(within(dialog).getByLabelText('Base factor'), {
          target: { value: factor },
        })
      }

      fireEvent.click(within(dialog).getByRole('button', { name: 'Create unit' }))

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect(requestsFor(mock, '/units')).toContainEqual({ method: 'POST', body: expected })
    },
  )

  it('validates the factor locally and surfaces server field errors', async () => {
    const mock = installAdminMocks([
      { path: '/units', body: [gram] },
      {
        path: '/units/10',
        method: 'PATCH',
        status: 400,
        body: {
          error: {
            code: 'VALIDATION_FAILED',
            message: 'The request body failed validation.',
            details: {
              field_errors: {
                base_factor: ['Base factor must be a positive number within the allowed range.'],
              },
            },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/units' })

    fireEvent.click(await screen.findByRole('button', { name: 'Edit factor for g' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit base factor for g' })
    const input = within(dialog).getByLabelText('Base factor')
    expect(input).toHaveValue('1')

    fireEvent.change(input, { target: { value: '' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save factor' }))
    expect(
      await within(dialog).findByText('Enter a base factor for mass and volume units.'),
    ).toBeInTheDocument()
    expect(requestsFor(mock, '/units/10').filter((request) => request.method === 'PATCH')).toEqual(
      [],
    )

    fireEvent.change(input, { target: { value: '0' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save factor' }))
    expect(
      await within(dialog).findByText('Base factor must be between 0.000001 and 1000000.'),
    ).toBeInTheDocument()

    fireEvent.change(input, { target: { value: '2' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save factor' }))

    expect(
      await within(dialog).findByText(
        'Base factor must be a positive number within the allowed range.',
      ),
    ).toBeInTheDocument()
    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(requestsFor(mock, '/units/10')).toContainEqual({
      method: 'PATCH',
      body: { base_factor: 2 },
    })
    expect(screen.getByRole('dialog', { name: 'Edit base factor for g' })).toBeInTheDocument()
  })

  it('adds and removes aliases with the expected payloads', async () => {
    const mock = installAdminMocks([
      { path: '/units', body: [gram] },
      { path: '/units/10/aliases', method: 'POST', status: 201, body: { id: 5, alias: 'kilo' } },
      { path: '/units/10/aliases/1', method: 'DELETE', status: 204 },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/units' })

    const input = await screen.findByLabelText('New alias for g')
    fireEvent.change(input, { target: { value: '  kilo  ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add alias to g' }))

    await waitFor(() =>
      expect(requestsFor(mock, '/units/10/aliases')).toContainEqual({
        method: 'POST',
        body: { alias: 'kilo' },
      }),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Remove alias grams from g' }))

    await waitFor(() =>
      expect(requestsFor(mock, '/units/10/aliases/1')).toContainEqual({ method: 'DELETE' }),
    )
  })

  it('keeps the alias form open and shows the ALIAS_EXISTS message', async () => {
    installAdminMocks([
      { path: '/units', body: [gram] },
      {
        path: '/units/10/aliases',
        method: 'POST',
        status: 409,
        body: {
          error: {
            code: 'ALIAS_EXISTS',
            message: 'The alias is already used on this unit.',
            details: { existing_id: 10 },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/units' })

    const input = await screen.findByLabelText('New alias for g')
    fireEvent.change(input, { target: { value: 'grams' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add alias to g' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'The alias is already used on this unit.',
    )
    expect(input).toHaveValue('grams')
  })

  it('shows the UNIT_EXISTS message with the existing unit id on create', async () => {
    installAdminMocks([
      { path: '/units', body: [gram] },
      {
        path: '/units',
        method: 'POST',
        status: 409,
        body: {
          error: {
            code: 'UNIT_EXISTS',
            message: 'A unit with this symbol or display name already exists.',
            details: { existing_id: 10 },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/units' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add unit' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add unit' })
    fireEvent.change(within(dialog).getByLabelText('Symbol'), { target: { value: 'g' } })
    fireEvent.change(within(dialog).getByLabelText('Display name'), { target: { value: 'gram' } })
    fireEvent.change(within(dialog).getByLabelText('Base factor'), { target: { value: '1' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create unit' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'A unit with this symbol or display name already exists (existing unit #10).',
    )
    expect(screen.getByRole('dialog', { name: 'Add unit' })).toBeInTheDocument()
  })

  it('keeps the delete dialog open with the UNIT_IN_USE usage count', async () => {
    installAdminMocks([
      { path: '/units', body: [gram] },
      {
        path: '/units/10',
        method: 'DELETE',
        status: 409,
        body: {
          error: {
            code: 'UNIT_IN_USE',
            message: 'The unit is still referenced by 2 recipe ingredient(s); retire it instead.',
            details: { usage_count: 2 },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/units' })

    fireEvent.click(await screen.findByRole('button', { name: 'Delete g' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Delete unit' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      '2 recipe ingredients use this unit. Retire it instead, or update those recipes first.',
    )
    expect(screen.getByRole('dialog', { name: 'Delete g?' })).toBeInTheDocument()
  })

  it('retires an active unit and offers no re-activation for retired rows', async () => {
    const mock = installAdminMocks([
      { path: '/units', body: [gram, kilogram] },
      {
        path: '/units/10/retire',
        method: 'POST',
        body: unit({
          id: 10,
          symbol: 'g',
          display_name: 'gram',
          dimension: 'mass',
          is_active: false,
        }),
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/units' })

    fireEvent.click(await screen.findByRole('button', { name: 'Retire g' }))
    await waitFor(() =>
      expect(requestsFor(mock, '/units/10/retire')).toContainEqual({ method: 'POST' }),
    )

    fireEvent.click(screen.getByLabelText('Include retired'))
    expect(await screen.findByRole('button', { name: 'Delete kg' })).toBeInTheDocument()

    // The M2 units API has no un-retire endpoint (docs/API.md §22), so retired
    // rows must not offer an Enable action that would fail server-side.
    expect(screen.queryByRole('button', { name: 'Retire kg' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Enable kg' })).not.toBeInTheDocument()
  })

  it('hides the Admin-only delete from a non-Admin editor', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: editorSession },
      { path: '/units', body: [gram] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/units' })

    expect(await screen.findByRole('button', { name: 'Add unit' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Edit g' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retire g' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete g' })).not.toBeInTheDocument()
  })
})
