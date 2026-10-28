import { expect, test, type Page, type WebSocketRoute } from '@playwright/test'

/**
 * Hermetic live-sync test. The SignalR hub is
 * mocked with `page.routeWebSocket` — no backend is involved — and the test
 * asserts the two observable provider effects
 * 1. a `groceries.updated` frame invalidates/refetches `GET /api/v1/groceries`
 * 2. the status indicator reflects the connected state.
 */

const setupComplete = { setup_completed: true, auth_mode: 'multiuser', recovery_key_present: true }

const adminSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 1, username: 'root', role: 'admin' },
  permissions: ['calendar.view', 'groceries.view', 'groceries.edit'],
  must_change_password: false,
}

const RANGE = { preset: 'next_7_days', start: '2026-10-12', end: '2026-10-18', day_count: 7 }

interface HubSocket {
  route: WebSocketRoute
}

/**
 * Minimal hermetic grocery API plus a hub mock. The hub answers the SignalR
 * JSON handshake (`{"protocol":"json","version":1}` + the record separator) so
 * the client's `start` resolves, and keeps every routed socket so the test
 * can push a server-to-client `event` invocation.
 */
async function mockLiveSync(page: Page): Promise<{
  getListFetches: () => number
  sendEvent: (event: Record<string, unknown>) => void
  completeHandshake: () => void
}> {
  let listFetches = 0
  let handshakeAllowed = false
  const sockets: HubSocket[] = []
  const pendingHandshakes: WebSocketRoute[] = []

  await page.route('**/api/v1/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api\/v1/, '')
    const method = route.request().method()

    if (path.endsWith('/setup/status')) {
      await route.fulfill({ json: setupComplete })
      return
    }
    if (path.endsWith('/auth/session')) {
      await route.fulfill({ json: adminSession })
      return
    }
    if (path === '/groceries' && method === 'GET') {
      listFetches += 1
      await route.fulfill({
        json: {
          range: RANGE,
          departments: [
            {
              department_id: 1,
              name: 'Produce',
              sort_order: 1,
              is_other: false,
              items: [
                {
                  identity: 'a'.repeat(64),
                  name: 'Onion',
                  quantity_text: '2',
                  quantity: 2,
                  unit: null,
                  ingredient_id: null,
                  department_id: 1,
                  checked: false,
                  source_type: 'recipe',
                  manual_item_id: null,
                  sources: [],
                },
              ],
            },
          ],
        },
      })
      return
    }
    if (path === '/groceries/preferences' && method === 'GET') {
      await route.fulfill({
        json: {
          hide_checked: false,
          range_preset: 'next_7_days',
          range_start: null,
          range_days: null,
        },
      })
      return
    }
    if (path === '/departments' || path === '/units' || path === '/ingredients') {
      await route.fulfill({ json: [] })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock for ${method} ${path}`, details: {} } },
    })
  })

  await page.routeWebSocket('**/hub/live', (route) => {
    sockets.push({ route })
    route.onMessage((message) => {
      const text = typeof message === 'string' ? message : message.toString()
      // SignalR handshake request. The empty handshake response is deferred
      // until the test has let the page settle (`completeHandshake`), so the
      // provider's connect-time invalidation runs against an already-active
      // groceries query instead of racing its initial fetch.
      if (text.includes('"protocol"')) {
        if (handshakeAllowed) {
          route.send('{}\u001e')
        } else {
          pendingHandshakes.push(route)
        }
      }
    })
  })

  return {
    getListFetches: () => listFetches,
    sendEvent: (event) => {
      const frame = `${JSON.stringify({ type: 1, target: 'event', arguments: [event] })}\u001e`
      for (const socket of sockets) {
        socket.route.send(frame)
      }
    },
    completeHandshake: () => {
      handshakeAllowed = true
      for (const route of pendingHandshakes.splice(0)) {
        route.send('{}\u001e')
      }
    },
  }
}

test('live sync: a groceries.updated frame refetches the list and shows Live', async ({ page }) => {
  const hub = await mockLiveSync(page)

  await page.goto('/groceries')
  await expect(page.getByRole('heading', { level: 2, name: 'Groceries' })).toBeVisible()
  await expect(page.getByText('Onion')).toBeVisible()

  // Let the page settle before completing the hub handshake, so the provider's
  // connect-time full invalidation runs against the now-active groceries query
  // and deterministically refetches it.
  hub.completeHandshake()

  // The provider connects over WebSockets, flips the status to Live, and runs a
  // full invalidation pass that refetches the list on connect.
  const indicator = page.getByTestId('live-sync-status')
  await expect(indicator).toHaveAttribute('data-state', 'connected')
  await expect(indicator).toContainText('Live')
  await expect.poll(() => hub.getListFetches(), { timeout: 15_000 }).toBeGreaterThanOrEqual(2)

  // The meaningful invariant: pushing a `groceries.updated` frame strictly
  // increases the list-fetch count relative to the settled count.
  const before = hub.getListFetches()
  hub.sendEvent({
    type: 'groceries.updated',
    entity: 'groceries',
    id: null,
    operation_id: null,
    actor_user_id: 1,
    timestamp: '2026-10-12T13:20:41.000Z',
  })

  await expect.poll(() => hub.getListFetches(), { timeout: 15_000 }).toBeGreaterThan(before)
  await expect(page.getByText('Onion')).toBeVisible()
})

test('live sync: the status indicator stays non-blocking and REST keeps working', async ({
  page,
}) => {
  // A hub that never answers the handshake leaves the connection down; the
  // groceries page must still render from REST.
  let listFetches = 0
  await page.route('**/api/v1/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api\/v1/, '')
    if (path.endsWith('/setup/status')) {
      await route.fulfill({ json: setupComplete })
      return
    }
    if (path.endsWith('/auth/session')) {
      await route.fulfill({ json: adminSession })
      return
    }
    if (path === '/groceries' && route.request().method() === 'GET') {
      listFetches += 1
      await route.fulfill({ json: { range: RANGE, departments: [] } })
      return
    }
    if (path === '/groceries/preferences') {
      await route.fulfill({ json: { hide_checked: false } })
      return
    }
    await route.fulfill({ json: [] })
  })
  await page.routeWebSocket('**/hub/live', () => {
    // Intentionally silent: the SignalR handshake never completes.
  })

  await page.goto('/groceries')
  await expect(page.getByRole('heading', { level: 2, name: 'Groceries' })).toBeVisible()
  expect(listFetches).toBeGreaterThanOrEqual(1)

  const indicator = page.getByTestId('live-sync-status')
  await expect(indicator).not.toHaveAttribute('data-state', 'connected')
  await expect(indicator).toHaveAttribute('aria-live', 'polite')
})
