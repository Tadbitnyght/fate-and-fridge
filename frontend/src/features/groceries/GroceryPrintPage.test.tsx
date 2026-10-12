import { fireEvent, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { adminSession, memberSession } from '../../test/fixtures'
import { installGroceryMocks, type GroceryMockOptions } from '../../test/groceryMocks'
import { renderWithProviders } from '../../test/utils'

function renderPage(options: GroceryMockOptions = {}) {
  const mock = installGroceryMocks({ session: adminSession, ...options })
  const view = renderWithProviders(<App />, { route: '/groceries/print' })
  return { mock, view }
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('grocery print page', () => {
  it('renders the range heading, departments in order with Other last, and attribution', async () => {
    renderPage()

    const heading = await screen.findByRole('heading', {
      level: 2,
      name: /Grocery list: Oct 12 – Oct 18/,
    })
    expect(heading).toBeInTheDocument()
    expect(screen.getByText(/^Generated /)).toBeInTheDocument()

    const headings = screen.getAllByRole('heading', { level: 3 }).map((node) => node.textContent)
    expect(headings).toEqual(['Produce', 'Bakery', 'Other'])

    const produce = screen.getByRole('list', { name: 'Produce' })
    expect(within(produce).getByText('Onion')).toBeInTheDocument()
    expect(within(produce).getByText(/Tacos/)).toBeInTheDocument()
    expect(within(produce).getByText(/Tue 10\/13/)).toBeInTheDocument()
  })

  it('draws a decorative, screen-reader-hidden checkbox before each item', async () => {
    const { view } = renderPage()
    await screen.findByText('Onion')

    const boxes = view.container.querySelectorAll('.grocery-print__box')
    // Onion (Produce), Bread (Bakery), and the manual Paper towels (Other);
    // the checked Carrot is excluded by default.
    expect(boxes).toHaveLength(3)
    boxes.forEach((box) => expect(box).toHaveAttribute('aria-hidden', 'true'))

    // The printable checkbox is not an interactive control.
    expect(within(screen.getByRole('list', { name: 'Produce' })).queryByRole('checkbox')).toBeNull()
  })

  it('includes manual items marked with the icon+text Manual badge', async () => {
    renderPage()

    const other = await screen.findByRole('list', { name: 'Other' })
    expect(within(other).getByText('Paper towels')).toBeInTheDocument()
    expect(within(other).getByText('Manual')).toBeInTheDocument()
    expect(within(other).getByText('✎')).toBeInTheDocument()
  })

  it('excludes checked items by default and reveals them via the include toggle', async () => {
    renderPage()
    await screen.findByText('Onion')

    expect(screen.queryByText('Carrot')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Include checked items')).not.toBeChecked()

    fireEvent.click(screen.getByLabelText('Include checked items'))

    expect(await screen.findByText('Carrot')).toBeInTheDocument()
  })

  it('invokes window.print from the Print button', async () => {
    const printSpy = vi.spyOn(window, 'print').mockImplementation(() => undefined)
    renderPage()
    await screen.findByText('Onion')

    fireEvent.click(screen.getByRole('button', { name: 'Print' }))

    expect(printSpy).toHaveBeenCalledTimes(1)
  })

  it('shows a permission message without groceries.view', async () => {
    const { mock } = renderPage({ session: { ...memberSession, permissions: ['calendar.view'] } })

    expect(await screen.findByText(/do not have permission to view groceries/i)).toBeInTheDocument()
    // The list read is gated on canView, so no /groceries request is issued.
    expect(mock.callsFor('/groceries')).toBe(0)
  })

  it('shows an error state when the grocery list cannot be loaded', async () => {
    renderPage({
      listError: {
        status: 500,
        body: { error: { code: 'INTERNAL_ERROR', message: 'boom', details: {} } },
      },
    })

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Cannot load groceries' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('boom')
  })

  it('shows an empty state when the range has no items', async () => {
    renderPage({ departments: [] })

    expect(await screen.findByText(/No groceries for this range/)).toBeInTheDocument()
  })
})
