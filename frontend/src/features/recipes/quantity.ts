/**
 * Formats stored decimal quantities as user-friendly cooking fractions for
 * display (docs/PRODUCT_SPEC.md §37). Decimals stay the storage/input format
 * this only affects presentation.
 */

/** Cooking fractions up to eighths, checked in ascending denominator order. */
const FRACTIONS: ReadonlyArray<readonly [number, number]> = [
  [1, 8],
  [1, 6],
  [1, 5],
  [1, 4],
  [1, 3],
  [3, 8],
  [2, 5],
  [1, 2],
  [3, 5],
  [5, 8],
  [2, 3],
  [3, 4],
  [4, 5],
  [5, 6],
  [7, 8],
]

const TOLERANCE = 1e-6

function matchFraction(value: number): readonly [number, number] | null {
  for (const candidate of FRACTIONS) {
    const [numerator, denominator] = candidate
    if (Math.abs(value - numerator / denominator) <= TOLERANCE) {
      return candidate
    }
  }
  return null
}

/** Trims float noise from a decimal fallback, e.g. `0.3` -> `"0.3"`. */
function trimDecimal(value: number): string {
  return Number(value.toFixed(6)).toString()
}

/**
 * Renders `0.5` -> `1/2`, `1.5` -> `1 1/2`, `2.25` -> `2 1/4`. Whole numbers
 * `null`, `undefined`, and empty strings are returned unchanged (empty strings
 * and nulls render as `''`). Non-fraction decimals are shown as decimals.
 */
export function formatQuantity(value: number | string | null | undefined): string {
  if (value === null || value === undefined || value === '') {
    return ''
  }

  const numeric = typeof value === 'number' ? value : Number(value)
  if (!Number.isFinite(numeric)) {
    return typeof value === 'string' ? value : ''
  }

  const negative = numeric < 0
  const absolute = Math.abs(numeric)
  let whole = Math.floor(absolute)
  let remainder = absolute - whole
  const sign = negative ? '-' : ''

  if (remainder > 1 - TOLERANCE) {
    whole += 1
    remainder = 0
  }

  if (remainder <= TOLERANCE) {
    return `${sign}${whole}`
  }

  const fraction = matchFraction(remainder)
  if (fraction === null) {
    return `${sign}${trimDecimal(absolute)}`
  }

  const [numerator, denominator] = fraction
  return whole === 0
    ? `${sign}${numerator}/${denominator}`
    : `${sign}${whole} ${numerator}/${denominator}`
}
