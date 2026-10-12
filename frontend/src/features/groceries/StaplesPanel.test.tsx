import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { GroceryRange, GroceryStaple } from '../../api/groceries'
import type { Department, Ingredient, Unit } from '../../api/referenceData'
import { installGroceryMocks } from '../../test/groceryMocks'
import { renderWithProviders } from '../../test/utils'
import { StaplesPanel } from './StaplesPanel'

const range: GroceryRange = {
  preset: 'next_7_days',
  start: '2026-10-12',
  end: '2026-10-18',
  day_count: 7,
}

const departments: Department[] = [
  { id: 1, name: 'Produce', sort_order: 1, is_active: true, created_at: '', updated_at: '' },
]
const units: Unit[] = [
  {
    id: 7,
    symbol: 'lb',
    display_name: 'Pound',
    dimension: 'mass',
    base_factor: 453.592,
    is_custom: false,
    is_active: true,
    aliases: [],
  },
]
const ingredients: Ingredient[] = [
  {
    id: 42,
    canonical_name: 'Milk',
    department_id: 1,
    is_active: true,
    created_at: '',
    updated_at: '',
    aliases: [],
  },
]

function staple(overrides: Partial<GroceryStaple> & { id: number; name: string }): GroceryStaple {
  return {
    ingredient_id: null,
    department_id: null,
    default_quantity_numeric: null,
    unit_id: null,
    is_active: true,
    created_at: '2026-10-10T00:00:00+00:00',
    updated_at: '2026-10-10T00:00:00+00:00',
    ...overrides,
  }
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function renderPanel(
  options: { canEdit?: boolean; staples?: GroceryStaple[]; range?: GroceryRange | null } = {},
) {
  const mock = installGroceryMocks({
    session: null,
    staples: options.staples ?? [staple({ id: 1, name: 'Eggs' }), staple({ id: 2, name: 'Milk' })],
  })
  const onAnnounce = vi.fn()
  renderWithProviders(
    <StaplesPanel
      canEdit={options.canEdit ?? true}
      range={options.range === undefined ? range : options.range}
      departments={departments}
      units={units}
      ingredients={ingredients}
      onAnnounce={onAnnounce}
    />,
  )
  return { mock, onAnnounce }
}

describe('staples panel', () => {
  it('lists active staples in server order and adds one to the current range', async () => {
    const { mock, onAnnounce } = renderPanel()

    const list = await screen.findByRole('list', { name: 'Staples' })
    const rows = within(list).getAllByRole('listitem')
    expect(rows.map((row) => within(row).getByText(/Eggs|Milk/).textContent)).toEqual([
      'Eggs',
      'Milk',
    ])

    fireEvent.click(within(rows[0]).getByRole('button', { name: 'Add to list' }))

    await waitFor(() => {
      const post = mock.state.requests.find(
        (request) => request.method === 'POST' && request.path === '/groceries/staples/add-to-list',
      )
      expect(post?.body).toMatchObject({ range_start: '2026-10-12', range_days: 7 })
    })
    expect(onAnnounce).toHaveBeenCalledWith(expect.stringContaining('Eggs'))
  })

  it('creates a staple and shows a duplicate name error inline', async () => {
    const { mock } = renderPanel()

    fireEvent.click(await screen.findByRole('button', { name: 'New staple' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add staple' })

    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Eggs' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add staple' }))

    expect(await within(dialog).findByText(/already exists/i)).toBeInTheDocument()

    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Butter' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add staple' }))

    await waitFor(() => {
      const posts = mock.state.requests.filter(
        (request) => request.method === 'POST' && request.path === '/groceries/staples',
      )
      expect(posts.some((post) => post.body?.['name'] === 'Butter')).toBe(true)
    })
  })

  it('hides staple writes when the user cannot edit', async () => {
    renderPanel({ canEdit: false })

    const addButtons = await screen.findAllByRole('button', { name: 'Add to list' })
    expect(addButtons.length).toBeGreaterThan(0)
    for (const button of addButtons) {
      expect(button).toBeDisabled()
    }
    expect(screen.queryByRole('button', { name: 'New staple' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit Eggs' })).not.toBeInTheDocument()
  })

  it('disables add-to-list until a range is known', async () => {
    renderPanel({ range: null })

    const addButtons = await screen.findAllByRole('button', { name: 'Add to list' })
    for (const button of addButtons) {
      expect(button).toBeDisabled()
    }
  })

  it('edits a staple and persists the changed fields', async () => {
    const { mock, onAnnounce } = renderPanel()

    fireEvent.click(await screen.findByRole('button', { name: 'Edit Eggs' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit Eggs' })
    fireEvent.change(within(dialog).getByLabelText('Default quantity (optional)'), {
      target: { value: '12' },
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
        (request) => request.method === 'PATCH' && request.path === '/groceries/staples',
      )
      expect(patch?.body).toMatchObject({
        default_quantity_numeric: 12,
        unit_id: 7,
        ingredient_id: 42,
        department_id: 1,
        is_active: true,
      })
    })
    expect(await screen.findByText('12 lb')).toBeInTheDocument()
    expect(onAnnounce).toHaveBeenCalledWith(expect.stringContaining('Updated staple Eggs'))
  })

  it('confirms before retiring a staple and announces it', async () => {
    const { mock, onAnnounce } = renderPanel()

    fireEvent.click(await screen.findByRole('button', { name: 'Edit Eggs' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit Eggs' })
    fireEvent.click(within(dialog).getByLabelText('Active'))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    const confirm = await screen.findByRole('dialog', { name: 'Retire Eggs?' })
    expect(screen.queryByRole('dialog', { name: 'Edit Eggs' })).not.toBeInTheDocument()
    fireEvent.click(within(confirm).getByRole('button', { name: 'Retire staple' }))

    await waitFor(() => {
      const patch = mock.state.requests.find(
        (request) => request.method === 'PATCH' && request.path === '/groceries/staples',
      )
      expect(patch?.body).toMatchObject({ is_active: false })
    })
    expect(onAnnounce).toHaveBeenCalledWith(expect.stringContaining('Retired staple Eggs'))
    await waitFor(() => expect(screen.queryByText('Eggs')).not.toBeInTheDocument())
  })

  it('shows a visible error when retiring a staple fails', async () => {
    const { mock } = renderPanel()

    fireEvent.click(await screen.findByRole('button', { name: 'Edit Eggs' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit Eggs' })
    fireEvent.click(within(dialog).getByLabelText('Active'))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    const confirm = await screen.findByRole('dialog', { name: 'Retire Eggs?' })
    mock.fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))
    fireEvent.click(within(confirm).getByRole('button', { name: 'Retire staple' }))

    expect(await within(confirm).findByText(/retiring the staple failed/i)).toBeInTheDocument()
    // The staple is still listed because the write failed.
    expect(await screen.findByRole('button', { name: 'Edit Eggs' })).toBeInTheDocument()
  })
})
