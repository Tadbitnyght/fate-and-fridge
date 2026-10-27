import { fireEvent, render, screen, within } from '@testing-library/react'
import type { ComponentProps } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { DangerousActionDialog } from './DangerousActionDialog'

const ACKNOWLEDGEMENT = 'I understand this cannot be undone'

function baseProps(
  overrides: Partial<ComponentProps<typeof DangerousActionDialog>> = {},
): ComponentProps<typeof DangerousActionDialog> {
  return {
    title: 'Delete recipe',
    description: 'This permanently removes the recipe.',
    warnings: ['This cannot be undone.', 'Past meals keep their snapshot text.'],
    reasonLabel: 'Reason',
    acknowledgementLabel: ACKNOWLEDGEMENT,
    confirmation: { label: 'Recipe name', expected: 'Chili', hint: 'Type it.' },
    confirmLabel: 'Delete recipe',
    onConfirm: vi.fn(),
    onClose: vi.fn(),
    ...overrides,
  }
}

function openDialog(overrides: Partial<ComponentProps<typeof DangerousActionDialog>> = {}) {
  const onConfirm = vi.fn()
  const onClose = vi.fn()
  render(<DangerousActionDialog {...baseProps({ onConfirm, onClose, ...overrides })} />)
  return { onConfirm, onClose }
}

function dialog(): HTMLElement {
  return screen.getByRole('dialog', { name: 'Delete recipe' })
}

function confirmButton(): HTMLElement {
  return within(dialog()).getByRole('button', { name: 'Delete recipe' })
}

/**
 * Fills a valid request. Pass `confirmation: null` when the dialog was rendered
 * without a typed confirmation.
 */
function fillValid(values: { reason?: string; confirmation?: string | null } = {}): void {
  const scope = within(dialog())
  fireEvent.change(scope.getByLabelText('Reason'), {
    target: { value: values.reason ?? 'Remove the retired recipe' },
  })
  fireEvent.click(scope.getByRole('checkbox', { name: ACKNOWLEDGEMENT }))

  const confirmation = values.confirmation === undefined ? 'Chili' : values.confirmation
  if (confirmation !== null) {
    fireEvent.change(scope.getByLabelText('Recipe name'), { target: { value: confirmation } })
  }
}

describe('DangerousActionDialog', () => {
  it('renders an accessible modal with the title, description, warnings and labels', () => {
    openDialog()
    const scope = within(dialog())

    expect(dialog()).toHaveAttribute('aria-modal', 'true')
    expect(document.activeElement).toBe(dialog())
    expect(scope.getByText('This permanently removes the recipe.')).toBeInTheDocument()
    expect(scope.getByText('This cannot be undone.')).toBeInTheDocument()
    expect(scope.getByText('Past meals keep their snapshot text.')).toBeInTheDocument()
    expect(scope.getByLabelText('Reason')).toBeInTheDocument()
    expect(scope.getByRole('checkbox', { name: ACKNOWLEDGEMENT })).not.toBeChecked()
    expect(scope.getByLabelText('Recipe name')).toBeInTheDocument()
    expect(scope.getByText('Type "Chili" exactly to confirm. Type it.')).toBeInTheDocument()
    expect(confirmButton()).toBeDisabled()
    expect(scope.getByRole('button', { name: 'Cancel' })).toBeEnabled()
  })

  it('keeps Confirm disabled until the reason, acknowledgement and confirmation are valid', () => {
    openDialog()
    const scope = within(dialog())
    expect(confirmButton()).toBeDisabled()

    fireEvent.change(scope.getByLabelText('Reason'), { target: { value: 'Cleanup' } })
    expect(confirmButton()).toBeDisabled()

    fireEvent.click(scope.getByRole('checkbox', { name: ACKNOWLEDGEMENT }))
    expect(confirmButton()).toBeDisabled()

    fireEvent.change(scope.getByLabelText('Recipe name'), { target: { value: 'Chil' } })
    expect(confirmButton()).toBeDisabled()

    fireEvent.change(scope.getByLabelText('Recipe name'), { target: { value: 'Chili' } })
    expect(confirmButton()).toBeEnabled()
  })

  it('rejects a blank or whitespace-only reason', () => {
    openDialog()
    const scope = within(dialog())
    fillValid({ reason: 'Cleanup' })
    expect(confirmButton()).toBeEnabled()

    fireEvent.change(scope.getByLabelText('Reason'), { target: { value: '   ' } })
    expect(confirmButton()).toBeDisabled()
  })

  it('rejects a reason longer than the maximum and reports the limit', () => {
    openDialog()
    const scope = within(dialog())
    fillValid({ reason: 'x'.repeat(501) })

    expect(confirmButton()).toBeDisabled()
    expect(scope.getByText('Reason must be at most 500 characters.')).toBeInTheDocument()
  })

  it('accepts a reason exactly at the maximum length', () => {
    openDialog()
    fillValid({ reason: 'x'.repeat(500) })
    expect(confirmButton()).toBeEnabled()
  })

  it('requires the acknowledgement checkbox', () => {
    openDialog()
    const scope = within(dialog())
    fireEvent.change(scope.getByLabelText('Reason'), { target: { value: 'Cleanup' } })
    fireEvent.change(scope.getByLabelText('Recipe name'), { target: { value: 'Chili' } })
    expect(confirmButton()).toBeDisabled()

    fireEvent.click(scope.getByRole('checkbox', { name: ACKNOWLEDGEMENT }))
    expect(confirmButton()).toBeEnabled()
  })

  it('requires an exact, case-sensitive confirmation and reports a near miss', () => {
    openDialog()
    const scope = within(dialog())
    fillValid({ confirmation: 'chili' })

    expect(confirmButton()).toBeDisabled()
    expect(scope.getByText('Type "Chili" exactly to confirm.')).toBeInTheDocument()

    fireEvent.change(scope.getByLabelText('Recipe name'), { target: { value: 'Chili ' } })
    expect(confirmButton()).toBeEnabled()
  })

  it('submits the trimmed reason and confirmation', () => {
    const { onConfirm } = openDialog()
    fillValid({ reason: '  Remove the retired recipe  ', confirmation: '  Chili  ' })

    fireEvent.click(confirmButton())

    expect(onConfirm).toHaveBeenCalledTimes(1)
    expect(onConfirm).toHaveBeenCalledWith({
      reason: 'Remove the retired recipe',
      confirmation: 'Chili',
    })
  })

  it('passes a null confirmation when none is required', () => {
    const { onConfirm } = openDialog({ confirmation: undefined })
    const scope = within(dialog())
    expect(scope.queryByLabelText('Recipe name')).not.toBeInTheDocument()

    fireEvent.change(scope.getByLabelText('Reason'), { target: { value: 'Cleanup' } })
    fireEvent.click(scope.getByRole('checkbox', { name: ACKNOWLEDGEMENT }))
    fireEvent.click(confirmButton())

    expect(onConfirm).toHaveBeenCalledWith({ reason: 'Cleanup', confirmation: null })
  })

  it('locks the controls while busy and ignores confirm clicks', () => {
    const onConfirm = vi.fn()
    const onClose = vi.fn()
    const { rerender } = render(<DangerousActionDialog {...baseProps({ onConfirm, onClose })} />)
    fillValid()
    expect(confirmButton()).toBeEnabled()

    rerender(<DangerousActionDialog {...baseProps({ onConfirm, onClose, busy: true })} />)
    const scope = within(dialog())

    expect(scope.getByLabelText('Reason')).toBeDisabled()
    expect(scope.getByRole('checkbox', { name: ACKNOWLEDGEMENT })).toBeDisabled()
    expect(scope.getByLabelText('Recipe name')).toBeDisabled()
    expect(scope.getByRole('button', { name: 'Cancel' })).toBeDisabled()
    expect(scope.getByRole('button', { name: 'Working…' })).toBeDisabled()

    fireEvent.click(scope.getByRole('button', { name: 'Working…' }))
    expect(onConfirm).not.toHaveBeenCalled()
  })

  it('renders a server error as an alert inside the dialog', () => {
    openDialog({ error: 'Pruning failed. Try again.' })

    expect(within(dialog()).getByRole('alert')).toHaveTextContent('Pruning failed. Try again.')
  })

  it('closes on Escape and on Cancel', () => {
    const { onClose } = openDialog()

    fireEvent.keyDown(document, { key: 'Escape' })
    expect(onClose).toHaveBeenCalledTimes(1)

    onClose.mockClear()
    fireEvent.click(within(dialog()).getByRole('button', { name: 'Cancel' }))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('does not close on Escape while busy', () => {
    const onClose = vi.fn()
    render(<DangerousActionDialog {...baseProps({ onClose, busy: true })} />)

    fireEvent.keyDown(document, { key: 'Escape' })

    expect(onClose).not.toHaveBeenCalled()
  })

  it('keeps Tab and Shift+Tab inside the dialog', () => {
    openDialog()
    fillValid()
    const scope = within(dialog())
    const first = scope.getByLabelText('Reason')
    const last = confirmButton()

    last.focus()
    fireEvent.keyDown(document, { key: 'Tab' })
    expect(document.activeElement).toBe(first)

    fireEvent.keyDown(document, { key: 'Tab', shiftKey: true })
    expect(document.activeElement).toBe(last)
  })
})
