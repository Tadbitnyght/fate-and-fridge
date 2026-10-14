/// <reference lib="dom" />
import { expect, test, type Page } from '@playwright/test'

const setupComplete = { setup_completed: true, auth_mode: 'multiuser', recovery_key_present: true }

const adminSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 1, username: 'root', role: 'admin' },
  permissions: [
    'calendar.view',
    'calendar.edit',
    'groceries.view',
    'groceries.edit',
    'recipes.view',
    'recipes.edit',
    'reference_data.edit',
  ],
  must_change_password: false,
}

interface CalendarEntry {
  id: number
  entry_type: string
  recipe: { id: number; name: string; image_url: string | null; status: string } | null
  display_text: string
  is_locked: boolean
  diner_count: number | null
  effective_diner_count: number
  restaurant_name: string | null
  location_text: string | null
  custom_label: string | null
  notes: string | null
  icon_key: string | null
  include_groceries: boolean
  blocks_automation: boolean
  created_at: string
  updated_at: string
}

function entry(overrides: Partial<CalendarEntry>): CalendarEntry {
  return {
    id: 1,
    entry_type: 'recipe',
    recipe: { id: 5, name: 'Lasagna', image_url: null, status: 'normal' },
    display_text: 'Lasagna',
    is_locked: false,
    diner_count: null,
    effective_diner_count: 4,
    restaurant_name: null,
    location_text: null,
    custom_label: null,
    notes: null,
    icon_key: null,
    include_groceries: true,
    blocks_automation: false,
    created_at: '2026-11-01T00:00:00+00:00',
    updated_at: '2026-11-01T00:00:00+00:00',
    ...overrides,
  }
}

/** October 2026 entries covering the printed content rules. */
const OCTOBER_ENTRIES: Record<string, CalendarEntry> = {
  '2026-10-05': entry({
    entry_type: 'recipe',
    display_text: 'Lasagna',
    recipe: { id: 5, name: 'Lasagna', image_url: '/meal.jpg', status: 'normal' },
  }),
  '2026-10-12': entry({
    id: 2,
    entry_type: 'holiday',
    display_text: 'Thanksgiving',
    recipe: null,
    icon_key: 'turkey',
    include_groceries: false,
  }),
  '2026-10-20': entry({
    id: 3,
    entry_type: 'eat_out',
    display_text: 'Pizza Place',
    recipe: null,
    restaurant_name: 'Pizza Place',
    location_text: 'Downtown',
    notes: 'Reservation at 7',
    diner_count: 6,
    is_locked: true,
    include_groceries: false,
  }),
}

/** A past month (relative to `today` 2026-10-14) with one stored recipe. */
const PAST_ENTRIES: Record<string, CalendarEntry> = {
  '2026-08-12': entry({
    id: 7,
    display_text: 'Zucchini boats',
    recipe: { id: 9, name: 'Zucchini boats', image_url: null, status: 'normal' },
  }),
}

function dayFor(date: string, value: CalendarEntry | null) {
  return {
    date,
    is_today: false,
    is_historical: false,
    can_edit: true,
    entry: value,
    repeat_warning: null,
  }
}

interface PrintMockOptions {
  /** Entries keyed by ISO date; defaults to `OCTOBER_ENTRIES`. */
  entries?: Record<string, CalendarEntry>
  session?: unknown
}

/** Hermetic calendar API mirroring the other e2e specs, with October entries. */
async function mockPrintApi(page: Page, options: PrintMockOptions = {}): Promise<void> {
  const entries = options.entries ?? OCTOBER_ENTRIES
  const session = options.session ?? adminSession

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname.replace(/^\/api\/v1/, '')
    const method = request.method()

    if (path.endsWith('/setup/status')) {
      await route.fulfill({ json: setupComplete })
      return
    }
    if (path.endsWith('/auth/session')) {
      await route.fulfill({ json: session })
      return
    }
    if (path === '/health' || path === '/health/db') {
      await route.fulfill({ json: { status: 'ok', database: 'connected' } })
      return
    }
    if (path === '/settings/household') {
      await route.fulfill({
        json: {
          household_name: 'The Smiths',
          timezone: 'America/Chicago',
          week_starts_on: 0,
          default_diner_count: 4,
          startup_page: 'calendar',
        },
      })
      return
    }
    if (path === '/recipes') {
      await route.fulfill({ json: { items: [], total: 0, limit: 5, offset: 0 } })
      return
    }
    if (path === '/calendar' && method === 'GET') {
      const start = url.searchParams.get('start') ?? ''
      const end = url.searchParams.get('end') ?? ''
      await route.fulfill({
        json: {
          start,
          end,
          today: '2026-10-14',
          week_starts_on: 0,
          entries: Object.entries(entries)
            .filter(([date]) => date >= start && date <= end)
            .map(([date, value]) => dayFor(date, value)),
        },
      })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock route for ${method} ${path}` } },
    })
  })
}

/** A minimal grocery list so `/groceries/print` renders a portrait job. */
async function mockGroceryPrintApi(page: Page): Promise<void> {
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname.replace(/^\/api\/v1/, '')
    const method = request.method()

    if (path.endsWith('/setup/status')) {
      await route.fulfill({ json: setupComplete })
      return
    }
    if (path.endsWith('/auth/session')) {
      await route.fulfill({ json: adminSession })
      return
    }
    if (path === '/health' || path === '/health/db') {
      await route.fulfill({ json: { status: 'ok', database: 'connected' } })
      return
    }
    if (path === '/groceries' && method === 'GET') {
      await route.fulfill({
        json: {
          range: { preset: 'next_7_days', start: '2026-10-12', end: '2026-10-18', day_count: 7 },
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
                  sources: [{ recipe_id: 5, recipe_name: 'Tacos', date: '2026-10-13' }],
                },
              ],
            },
          ],
        },
      })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock route for ${method} ${path}` } },
    })
  })
}

/** Every day of a month carries the same long name (busy/pathological content). */
function filledMonthEntries(
  year: number,
  monthIndex: number,
  name: string,
): Record<string, CalendarEntry> {
  const daysInMonth = new Date(year, monthIndex + 1, 0).getDate()
  const entries: Record<string, CalendarEntry> = {}
  for (let day = 1; day <= daysInMonth; day += 1) {
    const iso = `${year}-${String(monthIndex + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`
    entries[iso] = entry({
      id: day,
      display_text: name,
      recipe: { id: day, name, image_url: null, status: 'normal' },
    })
  }
  return entries
}

interface PdfSummary {
  pages: number
  widthPt: number
  heightPt: number
  orientation: 'landscape' | 'portrait'
}

/**
 * Summarizes a Chromium PDF buffer: the page count from the page objects and
 * the media-box geometry of the first page. `preferCSSPageSize` makes the
 * media box follow the CSS `@page` size, so this proves the printed geometry.
 */
function summarizePdf(pdf: Buffer): PdfSummary {
  const text = pdf.toString('latin1')
  const pages = text.match(/\/Type\s*\/Page\b/g)?.length ?? 0
  const media = /\/MediaBox\s*\[\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\]/.exec(text)
  if (media === null) {
    throw new Error('no /MediaBox found in the PDF')
  }
  const widthPt = Number(media[3]) - Number(media[1])
  const heightPt = Number(media[4]) - Number(media[2])
  return {
    pages,
    widthPt,
    heightPt,
    orientation: widthPt > heightPt ? 'landscape' : 'portrait',
  }
}

/** A4 dimensions in PDF points (1pt = 1/72in); landscape swaps width/height. */
const A4_LONG_PT = 841.89
const A4_SHORT_PT = 595.28

/** CSS reference resolution: 96 px per inch, so 1pt = 96/72 px. */
const PT_TO_PX = 96 / 72

/** Converts points to the CSS pixels a computed font-size reports. */
function ptToPx(pt: number): number {
  return pt * PT_TO_PX
}

interface AppliedPrintScale {
  /** The month's applied auto-shrink multiplier (`--calendar-print-scale`). */
  scale: number
  /** The month's computed font size in CSS pixels. */
  fontSizePx: number
  /** The legibility floor in pt (`--calendar-print-floor-font-pt`). */
  floorFontPt: number
  /** The unshrunk month font in pt (`--calendar-print-base-font-pt`). */
  baseFontPt: number
  /** Whether the month released its height cap and spills onto another page. */
  spill: boolean
}

/**
 * Reads the first printed month's applied auto-shrink scale and computed font
 * size from the CSS custom properties `index.css` publishes, so the test can
 * assert the shrink never drops below the ≈10pt legibility floor (M10-03).
 */
async function readAppliedPrintScale(page: Page): Promise<AppliedPrintScale> {
  return page.evaluate(() => {
    const month = document.querySelector('.calendar-print-month')
    if (month === null) {
      throw new Error('no .calendar-print-month in the DOM')
    }
    const monthStyle = getComputedStyle(month)
    const rootStyle = getComputedStyle(document.documentElement)
    return {
      scale: Number.parseFloat(monthStyle.getPropertyValue('--calendar-print-scale').trim() || '1'),
      fontSizePx: Number.parseFloat(monthStyle.fontSize),
      floorFontPt: Number.parseFloat(
        rootStyle.getPropertyValue('--calendar-print-floor-font-pt').trim(),
      ),
      baseFontPt: Number.parseFloat(
        rootStyle.getPropertyValue('--calendar-print-base-font-pt').trim(),
      ),
      spill: month.getAttribute('data-spill') === 'true',
    }
  })
}

interface PageRule {
  selector: string
  size: string
  margin: string
}

/**
 * Collects every `@page` rule (including those nested in `@media`) so the test
 * can prove page-size ownership is named rather than app-wide.
 */
async function collectPageRules(page: Page): Promise<PageRule[]> {
  return page.evaluate(() => {
    const results: { selector: string; size: string; margin: string }[] = []

    const parseSelector = (cssText: string): string => {
      const match = /@page\s*([^{]*)\{/.exec(cssText)
      return match === null ? '' : match[1].trim()
    }
    const parseProp = (cssText: string, prop: string): string => {
      const match = new RegExp(`${prop}\\s*:\\s*([^;}]+)`).exec(cssText)
      return match === null ? '' : match[1].trim()
    }

    const walk = (rules: CSSRuleList): void => {
      for (const rule of Array.from(rules)) {
        if (rule.type === CSSRule.PAGE_RULE) {
          const pageRule = rule as CSSPageRule
          const selector = (pageRule.selectorText ?? '').trim() || parseSelector(rule.cssText)
          const size = pageRule.style?.getPropertyValue('size') || parseProp(rule.cssText, 'size')
          const margin =
            pageRule.style?.getPropertyValue('margin') || parseProp(rule.cssText, 'margin')
          results.push({ selector, size, margin })
        }
        const nested = (rule as CSSGroupingRule).cssRules
        if (nested) {
          walk(nested)
        }
      }
    }

    for (const sheet of Array.from(document.styleSheets)) {
      try {
        walk(sheet.cssRules)
      } catch {
        // Ignore stylesheets whose rules are not readable (none expected here).
      }
    }
    return results
  })
}

test('print: the calendar print view renders a month and hides chrome in print', async ({
  page,
}) => {
  await mockPrintApi(page)

  await page.goto('/calendar/print?month=2026-10')
  await expect(page.getByRole('heading', { level: 3, name: 'October 2026' })).toBeVisible()
  await expect(page.locator('.calendar-print-month__cell').first()).toBeVisible()
  await expect(page.getByText('Lasagna')).toBeVisible()
  await expect(page.getByText('Eat out')).toBeVisible()

  await page.emulateMedia({ media: 'print' })

  // App chrome and the screen-only controls are hidden.
  await expect(page.locator('.app-header')).toBeHidden()
  await expect(page.locator('.app-nav')).toBeHidden()
  await expect(page.locator('.app-footer')).toBeHidden()
  await expect(page.locator('.skip-link')).toBeHidden()
  await expect(page.locator('.calendar-print-controls')).toBeHidden()
  await expect(page.getByRole('button', { name: 'Print' })).toBeHidden()
  await expect(page.getByRole('link', { name: 'Back to calendar' })).toBeHidden()

  // The printable month and its heading survive, black on white.
  await expect(page.getByRole('heading', { level: 3, name: 'October 2026' })).toBeVisible()
  await expect(page.locator('.calendar-print-month__cell').first()).toBeVisible()
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(255, 255, 255)')
  await expect(page.locator('body')).toHaveCSS('color', 'rgb(0, 0, 0)')
  await expect(page.locator('.calendar-print-month__cell').first()).toHaveCSS(
    'color',
    'rgb(0, 0, 0)',
  )
})

test('print: the printed month shows allowed content and none of the excluded data', async ({
  page,
}) => {
  await mockPrintApi(page)

  await page.goto('/calendar/print?month=2026-10')
  await expect(page.getByRole('heading', { level: 3, name: 'October 2026' })).toBeVisible()

  const month = page.locator('.calendar-print-month')

  // Allowed: the day number plus the recipe / special-event / eat-out text.
  const lasagnaCell = month.locator('.calendar-print-month__cell').filter({ hasText: 'Lasagna' })
  await expect(lasagnaCell.locator('.calendar-print-month__day')).toHaveText('5')
  await expect(month.getByText('Thanksgiving')).toBeVisible()
  await expect(month.getByText('Eat out')).toBeVisible()

  // Allowed: the curated special-event icon, rendered black (currentColor) and
  // decorative for assistive tech.
  const icon = month.locator('.calendar-print-month__icon')
  await expect(icon).toHaveCount(1)
  await expect(icon.first()).toHaveAttribute('stroke', 'currentColor')
  await expect(icon.first()).toHaveAttribute('aria-hidden', 'true')
  await page.emulateMedia({ media: 'print' })
  await expect(icon.first()).toHaveCSS('color', 'rgb(0, 0, 0)')

  // Excluded: meal photos, lock icons, notes, diner count, restaurant/location.
  await expect(month.locator('img')).toHaveCount(0)
  await expect(month.locator('.calendar-grid__lock')).toHaveCount(0)
  await expect(month.getByText('🔒')).toHaveCount(0)
  await expect(month.getByText('Reservation at 7')).toHaveCount(0)
  await expect(month.getByText(/diner/i)).toHaveCount(0)
  await expect(month.getByText('Pizza Place')).toHaveCount(0)
  await expect(month.getByText('Downtown')).toHaveCount(0)
})

test('print: page-size ownership is named with no unnamed @page rule', async ({ page }) => {
  await mockPrintApi(page)

  // The non-print calendar month view must not inherit a landscape page size.
  await page.goto('/calendar')
  await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()

  const rules = await collectPageRules(page)

  // Neither the removed M5 `@page { size: landscape }` nor the removed M8
  // global `@page { margin: 12mm }` may reappear as an unnamed page rule.
  expect(rules.filter((rule) => rule.selector === '')).toEqual([])

  const named = new Map(rules.map((rule) => [rule.selector, rule]))
  expect(named.get('calendar-print')?.size?.toLowerCase()).toContain('a4')
  expect(named.get('calendar-print')?.size).toContain('landscape')
  // `portrait` is the default orientation for A4, so the CSSOM normalizes
  // `size: A4 portrait` to `a4`; the dedicated PDF test proves the orientation.
  expect(named.get('grocery-list')?.size?.toLowerCase()).toContain('a4')
  expect(named.get('grocery-list')?.size).not.toContain('landscape')

  // The @page margin cannot reference a custom property, so the e2e test keeps
  // the literal in sync with the shared print geometry.
  const geometryMarginMm = await page.evaluate(() =>
    Number.parseFloat(
      getComputedStyle(document.documentElement)
        .getPropertyValue('--calendar-print-page-margin-mm')
        .trim(),
    ),
  )
  expect(Number.parseFloat(named.get('calendar-print')?.margin ?? '')).toBe(geometryMarginMm)

  // The interactive calendar page opts into no named page.
  await expect(page.locator('.calendar-page')).toHaveCSS('page', 'auto')
})

test('print: a 1-month calendar job is one landscape A4 PDF page', async ({ page }) => {
  await mockPrintApi(page)

  await page.goto('/calendar/print?month=2026-10')
  await expect(page.getByRole('heading', { level: 3, name: 'October 2026' })).toBeVisible()
  await expect(page.getByText('Lasagna')).toBeVisible()

  const pdf = summarizePdf(await page.pdf({ preferCSSPageSize: true }))
  expect(pdf.pages).toBe(1)
  expect(pdf.orientation).toBe('landscape')
  expect(pdf.widthPt).toBeCloseTo(A4_LONG_PT, 0)
  expect(pdf.heightPt).toBeCloseTo(A4_SHORT_PT, 0)
})

test('print: a 3-month calendar job is three landscape pages', async ({ page }) => {
  await mockPrintApi(page)

  await page.goto('/calendar/print?month=2026-10')
  await expect(page.getByRole('heading', { level: 3, name: 'October 2026' })).toBeVisible()

  await page.getByLabel('Months').fill('3')
  await expect(page.getByRole('heading', { level: 3, name: 'December 2026' })).toBeVisible()

  const pdf = summarizePdf(await page.pdf({ preferCSSPageSize: true }))
  expect(pdf.pages).toBe(3)
  expect(pdf.orientation).toBe('landscape')
  expect(pdf.widthPt).toBeCloseTo(A4_LONG_PT, 0)
})

test('print: a year-boundary multi-month job prints one page per month', async ({ page }) => {
  await mockPrintApi(page)

  await page.goto('/calendar/print?month=2026-11')
  await expect(page.getByRole('heading', { level: 3, name: 'November 2026' })).toBeVisible()

  await page.getByLabel('Months').fill('3')
  await expect(page.getByRole('heading', { level: 3, name: 'January 2027' })).toBeVisible()

  // The months roll across the year boundary in order, one heading per page.
  expect(await page.getByRole('heading', { level: 3 }).allTextContents()).toEqual([
    'November 2026',
    'December 2026',
    'January 2027',
  ])

  const pdf = summarizePdf(await page.pdf({ preferCSSPageSize: true }))
  expect(pdf.pages).toBe(3)
  expect(pdf.orientation).toBe('landscape')
})

test('print: the month count is bounded to twelve pages', async ({ page }) => {
  await mockPrintApi(page)

  await page.goto('/calendar/print?month=2026-10')
  await expect(page.getByRole('heading', { level: 3, name: 'October 2026' })).toBeVisible()

  // The control advertises and enforces the 1–12 bound, clamping an
  // out-of-range entry rather than resolving an unbounded list.
  const months = page.getByLabel('Months')
  await expect(months).toHaveAttribute('max', '12')
  await expect(months).toHaveAttribute('min', '1')
  await months.fill('99')
  await expect(months).toHaveValue('12')
  await expect(page.getByRole('heading', { level: 3 })).toHaveCount(12)

  const pdf = summarizePdf(await page.pdf({ preferCSSPageSize: true }))
  expect(pdf.pages).toBe(12)
  expect(pdf.orientation).toBe('landscape')
})

test('print: a past month prints and renders its stored data', async ({ page }) => {
  await mockPrintApi(page, { entries: PAST_ENTRIES })

  await page.goto('/calendar/print?month=2026-08')
  await expect(page.getByRole('heading', { level: 3, name: 'August 2026' })).toBeVisible()
  await expect(page.locator('.calendar-print-month').getByText('Zucchini boats')).toBeVisible()

  const pdf = summarizePdf(await page.pdf({ preferCSSPageSize: true }))
  expect(pdf.pages).toBe(1)
  expect(pdf.orientation).toBe('landscape')
})

test('print: the calendar print view is keyboard-operable with a sane heading order', async ({
  page,
}) => {
  await mockPrintApi(page)

  await page.goto('/calendar/print?month=2026-10')
  await expect(page.getByRole('heading', { level: 2, name: 'Print calendar' })).toBeVisible()

  // Sane heading order: the page h2 precedes the month h3s.
  const order = await page.evaluate(() =>
    Array.from(document.querySelectorAll('.calendar-print-page h2, .calendar-print-page h3')).map(
      (node) => `${node.tagName}:${node.textContent?.trim()}`,
    ),
  )
  expect(order[0]).toBe('H2:Print calendar')
  expect(order.slice(1)).toEqual(['H3:October 2026'])

  // The labelled month control is operable from the keyboard alone.
  const months = page.getByLabel('Months')
  await months.focus()
  await page.keyboard.press('ArrowUp')
  await page.keyboard.press('ArrowUp')
  await expect(months).toHaveValue('3')
  await expect(page.getByRole('heading', { level: 3, name: 'December 2026' })).toBeVisible()

  // The decorative print content stays out of the accessibility tree.
  await expect(page.locator('.calendar-print-month__icon')).toHaveAttribute('aria-hidden', 'true')
})

test('print: a busy month shrinks to fit without dropping below the legibility floor', async ({
  page,
}) => {
  const busyName = 'Slow-roasted butternut squash and sage brown butter'
  await mockPrintApi(page, { entries: filledMonthEntries(2026, 9, busyName) })

  await page.goto('/calendar/print?month=2026-10')
  await expect(page.getByRole('heading', { level: 3, name: 'October 2026' })).toBeVisible()
  await expect(page.getByText(busyName).first()).toBeVisible()

  // The month shrank to fit rather than spilling.
  await expect(page.locator('.calendar-print-month[data-spill="false"]')).toHaveCount(1)

  // The applied auto-shrink is real (< 1) but never below the ≈10pt floor.
  const applied = await readAppliedPrintScale(page)
  const floorScale = applied.floorFontPt / applied.baseFontPt
  expect(applied.spill).toBe(false)
  expect(applied.scale).toBeLessThan(1)
  expect(applied.scale).toBeGreaterThanOrEqual(floorScale - 1e-9)
  expect(applied.fontSizePx).toBeGreaterThanOrEqual(ptToPx(applied.floorFontPt) - 0.01)

  // Long names wrap fully: no ellipsis and no horizontally clipped overflow.
  const wrapped = await page
    .locator('.calendar-print-month__text')
    .first()
    .evaluate((node) => {
      const computed = getComputedStyle(node)
      return {
        whiteSpace: computed.whiteSpace,
        textOverflow: computed.textOverflow,
        clipped: node.scrollWidth > node.clientWidth + 1,
      }
    })
  expect(wrapped.whiteSpace).toBe('normal')
  expect(wrapped.textOverflow).not.toBe('ellipsis')
  expect(wrapped.clipped).toBe(false)

  const pdf = summarizePdf(await page.pdf({ preferCSSPageSize: true }))
  expect(pdf.pages).toBe(1)
  expect(pdf.orientation).toBe('landscape')
})

test('print: a pathological month spills to more pages instead of clipping', async ({ page }) => {
  const pathologicalName =
    'Impossibly long recipe name that keeps going and going with every conceivable ' +
    'ingredient and preparation detail spelled out at length so the cell cannot ' +
    'possibly fit on a single page without wrapping across many lines of text'
  await mockPrintApi(page, { entries: filledMonthEntries(2026, 9, pathologicalName) })

  await page.goto('/calendar/print?month=2026-10')
  await expect(page.getByRole('heading', { level: 3, name: 'October 2026' })).toBeVisible()
  await expect(page.getByText(pathologicalName).first()).toBeVisible()

  // The month reached the legibility floor and released the height cap.
  await expect(page.locator('.calendar-print-month[data-spill="true"]')).toHaveCount(1)

  // The full text is present and not truncated: no ellipsis, normal wrapping.
  const text = page.locator('.calendar-print-month__text').first()
  await expect(text).toHaveText(pathologicalName)
  const style = await text.evaluate((node) => {
    const computed = getComputedStyle(node)
    return {
      whiteSpace: computed.whiteSpace,
      textOverflow: computed.textOverflow,
      overflow: computed.overflow,
    }
  })
  expect(style.whiteSpace).toBe('normal')
  expect(style.textOverflow).not.toBe('ellipsis')
  expect(style.overflow).toBe('visible')

  // The month sits exactly at the ≈10pt legibility floor: it never shrinks
  // further, it spills instead.
  const applied = await readAppliedPrintScale(page)
  expect(applied.spill).toBe(true)
  expect(applied.scale).toBeCloseTo(applied.floorFontPt / applied.baseFontPt, 5)
  expect(applied.fontSizePx).toBeCloseTo(ptToPx(applied.floorFontPt), 1)

  // The content flowed onto another page rather than being clipped away.
  const pdf = summarizePdf(await page.pdf({ preferCSSPageSize: true }))
  expect(pdf.pages).toBeGreaterThan(1)
  expect(pdf.orientation).toBe('landscape')
})

test('print: the grocery print job is an A4 portrait PDF page', async ({ page }) => {
  await mockGroceryPrintApi(page)

  await page.goto('/groceries/print')
  await expect(page.getByRole('heading', { level: 2, name: /Grocery list/ })).toBeVisible()
  await expect(page.getByText('Onion')).toBeVisible()

  const pdf = summarizePdf(await page.pdf({ preferCSSPageSize: true }))
  expect(pdf.pages).toBe(1)
  expect(pdf.orientation).toBe('portrait')
  expect(pdf.widthPt).toBeCloseTo(A4_SHORT_PT, 0)
  expect(pdf.heightPt).toBeCloseTo(A4_LONG_PT, 0)
})

test('print: a non-print page keeps the default portrait paper', async ({ page }) => {
  await mockPrintApi(page)

  await page.goto('/calendar')
  await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()

  // No app-wide landscape rule: a normal page prints on the default portrait
  // paper, so the M5 global `@page { size: landscape }` has not returned.
  const pdf = summarizePdf(await page.pdf({ preferCSSPageSize: true }))
  expect(pdf.orientation).toBe('portrait')
})

test('print: dark mode still prints black on white', async ({ page }) => {
  await mockPrintApi(page)

  await page.goto('/calendar/print?month=2026-10')
  await expect(page.getByRole('heading', { level: 3, name: 'October 2026' })).toBeVisible()

  await page.emulateMedia({ media: 'print', colorScheme: 'dark' })

  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(255, 255, 255)')
  await expect(page.locator('body')).toHaveCSS('color', 'rgb(0, 0, 0)')
  await expect(page.locator('.calendar-print-month__heading')).toHaveCSS('color', 'rgb(0, 0, 0)')
  await expect(page.locator('.calendar-print-month__cell').first()).toHaveCSS(
    'color',
    'rgb(0, 0, 0)',
  )
  await expect(page.locator('.calendar-print-month__icon')).toHaveCSS('color', 'rgb(0, 0, 0)')

  // No background fills: headings and cells are transparent ink on white paper.
  await expect(page.locator('.calendar-print-month__heading')).toHaveCSS(
    'background-color',
    'rgba(0, 0, 0, 0)',
  )
  await expect(page.locator('.calendar-print-month__cell').first()).toHaveCSS(
    'background-color',
    'rgba(0, 0, 0, 0)',
  )
})

test('print: the Back link returns to the calendar', async ({ page }) => {
  await mockPrintApi(page)

  await page.goto('/calendar/print?month=2026-10')
  await expect(page.getByRole('heading', { level: 3, name: 'October 2026' })).toBeVisible()

  await page.getByRole('link', { name: 'Back to calendar' }).click()

  await expect(page).toHaveURL(/\/calendar$/)
  await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()
})
