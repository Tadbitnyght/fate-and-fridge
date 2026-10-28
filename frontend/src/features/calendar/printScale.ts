/**
 * Deterministic auto-shrink for the printed calendar.
 * Mechanism. The printable month is laid out at the **print content width** in
 * both screen and print media, and every dimension inside it is `em`-based, so
 * its rendered height is proportional to the month's `font-size`. We measure the
 * whole month's natural (unscaled) height once — heading included, so the
 * month/year heading is part of the fit budget — derive
 * `scale = availableHeight / naturalHeight`, and apply that scale as a
 * font-size multiplier (`--calendar-print-scale`) on the month. Scaling the
 * font size reflows the layout (unlike a `transform`), so the browser's
 * pagination sees the shrunk height and keeps the month on one page.
 * This is **deterministic**: the scale is a pure function of the measured
 * content height and the fixed page geometry, so identical content always
 * produces the identical scale (no timers, randomness, or user-visible seeds).
 * The scale is clamped to `MIN_PRINT_SCALE` so the month's text never prints
 * smaller than `PRINT_FLOOR_FONT_PT` (≈10pt). When even the floor scale cannot
 * fit the content, `shouldSpillPrintMonth` reports true and the view leaves the
 * month unconstrained (`data-spill="true"`) so it continues onto a following
 * page instead of shrinking further or clipping.
 * Single source of truth. The page geometry lives in `index.css` as CSS custom
 * properties on `:root` (`--calendar-print-*`); `index.css` derives the month
 * width and the print height cap from them, and this module reads the same
 * properties at runtime via `readPrintGeometry`. `PRINT_GEOMETRY_FALLBACK`
 * mirrors them only for environments that cannot resolve custom properties
 * (jsdom in unit tests); `printScale.test.ts` asserts the fallback and the
 * `@page` margin match `index.css`, and `e2e/print.spec.ts` asserts the
 * `@page` margin matches, so CSS and JS cannot drift.
 */

/**
 * Numeric print geometry. Each value is a plain number in the unit named by the
 * property (`mm`, `pt`, or a unitless factor).
 */
export interface PrintGeometry {
  /** Physical page width in mm (the calendar's named page is A4 landscape). */
  pageWidthMm: number
  /** Physical page height in mm. */
  pageHeightMm: number
  /** Page margin in mm; must match the `@page calendar-print` margin. */
  pageMarginMm: number
  /** Month font size in pt at scale 1 (the largest the calendar prints). */
  baseFontPt: number
  /** Legibility floor in pt: no printed calendar text is smaller than this. */
  floorFontPt: number
  /** Fit-budget safety allowance so a "fitting" month does not trip a break. */
  fitSafety: number
}

/** CSS custom properties carrying the print geometry (declared in `index.css`). */
export const PRINT_GEOMETRY_VARS: Record<keyof PrintGeometry, string> = {
  pageWidthMm: '--calendar-print-page-width-mm',
  pageHeightMm: '--calendar-print-page-height-mm',
  pageMarginMm: '--calendar-print-page-margin-mm',
  baseFontPt: '--calendar-print-base-font-pt',
  floorFontPt: '--calendar-print-floor-font-pt',
  fitSafety: '--calendar-print-fit-safety',
}

/**
 * Fallback geometry, mirroring the `:root` custom properties in `index.css`.
 * Used only when the custom properties cannot be read (jsdom in unit tests).
 * `printScale.test.ts` asserts it matches `index.css`, so the two cannot drift.
 */
export const PRINT_GEOMETRY_FALLBACK: PrintGeometry = {
  pageWidthMm: 297,
  pageHeightMm: 210,
  pageMarginMm: 10,
  baseFontPt: 12,
  floorFontPt: 10,
  fitSafety: 0.98,
}

/** CSS reference resolution: 96 px per inch. */
export const CSS_PX_PER_MM = 96 / 25.4

/** Converts millimetres to CSS pixels at the 96 dpi reference. */
export function mmToPx(mm: number): number {
  return mm * CSS_PX_PER_MM
}

function readNumber(style: CSSStyleDeclaration, name: string, fallback: number): number {
  const raw = style.getPropertyValue(name).trim()
  if (raw.length === 0) {
    return fallback
  }
  const value = Number.parseFloat(raw)
  return Number.isFinite(value) ? value : fallback
}

/**
 * Reads the print geometry from the CSS custom properties, falling back to
 * `PRINT_GEOMETRY_FALLBACK` per property when the value is absent or not a
 * number (jsdom, or a stylesheet that failed to load).
 */
export function readPrintGeometry(
  element?: Element | null,
  fallback: PrintGeometry = PRINT_GEOMETRY_FALLBACK,
): PrintGeometry {
  if (typeof window === 'undefined' || typeof window.getComputedStyle !== 'function') {
    return fallback
  }
  const target = element ?? (typeof document === 'undefined' ? null : document.documentElement)
  if (target === null) {
    return fallback
  }
  const style = window.getComputedStyle(target)
  return {
    pageWidthMm: readNumber(style, PRINT_GEOMETRY_VARS.pageWidthMm, fallback.pageWidthMm),
    pageHeightMm: readNumber(style, PRINT_GEOMETRY_VARS.pageHeightMm, fallback.pageHeightMm),
    pageMarginMm: readNumber(style, PRINT_GEOMETRY_VARS.pageMarginMm, fallback.pageMarginMm),
    baseFontPt: readNumber(style, PRINT_GEOMETRY_VARS.baseFontPt, fallback.baseFontPt),
    floorFontPt: readNumber(style, PRINT_GEOMETRY_VARS.floorFontPt, fallback.floorFontPt),
    fitSafety: readNumber(style, PRINT_GEOMETRY_VARS.fitSafety, fallback.fitSafety),
  }
}

/** Printable content width in mm (page width minus the left/right margins). */
export function printContentWidthMm(geometry: PrintGeometry = PRINT_GEOMETRY_FALLBACK): number {
  return geometry.pageWidthMm - geometry.pageMarginMm * 2
}

/** Printable content height in mm (page height minus the top/bottom margins). */
export function printContentHeightMm(geometry: PrintGeometry = PRINT_GEOMETRY_FALLBACK): number {
  return geometry.pageHeightMm - geometry.pageMarginMm * 2
}

/** Printable height per landscape page, in CSS pixels, with the safety allowance. */
export function printAvailableHeightPx(geometry: PrintGeometry = PRINT_GEOMETRY_FALLBACK): number {
  return mmToPx(printContentHeightMm(geometry)) * geometry.fitSafety
}

/** Smallest allowed scale; keeps the month font at the legibility floor. */
export function printMinScale(geometry: PrintGeometry = PRINT_GEOMETRY_FALLBACK): number {
  return geometry.floorFontPt / geometry.baseFontPt
}

/* Fallback-geometry constants (pure math and unit-test convenience). ----- */

/** Month font size in pt at scale 1 (the largest the calendar prints). */
export const PRINT_BASE_FONT_PT = PRINT_GEOMETRY_FALLBACK.baseFontPt
/** Legibility floor: no printed calendar text is smaller than this. */
export const PRINT_FLOOR_FONT_PT = PRINT_GEOMETRY_FALLBACK.floorFontPt
/** Smallest allowed scale; keeps the month font at the floor (10 / 12 ≈ 0.833). */
export const MIN_PRINT_SCALE = printMinScale()
/** Largest allowed scale; a month is never blown up above its base size. */
export const MAX_PRINT_SCALE = 1
/** A4 landscape height in mm (the calendar page size). */
export const LANDSCAPE_PAGE_HEIGHT_MM = PRINT_GEOMETRY_FALLBACK.pageHeightMm
/** Page margin in mm; mirrors `--calendar-print-page-margin-mm` in `index.css`. */
export const PRINT_PAGE_MARGIN_MM = PRINT_GEOMETRY_FALLBACK.pageMarginMm
/** Printable content height in mm (page height minus the top/bottom margins). */
export const PRINT_CONTENT_HEIGHT_MM = printContentHeightMm()
/**
 * Safety allowance so a month scaled to "fit" does not trip a fragmentation
 * break on a rounding difference (e.g. a row that lands 1px over the edge).
 */
export const PRINT_FIT_SAFETY = PRINT_GEOMETRY_FALLBACK.fitSafety
/** Printable height per landscape page, in CSS pixels, with the safety allowance. */
export const PRINT_AVAILABLE_HEIGHT_PX = printAvailableHeightPx()

/**
 * The font-size multiplier that shrinks `naturalHeightPx` to fit
 * `availableHeightPx`, clamped to `[MIN_PRINT_SCALE, MAX_PRINT_SCALE]`.
 * A non-positive or non-finite measurement (e.g. jsdom, which lays nothing out)
 * yields `MAX_PRINT_SCALE`, i.e. no shrink.
 */
export function computePrintScale(
  naturalHeightPx: number,
  availableHeightPx: number = PRINT_AVAILABLE_HEIGHT_PX,
): number {
  if (!Number.isFinite(naturalHeightPx) || naturalHeightPx <= 0) {
    return MAX_PRINT_SCALE
  }
  if (!Number.isFinite(availableHeightPx) || availableHeightPx <= 0) {
    return MAX_PRINT_SCALE
  }
  const raw = availableHeightPx / naturalHeightPx
  return Math.min(MAX_PRINT_SCALE, Math.max(MIN_PRINT_SCALE, raw))
}

/**
 * True when the month cannot fit on one page even at the legibility floor. The
 * view then releases any height constraint so the content flows onto the next
 * page (never shrinking below the floor, never clipping).
 */
export function shouldSpillPrintMonth(
  naturalHeightPx: number,
  availableHeightPx: number = PRINT_AVAILABLE_HEIGHT_PX,
): boolean {
  if (!Number.isFinite(naturalHeightPx) || naturalHeightPx <= 0) {
    return false
  }
  return naturalHeightPx * MIN_PRINT_SCALE > availableHeightPx
}
