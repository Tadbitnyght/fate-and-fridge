import { defineConfig, devices } from '@playwright/test'

const baseURL = process.env.E2E_BASE_URL ?? 'http://localhost:4173'

// E2E smoke tests are hermetic: they mock /api/v1/** with page.route and run
// against a local Vite dev server. Set E2E_BASE_URL to point at a real stack
// (for example the docker compose deployment) instead.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['list']] : 'list',
  use: {
    baseURL,
    trace: 'on-first-retry',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
    // Mobile audit projects. They run the
    // device-appropriate hermetic specs only: the dedicated mobile audit
    // (`e2e/mobile.spec.ts`: no horizontal overflow, 44 px tap targets, dialog
    // focus handling) and the axe matrix re-run at the device viewports.
    // The older specs stay in the desktop project on purpose: several assert
    // desktop presentation — e.g. `reference-data.spec.ts` requires a visible
    // `columnheader`, which the <= 40rem card layout hides by design — so
    // running them under phone emulation would test the CSS breakpoint, not the
    // product. Making that whole suite mobile-aware is not scope.
    // Both projects deliberately use the Chromium engine: CI installs only
    // Chromium, and the app's behaviour under test (layout, focus, CSS) is
    // engine-independent here. `iPhone 13` keeps the iOS viewport, device
    // pixel ratio, touch support and UA, but not the WebKit engine.
    {
      name: 'Pixel 7',
      testMatch: /(?:mobile|a11y)\.spec\.ts$/,
      use: { ...devices['Pixel 7'], browserName: 'chromium' },
    },
    {
      name: 'iPhone 13',
      testMatch: /(?:mobile|a11y)\.spec\.ts$/,
      use: { ...devices['iPhone 13'], browserName: 'chromium' },
    },
  ],
  webServer: process.env.E2E_BASE_URL
    ? undefined
    : {
        command: 'npm run dev -- --port 4173 --strictPort',
        url: 'http://localhost:4173',
        reuseExistingServer: !process.env.CI,
        timeout: 300_000,
      },
})
