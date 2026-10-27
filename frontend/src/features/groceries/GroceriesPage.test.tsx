import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { GroceryDepartment, GroceryPreferences } from '../../api/groceries'
import { adminSession, memberSession } from '../../test/fixtures'
import {
  groceryItem,
  hexIdentity,
  installGroceryMocks,
  type GroceryMockOptions,
} from '../../test/groceryMocks'
import { renderWithProviders } from '../../test/utils'

function renderPage(options: GroceryMockOptions = {}) {
  const mock = installGroceryMocks({ session: adminSession, ...options })
  renderWithProviders(<App />, { route: '/groceries' })
  return mock
}

function hiddenPreferences(): GroceryPreferences {
  return { hide_checked: true, range_preset: 'next_7_days', range_start: null, range_days: null }
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('groceries page', () => {
  it('loads the remembered range with no query and groups departments with Other last', async () => {
    const mock = renderPage()

    expect(await screen.findByRole('heading', { level: 2, name: 'Groceries' })).toBeInTheDocument()
    await screen.findByText('Onion')

    const firstListCall = mock.fetchMock.mock.calls.find(([input]) =>
      String(input).endsWith('/api/v1/groceries'),
    )
    expect(firstListCall).toBeDefined()
    expect(String(firstListCall![0])).not.toContain('?')

    const headings = screen.getAllByRole('heading', { level: 3 }).map((node) => node.textContent)
    expect(headings).toEqual(['Produce', 'Bakery', 'Other'])

    const produce = screen.getByRole('list', { name: 'Produce' })
    expect(within(produce).getByText('Onion')).toBeInTheDocument()
    expect(within(produce).getByText(/Tacos/)).toBeInTheDocument()
  })

  it('checks an item, persists it, and moves it below the unchecked items', async () => {
    const departments: GroceryDepartment[] = [
      {
        department_id: 1,
        name: 'Produce',
        sort_order: 1,
        is_other: false,
        items: [
          groceryItem({ identity: hexIdentity('a'), name: 'Apple', department_id: 1 }),
          groceryItem({ identity: hexIdentity('b'), name: 'Banana', department_id: 1 }),
        ],
      },
    ]
    const mock = renderPage({ departments })
    await screen.findByText('Apple')

    fireEvent.click(screen.getByLabelText('Mark Apple as bought'))

    await waitFor(() => expect(mock.callsFor('/groceries/check')).toBe(1))
    await waitFor(() => {
      const labels = within(screen.getByRole('list', { name: 'Produce' }))
        .getAllByRole('checkbox')
        .map((box) => box.getAttribute('aria-label'))
      expect(labels).toEqual(['Mark Banana as bought', 'Mark Apple as not bought'])
    })
  })

  it('toggles hide-checked and persists it via preferences', async () => {
    const mock = renderPage()
    await screen.findByText('Carrot')

    fireEvent.click(screen.getByLabelText('Hide checked items'))

    await waitFor(() => expect(screen.queryByText('Carrot')).not.toBeInTheDocument())
    const patch = mock.state.requests.find(
      (request) => request.method === 'PATCH' && request.path === '/groceries/preferences',
    )
    expect(patch?.body).toMatchObject({ hide_checked: true })
  })

  it('respects the stored hide-checked preference on load', async () => {
    renderPage({ preferences: hiddenPreferences() })
    await screen.findByText('Onion')

    expect(screen.getByLabelText('Hide checked items')).toBeChecked()
    expect(screen.queryByText('Carrot')).not.toBeInTheDocument()
  })

  it('adds a manual item and validates the required name', async () => {
    const mock = renderPage()
    await screen.findByText('Onion')

    fireEvent.click(screen.getByRole('button', { name: 'Add grocery item' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add grocery item' })

    fireEvent.click(within(dialog).getByRole('button', { name: 'Add item' }))
    expect(await within(dialog).findByText('Enter an item name.')).toBeInTheDocument()

    fireEvent.change(within(dialog).getByLabelText('Name'), {
      target: { value: 'Dish soap' },
    })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add item' }))

    await waitFor(() => {
      const post = mock.state.requests.find(
        (request) => request.method === 'POST' && request.path === '/groceries/manual',
      )
      expect(post?.body).toMatchObject({
        name: 'Dish soap',
        range_start: '2026-10-12',
        range_days: 7,
      })
    })
    expect(await screen.findByText('Dish soap')).toBeInTheDocument()
  })

  it('edits an existing manual item', async () => {
    const mock = renderPage()
    await screen.findByText('Paper towels')

    fireEvent.click(screen.getByRole('button', { name: 'Edit Paper towels' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit Paper towels' })
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Kitchen roll' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => {
      const patch = mock.state.requests.find(
        (request) => request.method === 'PATCH' && request.path === '/groceries/manual',
      )
      expect(patch?.body).toMatchObject({ name: 'Kitchen roll' })
    })
    expect(await screen.findByText('Kitchen roll')).toBeInTheDocument()
  })

  it('confirms before deleting a manual item', async () => {
    const mock = renderPage()
    await screen.findByText('Paper towels')

    fireEvent.click(screen.getByRole('button', { name: 'Delete Paper towels' }))
    const dialog = await screen.findByRole('dialog', { name: 'Delete Paper towels?' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete item' }))

    await waitFor(() => expect(mock.callsFor('/groceries/manual/')).toBe(1))
    await waitFor(() => expect(screen.queryByText('Paper towels')).not.toBeInTheDocument())
  })

  it('disables every write for a member without groceries.edit', async () => {
    renderPage({ session: memberSession })
    await screen.findByText('Onion')

    expect(screen.queryByRole('button', { name: 'Add grocery item' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Mark Onion as bought')).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Edit Paper towels' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Staples' }))
    expect(await screen.findByRole('button', { name: 'Add to list' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Add staple' })).not.toBeInTheDocument()
  })

  it('shows a permission message when the member lacks groceries.view', async () => {
    renderPage({
      session: { ...memberSession, permissions: ['calendar.view'] },
    })
    expect(await screen.findByText(/do not have permission to view groceries/i)).toBeInTheDocument()
  })

  it('persists a preset range selection and refetches the list', async () => {
    const mock = renderPage()
    await screen.findByText('Onion')
    const listCallsBefore = mock.callsFor('/groceries')

    fireEvent.click(screen.getByRole('button', { name: 'Next 3 days' }))

    await waitFor(() => {
      const patch = mock.state.requests.find(
        (request) => request.method === 'PATCH' && request.path === '/groceries/preferences',
      )
      expect(patch?.body).toMatchObject({
        range_preset: 'next_3_days',
        range_start: null,
        range_days: null,
      })
    })
    await waitFor(() => expect(mock.callsFor('/groceries')).toBeGreaterThan(listCallsBefore))
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Next 3 days' })).toHaveAttribute(
        'aria-pressed',
        'true',
      ),
    )
  })

  it('persists a custom range', async () => {
    const mock = renderPage()
    await screen.findByText('Onion')

    fireEvent.click(screen.getByRole('button', { name: 'Custom' }))
    fireEvent.change(await screen.findByLabelText('Start date'), {
      target: { value: '2026-11-01' },
    })
    fireEvent.change(screen.getByLabelText('Days'), { target: { value: '5' } })
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }))

    await waitFor(() => {
      const patch = mock.state.requests.find(
        (request) => request.method === 'PATCH' && request.path === '/groceries/preferences',
      )
      expect(patch?.body).toMatchObject({
        range_preset: 'custom',
        range_start: '2026-11-01',
        range_days: 5,
      })
    })
  })

  it('shows a clear message when the range change is rejected', async () => {
    renderPage({
      preferencesPatchError: {
        status: 400,
        body: {
          error: { code: 'GROCERY_RANGE_TOO_LARGE', message: 'too large', details: {} },
        },
      },
    })
    await screen.findByText('Onion')

    fireEvent.click(screen.getByRole('button', { name: 'Next 14 days' }))

    expect(await screen.findByText(/too long/i)).toBeInTheDocument()
  })

  it('shows an error when a check is rejected', async () => {
    renderPage({
      checkError: {
        status: 403,
        body: { error: { code: 'FORBIDDEN', message: 'no', details: {} } },
      },
    })
    await screen.findByText('Onion')

    fireEvent.click(screen.getByLabelText('Mark Onion as bought'))

    expect(await screen.findByText(/do not have permission/i)).toBeInTheDocument()
  })

  it('shows a visible error when the hide-checked preference fails to persist', async () => {
    const mock = renderPage()
    await screen.findByText('Carrot')

    mock.fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))
    fireEvent.click(screen.getByLabelText('Hide checked items'))

    expect(await screen.findByText(/hide-checked preference failed/i)).toBeInTheDocument()
    // The toggle reverts to the stored preference rather than lying about state.
    await waitFor(() => expect(screen.getByLabelText('Hide checked items')).not.toBeChecked())
  })

  it('surfaces a clear error when deleting a manual item returns 404 NOT_FOUND', async () => {
    renderPage({
      manualDeleteError: {
        status: 404,
        body: { error: { code: 'NOT_FOUND', message: 'gone', details: {} } },
      },
    })
    await screen.findByText('Paper towels')

    fireEvent.click(screen.getByRole('button', { name: 'Delete Paper towels' }))
    const dialog = await screen.findByRole('dialog', { name: 'Delete Paper towels?' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete item' }))

    expect(await within(dialog).findByText(/no longer exists/i)).toBeInTheDocument()
    expect(screen.getByText('Paper towels')).toBeInTheDocument()
  })

  it('edits quantity, unit, ingredient, and department of a manual item', async () => {
    const mock = renderPage()
    await screen.findByText('Paper towels')

    fireEvent.click(screen.getByRole('button', { name: 'Edit Paper towels' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit Paper towels' })
    fireEvent.change(within(dialog).getByLabelText('Quantity (optional)'), {
      target: { value: '3' },
    })
    fireEvent.change(within(dialog).getByLabelText('Unit (optional)'), { target: { value: '7' } })
    fireEvent.change(within(dialog).getByLabelText('Ingredient (optional)'), {
      target: { value: '42' },
    })
    fireEvent.change(within(dialog).getByLabelText('Department (optional)'), {
      target: { value: '1' },
    })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => {
      const patch = mock.state.requests.find(
        (request) => request.method === 'PATCH' && request.path === '/groceries/manual',
      )
      expect(patch?.body).toMatchObject({
        quantity_numeric: 3,
        unit_id: 7,
        ingredient_id: 42,
        department_id: 1,
      })
    })

    // The edited item now renders under Produce with the new quantity and unit.
    const produce = await screen.findByRole('list', { name: 'Produce' })
    const row = within(produce).getByText('Paper towels').closest('li')
    expect(row).not.toBeNull()
    expect(within(row as HTMLElement).getByText('3 lb')).toBeInTheDocument()
  })

  it('rejects a non-positive manual quantity inline without calling the API', async () => {
    const mock = renderPage()
    await screen.findByText('Onion')

    fireEvent.click(screen.getByRole('button', { name: 'Add grocery item' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add grocery item' })
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Dish soap' } })
    fireEvent.change(within(dialog).getByLabelText('Quantity (optional)'), {
      target: { value: '0' },
    })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add item' }))

    expect(await within(dialog).findByText(/greater than 0/i)).toBeInTheDocument()
    expect(
      mock.state.requests.some(
        (request) => request.method === 'POST' && request.path === '/groceries/manual',
      ),
    ).toBe(false)
  })

  it('accepts a custom range of exactly 366 days', async () => {
    const mock = renderPage()
    await screen.findByText('Onion')

    fireEvent.click(screen.getByRole('button', { name: 'Custom' }))
    fireEvent.change(await screen.findByLabelText('Start date'), {
      target: { value: '2026-11-01' },
    })
    fireEvent.change(screen.getByLabelText('Days'), { target: { value: '366' } })
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }))

    await waitFor(() => {
      const patch = mock.state.requests.find(
        (request) => request.method === 'PATCH' && request.path === '/groceries/preferences',
      )
      expect(patch?.body).toMatchObject({
        range_preset: 'custom',
        range_start: '2026-11-01',
        range_days: 366,
      })
    })
    expect(screen.queryByText(/too long/i)).not.toBeInTheDocument()
  })

  it('rejects a custom range longer than 366 days', async () => {
    renderPage()
    await screen.findByText('Onion')

    fireEvent.click(screen.getByRole('button', { name: 'Custom' }))
    fireEvent.change(await screen.findByLabelText('Start date'), {
      target: { value: '2026-11-01' },
    })
    fireEvent.change(screen.getByLabelText('Days'), { target: { value: '367' } })
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }))

    expect(await screen.findByText(/too long/i)).toBeInTheDocument()
  })

  it('links to the print view once the range has items', async () => {
    renderPage()
    await screen.findByText('Onion')

    expect(screen.getByRole('link', { name: 'Print' })).toHaveAttribute('href', '/groceries/print')
  })

  it('hides the print link when the range is empty', async () => {
    renderPage({ departments: [] })
    expect(await screen.findByText(/No groceries for this range/)).toBeInTheDocument()

    expect(screen.queryByRole('link', { name: 'Print' })).not.toBeInTheDocument()
  })
})
