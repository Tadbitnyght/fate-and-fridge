import { fireEvent, screen, waitFor } from '@testing-library/react'
import { Link } from 'react-router-dom'
import { afterEach, describe, expect, it } from 'vitest'

import { renderWithProviders } from '../test/utils'
import { useDocumentTitle } from './useDocumentTitle'

function TitleProbe() {
  useDocumentTitle()
  return <p>probe</p>
}

afterEach(() => {
  document.title = ''
})

describe('useDocumentTitle', () => {
  it('names the route in the browser tab', () => {
    renderWithProviders(<TitleProbe />, { route: '/calendar' })

    expect(document.title).toBe('Calendar · Fate & Fridge')
  })

  it('follows client-side navigation', async () => {
    renderWithProviders(
      <>
        <TitleProbe />
        <Link to="/settings/audit">Audit log</Link>
      </>,
      { route: '/recipes' },
    )

    expect(document.title).toBe('Recipes · Fate & Fridge')

    fireEvent.click(screen.getByRole('link', { name: 'Audit log' }))

    await waitFor(() => expect(document.title).toBe('Audit log · Fate & Fridge'))
  })

  it('sets the product name on the startup redirect', () => {
    renderWithProviders(<TitleProbe />, { route: '/' })

    expect(document.title).toBe('Fate & Fridge')
  })
})
