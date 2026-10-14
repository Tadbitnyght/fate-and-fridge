import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { describe, expect, it } from 'vitest'

import {
  MAX_PRINT_SCALE,
  MIN_PRINT_SCALE,
  PRINT_AVAILABLE_HEIGHT_PX,
  PRINT_BASE_FONT_PT,
  PRINT_FLOOR_FONT_PT,
  PRINT_GEOMETRY_FALLBACK,
  PRINT_GEOMETRY_VARS,
  computePrintScale,
  mmToPx,
  printAvailableHeightPx,
  printContentHeightMm,
  printContentWidthMm,
  printMinScale,
  readPrintGeometry,
  shouldSpillPrintMonth,
} from './printScale'

describe('print auto-shrink scale', () => {
  it('expresses the floor as a pt-equivalent font size', () => {
    expect(MIN_PRINT_SCALE).toBeCloseTo(PRINT_FLOOR_FONT_PT / PRINT_BASE_FONT_PT)
    expect(PRINT_BASE_FONT_PT * MIN_PRINT_SCALE).toBeCloseTo(PRINT_FLOOR_FONT_PT)
    expect(PRINT_FLOOR_FONT_PT).toBeCloseTo(10)
  })

  it('never enlarges a month that already fits', () => {
    expect(computePrintScale(100, 500)).toBe(MAX_PRINT_SCALE)
    expect(computePrintScale(500, 500)).toBe(MAX_PRINT_SCALE)
  })

  it('shrinks proportionally to fit the page', () => {
    expect(computePrintScale(1000, 900)).toBeCloseTo(0.9)
    expect(computePrintScale(800, 720)).toBeCloseTo(0.9)
  })

  it('never shrinks below the legibility floor', () => {
    const scale = computePrintScale(100_000, 500)
    expect(scale).toBe(MIN_PRINT_SCALE)
    expect(scale).toBeGreaterThanOrEqual(MIN_PRINT_SCALE)
  })

  it('is deterministic for the same content', () => {
    expect(computePrintScale(900, 600)).toBe(computePrintScale(900, 600))
  })

  it('does not shrink an unmeasurable month', () => {
    expect(computePrintScale(0, 500)).toBe(MAX_PRINT_SCALE)
    expect(computePrintScale(-10, 500)).toBe(MAX_PRINT_SCALE)
    expect(computePrintScale(Number.NaN, 500)).toBe(MAX_PRINT_SCALE)
    expect(computePrintScale(900, 0)).toBe(MAX_PRINT_SCALE)
  })

  it('spills only when the month cannot fit even at the floor', () => {
    const available = PRINT_AVAILABLE_HEIGHT_PX
    // Exactly fits at the floor: natural × floor == available.
    expect(shouldSpillPrintMonth(available / MIN_PRINT_SCALE, available)).toBe(false)
    // Needs more than the floor allows.
    expect(shouldSpillPrintMonth(available / MIN_PRINT_SCALE + 1, available)).toBe(true)
    expect(shouldSpillPrintMonth(available * 2, available)).toBe(true)
    // Nothing measured: never spill.
    expect(shouldSpillPrintMonth(0)).toBe(false)
    expect(shouldSpillPrintMonth(Number.NaN)).toBe(false)
  })

  it('converts millimetres to CSS pixels at the 96 dpi reference', () => {
    expect(mmToPx(25.4)).toBeCloseTo(96)
    expect(PRINT_AVAILABLE_HEIGHT_PX).toBeGreaterThan(0)
  })
})

/**
 * The page geometry has a single source of truth: the `:root` custom properties
 * in `index.css`, which the print rules and `printScale.ts` both read. This
 * guard fails if the fallback geometry, or the `@page calendar-print` margin,
 * drifts from the stylesheet.
 */
describe('print geometry single source of truth', () => {
  // Read the stylesheet directly: Vitest runs with `css: false`, so a CSS import
  // resolves to an empty module. `npm test` runs from the frontend package root.
  const indexCss = readFileSync(resolve(process.cwd(), 'src/index.css'), 'utf8')

  function cssNumber(name: string): number {
    const match = new RegExp(`${name}\\s*:\\s*([-+0-9.eE]+)\\s*;`).exec(indexCss)
    if (match === null) {
      throw new Error(`${name} is not declared in index.css`)
    }
    return Number.parseFloat(match[1])
  }

  it('mirrors every geometry custom property in the fallback', () => {
    expect(cssNumber(PRINT_GEOMETRY_VARS.pageWidthMm)).toBe(PRINT_GEOMETRY_FALLBACK.pageWidthMm)
    expect(cssNumber(PRINT_GEOMETRY_VARS.pageHeightMm)).toBe(PRINT_GEOMETRY_FALLBACK.pageHeightMm)
    expect(cssNumber(PRINT_GEOMETRY_VARS.pageMarginMm)).toBe(PRINT_GEOMETRY_FALLBACK.pageMarginMm)
    expect(cssNumber(PRINT_GEOMETRY_VARS.baseFontPt)).toBe(PRINT_GEOMETRY_FALLBACK.baseFontPt)
    expect(cssNumber(PRINT_GEOMETRY_VARS.floorFontPt)).toBe(PRINT_GEOMETRY_FALLBACK.floorFontPt)
    expect(cssNumber(PRINT_GEOMETRY_VARS.fitSafety)).toBe(PRINT_GEOMETRY_FALLBACK.fitSafety)
  })

  it('keeps the @page calendar-print margin in sync with the geometry', () => {
    const rule = /@page\s+calendar-print\s*\{([^}]*)\}/.exec(indexCss)
    expect(rule).not.toBeNull()
    const margin = /margin\s*:\s*([\d.]+)mm/.exec(rule![1])
    expect(margin).not.toBeNull()
    expect(Number.parseFloat(margin![1])).toBe(PRINT_GEOMETRY_FALLBACK.pageMarginMm)
  })

  it('derives the fit budget from the geometry', () => {
    expect(printContentWidthMm()).toBeCloseTo(277)
    expect(printContentHeightMm()).toBeCloseTo(190)
    expect(printAvailableHeightPx()).toBeCloseTo(mmToPx(190) * PRINT_GEOMETRY_FALLBACK.fitSafety)
    expect(printMinScale()).toBeCloseTo(MIN_PRINT_SCALE)
  })

  it('reads the geometry from CSS, falling back when it is unavailable', () => {
    // jsdom cannot resolve the stylesheet's custom properties, so the reader
    // returns the documented fallback rather than NaN.
    expect(readPrintGeometry(null)).toEqual(PRINT_GEOMETRY_FALLBACK)
  })
})
