import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { Modal } from './Modal'

function Harness() {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button type="button" onClick={() => setOpen(true)}>
        Open dialog
      </button>
      {open && (
        <Modal title="Test dialog" onClose={() => setOpen(false)}>
          <button type="button">First action</button>
          <button type="button">Last action</button>
        </Modal>
      )}
    </>
  )
}

describe('Modal', () => {
  it('moves focus into the dialog and marks the background inert', async () => {
    const { container } = render(<Harness />)

    fireEvent.click(screen.getByRole('button', { name: 'Open dialog' }))

    const dialog = await screen.findByRole('dialog', { name: 'Test dialog' })
    expect(document.activeElement).toBe(dialog)
    await waitFor(() => expect(container).toHaveAttribute('inert'))
  })

  it('wraps Tab and Shift+Tab within the dialog', async () => {
    render(<Harness />)
    fireEvent.click(screen.getByRole('button', { name: 'Open dialog' }))

    const dialog = await screen.findByRole('dialog', { name: 'Test dialog' })
    const first = within(dialog).getByRole('button', { name: 'First action' })
    const last = within(dialog).getByRole('button', { name: 'Last action' })

    first.focus()
    fireEvent.keyDown(document, { key: 'Tab', shiftKey: true })
    expect(document.activeElement).toBe(last)

    fireEvent.keyDown(document, { key: 'Tab' })
    expect(document.activeElement).toBe(first)
  })

  it('closes on Escape, restores the background, and returns focus to the trigger', async () => {
    const { container } = render(<Harness />)
    const trigger = screen.getByRole('button', { name: 'Open dialog' })
    trigger.focus()
    fireEvent.click(trigger)
    await screen.findByRole('dialog', { name: 'Test dialog' })

    fireEvent.keyDown(document, { key: 'Escape' })

    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Test dialog' })).not.toBeInTheDocument(),
    )
    expect(container).not.toHaveAttribute('inert')
    await waitFor(() => expect(document.activeElement).toBe(trigger))
  })

  it('keeps a click on the backdrop closing and a click inside the dialog open', async () => {
    render(<Harness />)
    fireEvent.click(screen.getByRole('button', { name: 'Open dialog' }))
    const dialog = await screen.findByRole('dialog', { name: 'Test dialog' })

    fireEvent.mouseDown(dialog)
    expect(screen.getByRole('dialog', { name: 'Test dialog' })).toBeInTheDocument()

    fireEvent.mouseDown(dialog.parentElement!)
    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Test dialog' })).not.toBeInTheDocument(),
    )
  })

  it('does nothing when no focusable element exists besides the panel', async () => {
    const onClose = vi.fn()
    render(
      <Modal title="Empty dialog" onClose={onClose}>
        <p>Nothing interactive here.</p>
      </Modal>,
    )

    const dialog = await screen.findByRole('dialog', { name: 'Empty dialog' })
    fireEvent.keyDown(document, { key: 'Tab' })
    expect(document.activeElement).toBe(dialog)
  })
})
