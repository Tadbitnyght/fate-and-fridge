import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { MAX_BACKUP_RESTORE_BYTES, type BackupRow } from '../../api/backups'
import { saveDownloadedFile } from '../../lib/download'
import { AuthProvider } from '../auth/AuthProvider'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import {
  installMockFetch,
  type MockFetch,
  type MockResponse,
  type MockRoute,
} from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'
import { BackupsSection } from './BackupsSection'

vi.mock('../../lib/download', () => ({ saveDownloadedFile: vi.fn() }))

const MANUAL_FILENAME = 'fatefridge-0.1.0-20261021T120000Z.ffbackup'
const SAFETY_FILENAME = 'fatefridge-0.1.0-20261020T090000Z.ffbackup'

const manualRow: BackupRow = {
  id: 12,
  filename: MANUAL_FILENAME,
  kind: 'manual',
  status: 'completed',
  app_version: '0.1.0',
  schema_version: '20261021023131_M13Backups',
  created_at: '2026-10-21T12:00:00+00:00',
  size_bytes: 1048576,
  checksum: '9f2c8a1b4d6e0f3a5c7b9d1e2f4a6b8c0d2e4f6a8b0c2d4e6f8a0b2c4d6e8f0a',
  error: null,
}

const safetyRow: BackupRow = {
  id: 11,
  filename: SAFETY_FILENAME,
  kind: 'safety',
  status: 'completed',
  app_version: '0.1.0',
  schema_version: '20261021023131_M13Backups',
  created_at: '2026-10-20T09:00:00+00:00',
  size_bytes: 512,
  checksum: 'aa2c8a1b4d6e0f3a5c7b9d1e2f4a6b8c0d2e4f6a8b0c2d4e6f8a0b2c4d6e8f0a',
  error: null,
}

const createdRow: BackupRow = {
  ...manualRow,
  id: 13,
  filename: 'fatefridge-0.1.0-20261021T130000Z.ffbackup',
}

const restoreAccepted = {
  restore_id: '20261021T150000Z-1f2e3d4c5b6a7988',
  backup_id: manualRow.id,
  filename: MANUAL_FILENAME,
  safety_backup_filename: 'fatefridge-0.1.0-20261021T150500Z.ffbackup',
}

const healthPayload = {
  status: 'ok',
  app: 'Fate & Fridge',
  version: '0.1.0',
  environment: 'test',
}

interface BackupsMockOptions {
  list?: BackupRow[]
  create?: MockRoute | MockResponse
  delete?: MockRoute | MockResponse
  restore?: MockRoute | MockResponse
  session?: unknown
}

/**
 * Hermetic Admin backups API. The list is stateful so a create (row added) or a
 * delete (row marked `deleted`) is visible on the automatic refetch, exactly as
 * the real endpoint behaves.
 */
function installBackupsMocks(options: BackupsMockOptions = {}): MockFetch {
  const rows = [...(options.list ?? [manualRow, safetyRow])]

  const createRoute: MockRoute =
    options.create === undefined
      ? {
          path: '/admin/backups',
          method: 'POST',
          respond: () => {
            rows.unshift(createdRow)
            return { status: 201, body: createdRow }
          },
        }
      : { path: '/admin/backups', method: 'POST', ...options.create }

  const deleteRoute: MockRoute =
    options.delete === undefined
      ? {
          path: `/admin/backups/${manualRow.id}`,
          method: 'DELETE',
          respond: () => {
            const index = rows.findIndex((row) => row.id === manualRow.id)
            if (index >= 0) {
              rows[index] = { ...rows[index], status: 'deleted', checksum: '', size_bytes: 0 }
            }
            return { status: 200, body: rows[index] }
          },
        }
      : { path: `/admin/backups/${manualRow.id}`, method: 'DELETE', ...options.delete }

  const restoreRoute: MockRoute =
    options.restore === undefined
      ? {
          path: '/admin/backups/restore',
          method: 'POST',
          respond: () => ({ status: 202, body: restoreAccepted }),
        }
      : { path: '/admin/backups/restore', method: 'POST', ...options.restore }

  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: options.session ?? adminSession },
    // Download first: its URL also contains `/admin/backups`.
    {
      path: '/download',
      respond: () => ({
        status: 200,
        raw: 'encrypted-archive',
        headers: {
          'Content-Type': 'application/octet-stream',
          'Content-Disposition': `attachment; filename="${MANUAL_FILENAME}"`,
        },
      }),
    },
    restoreRoute,
    createRoute,
    deleteRoute,
    {
      path: '/admin/backups',
      method: 'GET',
      respond: () => ({ status: 200, body: rows }),
    },
    { path: '/health', body: healthPayload },
  ])
}

function renderBackupsSection(options: BackupsMockOptions = {}): MockFetch {
  const mock = installBackupsMocks(options)
  renderWithProviders(<App />, { route: '/settings/backups' })
  return mock
}

function backupRows(): HTMLElement[] {
  const table = screen.getByRole('table', { name: 'Backups, newest first' })
  return within(table).getAllByRole('row').slice(1)
}

function requestBodies(mock: MockFetch, path: string, method: string): unknown[] {
  return mock.fetchMock.mock.calls
    .filter(
      ([url, init]) =>
        String(url).includes(path) &&
        ((init as RequestInit | undefined)?.method ?? 'GET').toUpperCase() === method,
    )
    .map(([, init]) => (init as RequestInit).body)
}

async function fillDangerousDialog(confirmation?: string): Promise<HTMLElement> {
  const dialog = await screen.findByRole('dialog')
  fireEvent.change(within(dialog).getByLabelText('Reason'), {
    target: { value: 'Before the upgrade' },
  })
  fireEvent.click(within(dialog).getByRole('checkbox'))
  if (confirmation !== undefined) {
    fireEvent.change(within(dialog).getByLabelText(/^Type \w+ to confirm$/), {
      target: { value: confirmation },
    })
  }
  return dialog
}

function chooseArchive(name = 'household.ffbackup'): void {
  const file = new File(['archive bytes'], name, { type: 'application/octet-stream' })
  fireEvent.change(screen.getByLabelText('Backup archive'), { target: { files: [file] } })
}

afterEach(() => {
  vi.unstubAllGlobals()
})

beforeEach(() => {
  vi.clearAllMocks()
})

describe('backups section', () => {
  it('renders the list newest first with kind, status, versions, size, date, and checksum', async () => {
    renderBackupsSection()

    await screen.findByRole('table', { name: 'Backups, newest first' })
    const rows = backupRows()
    expect(rows).toHaveLength(2)

    const first = rows[0]
    expect(within(first).getByText('Manual')).toBeInTheDocument()
    expect(within(first).getByText('Completed')).toBeInTheDocument()
    expect(within(first).getByText('0.1.0')).toBeInTheDocument()
    expect(within(first).getByText('20261021023131_M13Backups')).toBeInTheDocument()
    expect(within(first).getByText('1 MB')).toBeInTheDocument()
    expect(within(first).getByText(MANUAL_FILENAME)).toBeInTheDocument()
    // The date is rendered through the shared household timestamp format.
    expect(within(first).getByText(/10-21-26/)).toBeInTheDocument()
    // The checksum is truncated in the summary and available in full on expand.
    expect(within(first).getByText('9f2c8a1b4d6e…')).toBeInTheDocument()
    expect(within(first).getByText(manualRow.checksum)).toBeInTheDocument()

    const second = rows[1]
    expect(within(second).getByText('Safety')).toBeInTheDocument()
    expect(within(second).getByText('512 B')).toBeInTheDocument()
    expect(within(second).getByText(SAFETY_FILENAME)).toBeInTheDocument()
  })

  it('shows an empty state when there are no backups', async () => {
    renderBackupsSection({ list: [] })

    expect(await screen.findByText(/No backups yet/i)).toBeInTheDocument()
    expect(screen.queryByRole('table', { name: 'Backups, newest first' })).not.toBeInTheDocument()
  })

  it('shows a readable error state when the list cannot be loaded', async () => {
    const mock = installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      {
        path: '/admin/backups',
        method: 'GET',
        status: 500,
        body: {
          error: { code: 'INTERNAL_ERROR', message: 'The server failed.', details: {} },
        },
      },
    ])
    renderWithProviders(<App />, { route: '/settings/backups' })

    expect(await screen.findByText('Cannot load backups')).toBeInTheDocument()
    expect(mock.callsFor('/admin/backups')).toBe(1)
  })

  it('creates a backup through the dangerous-action dialog and offers the download', async () => {
    const mock = renderBackupsSection()
    await screen.findByRole('table', { name: 'Backups, newest first' })

    fireEvent.click(screen.getByRole('button', { name: 'Create backup' }))
    const dialog = await fillDangerousDialog()

    // The dialog is a gate: the reason and the acknowledgement are required.
    const confirm = within(dialog).getByRole('button', { name: 'Create backup' })
    expect(confirm).toBeEnabled()
    fireEvent.click(confirm)

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    const banner = await screen.findByRole('status')
    expect(banner).toHaveTextContent(/Backup created:/)
    expect(within(banner).getByRole('button')).toBeInTheDocument()

    await waitFor(() => expect(backupRows()).toHaveLength(3))

    const bodies = requestBodies(mock, '/admin/backups', 'POST')
    expect(bodies).toHaveLength(1)
    expect(JSON.parse(String(bodies[0]))).toEqual({ reason: 'Before the upgrade' })
  })

  it('keeps the create dialog open and disables confirm until it is valid', async () => {
    renderBackupsSection()
    await screen.findByRole('table', { name: 'Backups, newest first' })

    fireEvent.click(screen.getByRole('button', { name: 'Create backup' }))
    const dialog = await screen.findByRole('dialog')
    const confirm = within(dialog).getByRole('button', { name: 'Create backup' })
    expect(confirm).toBeDisabled()

    fireEvent.change(within(dialog).getByLabelText('Reason'), {
      target: { value: 'Before the upgrade' },
    })
    expect(confirm).toBeDisabled()

    fireEvent.click(within(dialog).getByRole('checkbox'))
    expect(confirm).toBeEnabled()
  })

  const createFailures: Array<[string, RegExp, number]> = [
    ['BACKUP_CLIENT_TOO_OLD', /database tools are older/i, 409],
    ['BACKUP_KEY_MISSING', /no usable recovery key/i, 409],
    ['BACKUP_IN_PROGRESS', /already running/i, 409],
    ['BACKUP_FAILED', /failed before it completed/i, 500],
  ]

  it.each(createFailures)(
    'shows a readable message for %s and keeps the create dialog usable',
    async (code, message, status) => {
      renderBackupsSection({
        create: {
          status,
          body: { error: { code, message: 'server detail', details: {} } },
        },
      })
      await screen.findByRole('table', { name: 'Backups, newest first' })

      fireEvent.click(screen.getByRole('button', { name: 'Create backup' }))
      const dialog = await fillDangerousDialog()
      fireEvent.click(within(dialog).getByRole('button', { name: 'Create backup' }))

      expect(await within(dialog).findByRole('alert')).toHaveTextContent(message)
      expect(screen.getByRole('dialog')).toBeInTheDocument()
      expect(within(dialog).getByRole('button', { name: 'Create backup' })).toBeEnabled()
    },
  )

  it('downloads the encrypted archive through the download endpoint', async () => {
    const mock = renderBackupsSection()
    await screen.findByRole('table', { name: 'Backups, newest first' })

    fireEvent.click(screen.getByRole('button', { name: `Download ${MANUAL_FILENAME}` }))

    await waitFor(() => expect(saveDownloadedFile).toHaveBeenCalledTimes(1))
    const saved = vi.mocked(saveDownloadedFile).mock.calls[0][0]
    expect(saved.filename).toBe(MANUAL_FILENAME)
    expect(saved.blob).toBeInstanceOf(Blob)

    const urls = mock.fetchMock.mock.calls.map(([url]) => String(url))
    expect(urls).toContain(`/api/v1/admin/backups/${manualRow.id}/download`)
  })

  it('deletes a backup only after the reason and the typed DELETE confirmation', async () => {
    const mock = renderBackupsSection()
    await screen.findByRole('table', { name: 'Backups, newest first' })

    fireEvent.click(screen.getByRole('button', { name: `Delete ${MANUAL_FILENAME}` }))
    const dialog = await fillDangerousDialog('delete')

    const confirm = within(dialog).getByRole('button', { name: 'Delete backup' })
    expect(confirm).toBeDisabled()

    fireEvent.change(within(dialog).getByLabelText('Type DELETE to confirm'), {
      target: { value: 'DELETE' },
    })
    expect(confirm).toBeEnabled()
    fireEvent.click(confirm)

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() => expect(within(backupRows()[0]).getByText('Deleted')).toBeInTheDocument())

    const bodies = requestBodies(mock, `/admin/backups/${manualRow.id}`, 'DELETE')
    expect(bodies).toHaveLength(1)
    expect(JSON.parse(String(bodies[0]))).toEqual({
      reason: 'Before the upgrade',
      confirmation: 'DELETE',
    })
  })

  it('shows a readable message when the delete fails', async () => {
    renderBackupsSection({
      delete: {
        status: 404,
        body: {
          error: { code: 'BACKUP_NOT_FOUND', message: 'No such backup.', details: {} },
        },
      },
    })
    await screen.findByRole('table', { name: 'Backups, newest first' })

    fireEvent.click(screen.getByRole('button', { name: `Delete ${MANUAL_FILENAME}` }))
    const dialog = await fillDangerousDialog('DELETE')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete backup' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/no longer exists/i)
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('restores a stored archive as JSON and switches to the restarting state', async () => {
    const mock = renderBackupsSection()
    await screen.findByRole('table', { name: 'Backups, newest first' })

    fireEvent.click(screen.getByRole('button', { name: `Restore ${MANUAL_FILENAME}` }))
    const dialog = await fillDangerousDialog('RESTORE')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Restore backup' }))

    const status = await screen.findByRole('status')
    expect(status).toHaveTextContent(/restart/i)
    expect(within(status).getByText(restoreAccepted.restore_id)).toBeInTheDocument()
    expect(within(status).getByText(restoreAccepted.safety_backup_filename)).toBeInTheDocument()

    const bodies = requestBodies(mock, '/admin/backups/restore', 'POST')
    expect(bodies).toHaveLength(1)
    expect(String(bodies[0])).not.toBe('[object FormData]')
    expect(JSON.parse(String(bodies[0]))).toEqual({
      backup_id: manualRow.id,
      reason: 'Before the upgrade',
      confirmation: 'RESTORE',
    })
  })

  it('uploads an archive as multipart and switches to the restarting state', async () => {
    const mock = renderBackupsSection()
    await screen.findByRole('table', { name: 'Backups, newest first' })

    chooseArchive('household.ffbackup')
    fireEvent.click(screen.getByRole('button', { name: 'Restore uploaded archive' }))
    const dialog = await fillDangerousDialog('RESTORE')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Restore backup' }))

    expect(await screen.findByRole('status')).toHaveTextContent(/restart/i)

    const call = mock.fetchMock.mock.calls.find(
      ([url, init]) =>
        String(url).endsWith('/admin/backups/restore') &&
        (init as RequestInit | undefined)?.method === 'POST',
    )
    expect(call).toBeDefined()
    const [url, init] = call as [string, RequestInit]
    expect(url).toBe('/api/v1/admin/backups/restore')
    expect(init.credentials).toBe('include')
    expect((init.headers as Record<string, string>)['Content-Type']).toBeUndefined()

    const body = init.body as FormData
    expect(body).toBeInstanceOf(FormData)
    expect((body.get('file') as File).name).toBe('household.ffbackup')
    expect(body.get('reason')).toBe('Before the upgrade')
    expect(body.get('confirmation')).toBe('RESTORE')
  })

  it('refuses an oversized upload client-side with a clear 2 GB message', async () => {
    renderBackupsSection()
    await screen.findByRole('table', { name: 'Backups, newest first' })

    const oversized = new File(['x'], 'huge.ffbackup', { type: 'application/octet-stream' })
    Object.defineProperty(oversized, 'size', { value: MAX_BACKUP_RESTORE_BYTES + 1 })
    fireEvent.change(screen.getByLabelText('Backup archive'), { target: { files: [oversized] } })

    expect(await screen.findByRole('alert')).toHaveTextContent(/2 GB or smaller/i)

    fireEvent.click(screen.getByRole('button', { name: 'Restore uploaded archive' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  const restoreFailures: Array<[string, RegExp, number]> = [
    ['BACKUP_KEY_MISMATCH', /does not match this backup/i, 400],
    ['BACKUP_INTEGRITY_FAILED', /damaged or incomplete/i, 400],
    ['BACKUP_FORMAT_UNSUPPORTED', /not a supported Fate & Fridge backup/i, 400],
    ['BACKUP_SCHEMA_TOO_NEW', /newer version of Fate & Fridge/i, 400],
    ['BACKUP_FILE_MISSING', /missing from the server/i, 404],
    ['BACKUP_NOT_FOUND', /no longer exists/i, 404],
    ['RESTORE_IN_PROGRESS', /already running/i, 409],
    ['RESTORE_FAILED', /could not be started/i, 500],
    ['BACKUP_KEY_MISSING', /no usable recovery key/i, 409],
  ]

  it.each(restoreFailures)(
    'shows a readable message for restore failure %s and keeps the dialog open',
    async (code, message, status) => {
      renderBackupsSection({
        restore: {
          status,
          body: { error: { code, message: 'server detail', details: {} } },
        },
      })
      await screen.findByRole('table', { name: 'Backups, newest first' })

      fireEvent.click(screen.getByRole('button', { name: `Restore ${MANUAL_FILENAME}` }))
      const dialog = await fillDangerousDialog('RESTORE')
      fireEvent.click(within(dialog).getByRole('button', { name: 'Restore backup' }))

      expect(await within(dialog).findByRole('alert')).toHaveTextContent(message)
      expect(screen.queryByRole('status')).not.toBeInTheDocument()
    },
  )

  it('polls health through a connection error and re-enters the app once healthy', async () => {
    let healthCalls = 0
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      {
        path: '/admin/backups/restore',
        method: 'POST',
        respond: () => ({ status: 202, body: restoreAccepted }),
      },
      {
        path: '/admin/backups',
        method: 'GET',
        respond: () => ({ status: 200, body: [manualRow] }),
      },
      {
        path: '/health',
        respond: () => {
          healthCalls += 1
          if (healthCalls <= 2) {
            // The container is going away: a connection error is expected.
            throw new TypeError('Failed to fetch')
          }
          return { status: 200, body: healthPayload }
        },
      },
    ])

    renderWithProviders(
      <Routes>
        <Route
          path="/settings/backups"
          element={
            <AuthProvider>
              <BackupsSection pollIntervalMs={20} />
            </AuthProvider>
          }
        />
        <Route path="/" element={<p>app home</p>} />
      </Routes>,
      { route: '/settings/backups' },
    )

    fireEvent.click(await screen.findByRole('button', { name: `Restore ${MANUAL_FILENAME}` }))
    const dialog = await fillDangerousDialog('RESTORE')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Restore backup' }))

    expect(await screen.findByRole('status')).toHaveTextContent(/restart/i)
    // The first health attempts fail (the app is down), and the page says so.
    expect(await screen.findByText(/The app is restarting/i)).toBeInTheDocument()
    // Once health answers, the client re-enters the app.
    expect(await screen.findByText('app home')).toBeInTheDocument()
    expect(healthCalls).toBeGreaterThanOrEqual(3)
  })

  it('hides the section from members and never fetches the backup list', async () => {
    const mock = renderBackupsSection({ session: memberSession })
    await screen.findByRole('heading', { name: 'Backups' })

    expect(screen.getByText(/limited to admins/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Create backup' })).not.toBeInTheDocument()
    expect(mock.callsFor('/admin/backups')).toBe(0)
  })
})
