import { fireEvent, screen, waitFor } from '@testing-library/react'
import { Route, Routes } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { installMockFetch, type MockResponse } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'
import { RestoreFromBackupPage } from './RestoreFromBackupPage'

const setupIncomplete = { setup_completed: false, auth_mode: null, recovery_key_present: false }

const setupComplete = {
  setup_completed: true,
  auth_mode: 'single_user',
  recovery_key_present: true,
}

const restoreAccepted = {
  restore_id: '20261021T150000Z-1f2e3d4c5b6a7988',
  filename: 'household.ffbackup',
  safety_backup_filename: 'fatefridge-0.1.0-20261021T150000Z.ffbackup',
}

const setupRequired: MockResponse = {
  status: 409,
  body: { error: { code: 'SETUP_REQUIRED', message: 'Setup is not complete.', details: {} } },
}

function installRestoreMocks(
  restoreResponse: MockResponse | (() => MockResponse) = () => ({
    status: 202,
    body: restoreAccepted,
  }),
) {
  return installMockFetch([
    {
      path: '/setup/status',
      respond: () => ({ status: 200, body: setupIncomplete }),
    },
    {
      path: '/setup/restore',
      method: 'POST',
      respond: typeof restoreResponse === 'function' ? restoreResponse : () => restoreResponse,
    },
    { path: '/auth/session', respond: () => setupRequired },
  ])
}

async function openRestorePage(): Promise<void> {
  renderWithProviders(<App />, { route: '/setup/restore' })
  expect(
    await screen.findByRole('heading', { level: 1, name: 'Restore from backup' }),
  ).toBeInTheDocument()
}

function chooseArchive(name = 'household.ffbackup'): void {
  const file = new File(['archive bytes'], name, { type: 'application/octet-stream' })
  fireEvent.change(screen.getByLabelText('Backup archive'), { target: { files: [file] } })
}

function fillRestoreForm(): void {
  chooseArchive()
  fireEvent.change(screen.getByLabelText('Recovery key'), { target: { value: 'PASTED-KEY' } })
  fireEvent.change(screen.getByLabelText('Type RESTORE to confirm'), {
    target: { value: 'RESTORE' },
  })
}

function submit(): void {
  fireEvent.click(screen.getByRole('button', { name: 'Restore backup' }))
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('restore from backup page', () => {
  it('keeps submission disabled until the archive, key, and typed confirmation are present', async () => {
    installRestoreMocks()
    await openRestorePage()

    const button = screen.getByRole('button', { name: 'Restore backup' })
    expect(button).toBeDisabled()

    chooseArchive()
    expect(button).toBeDisabled()

    fireEvent.change(screen.getByLabelText('Recovery key'), { target: { value: 'PASTED-KEY' } })
    expect(button).toBeDisabled()

    const confirmation = screen.getByLabelText('Type RESTORE to confirm')
    fireEvent.change(confirmation, { target: { value: 'restore' } })
    expect(button).toBeDisabled()

    fireEvent.change(confirmation, { target: { value: 'RESTORE ' } })
    expect(button).toBeEnabled()

    fireEvent.change(confirmation, { target: { value: 'RESTORE' } })
    expect(button).toBeEnabled()
  })

  it('posts the archive, key, and confirmation as multipart to /setup/restore', async () => {
    const { fetchMock } = installRestoreMocks()
    await openRestorePage()
    fillRestoreForm()
    submit()

    expect(await screen.findByRole('status')).toHaveTextContent(/restart/i)
    await waitFor(() =>
      expect(document.activeElement).toBe(
        screen.getByRole('heading', { level: 1, name: 'Restoring from backup' }),
      ),
    )
    expect(screen.queryByDisplayValue('PASTED-KEY')).not.toBeInTheDocument()

    const call = fetchMock.mock.calls.find(
      ([url, init]) =>
        String(url).endsWith('/setup/restore') &&
        (init as RequestInit | undefined)?.method === 'POST',
    )
    expect(call).toBeDefined()
    const [url, init] = call as [string, RequestInit]
    expect(url).toBe('/api/v1/setup/restore')
    expect(init.credentials).toBe('include')

    const headers = init.headers as Record<string, string>
    expect(headers['Content-Type']).toBeUndefined()

    const body = init.body as FormData
    expect(body).toBeInstanceOf(FormData)
    expect((body.get('file') as File).name).toBe('household.ffbackup')
    expect(body.get('recovery_key')).toBe('PASTED-KEY')
    expect(body.get('confirmation')).toBe('RESTORE')
  })

  const failureCases: Array<[string, RegExp]> = [
    ['BACKUP_KEY_MISMATCH', /does not match this backup/i],
    ['BACKUP_INTEGRITY_FAILED', /damaged or incomplete/i],
    ['BACKUP_FORMAT_UNSUPPORTED', /not a supported Fate & Fridge backup/i],
    ['BACKUP_SCHEMA_TOO_NEW', /newer version of Fate & Fridge/i],
    ['BACKUP_KEY_MISSING', /recovery key for this restore/i],
    ['RESTORE_IN_PROGRESS', /already running/i],
  ]

  it.each(failureCases)(
    'shows a readable message for %s and keeps the form usable',
    async (code, message) => {
      installRestoreMocks(() => ({
        status: code === 'RESTORE_IN_PROGRESS' ? 409 : 400,
        body: { error: { code, message: 'server detail', details: {} } },
      }))
      await openRestorePage()
      fillRestoreForm()
      submit()

      expect(await screen.findByRole('alert')).toHaveTextContent(message)
      expect(screen.getByRole('button', { name: 'Restore backup' })).toBeEnabled()
      expect(screen.getByLabelText('Recovery key')).toHaveValue('PASTED-KEY')
      expect(screen.getByLabelText('Type RESTORE to confirm')).toHaveValue('RESTORE')
    },
  )

  it('maps VALIDATION_FAILED field errors onto the fields', async () => {
    installRestoreMocks(() => ({
      status: 400,
      body: {
        error: {
          code: 'VALIDATION_FAILED',
          message: 'The request body failed validation.',
          details: {
            field_errors: {
              file: ['The archive upload must be at most 2 GB.'],
            },
          },
        },
      },
    }))
    await openRestorePage()
    fillRestoreForm()
    submit()

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'The archive upload must be at most 2 GB.',
    )
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('alert')))
    expect(screen.getByLabelText('Backup archive')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByRole('button', { name: 'Restore backup' })).toBeEnabled()
  })

  it('shows a generic message when the restore request cannot reach the server', async () => {
    installRestoreMocks(() => {
      throw new TypeError('Failed to fetch')
    })
    await openRestorePage()
    fillRestoreForm()
    submit()

    expect(await screen.findByRole('alert')).toHaveTextContent(/could not reach the server/i)
    expect(screen.getByRole('button', { name: 'Restore backup' })).toBeEnabled()
  })

  it('polls setup status through the restart and enters the app once it answers', async () => {
    let statusCalls = 0
    installMockFetch([
      {
        path: '/setup/status',
        respond: () => {
          statusCalls += 1
          if (statusCalls === 1) {
            // The container is going away: a connection error is expected.
            throw new TypeError('Failed to fetch')
          }
          if (statusCalls === 2) {
            return {
              status: 503,
              body: {
                error: {
                  code: 'RESTORE_IN_PROGRESS',
                  message: 'A restore is in progress.',
                  details: {},
                },
              },
            }
          }
          return { status: 200, body: setupComplete }
        },
      },
      {
        path: '/setup/restore',
        method: 'POST',
        respond: () => ({ status: 202, body: restoreAccepted }),
      },
    ])

    renderWithProviders(
      <Routes>
        <Route path="/setup/restore" element={<RestoreFromBackupPage pollIntervalMs={5} />} />
        <Route path="/" element={<p>normal startup flow</p>} />
      </Routes>,
      { route: '/setup/restore' },
    )

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Restore from backup' }),
    ).toBeInTheDocument()
    fillRestoreForm()
    submit()

    expect(await screen.findByText('normal startup flow')).toBeInTheDocument()
    expect(statusCalls).toBeGreaterThanOrEqual(3)
  })
})
