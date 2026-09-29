import { expect, test } from '@playwright/test'

test('status page reports application and database health', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('heading', { name: 'Fate & Fridge', level: 1 })).toBeVisible()
  await expect(page.getByText('Operational')).toBeVisible()
  await expect(page.getByText('connected', { exact: true })).toBeVisible()
})

test('health API responds with ok', async ({ request }) => {
  const response = await request.get('/api/v1/health')

  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  expect(body.status).toBe('ok')
  expect(body.app).toBe('Fate & Fridge')
})
