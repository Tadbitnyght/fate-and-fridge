import type { UnitDimension } from '../../api/referenceData'

export const UNIT_DIMENSIONS: Array<{ value: UnitDimension; label: string }> = [
  { value: 'mass', label: 'Mass' },
  { value: 'volume', label: 'Volume' },
  { value: 'count', label: 'Count' },
  { value: 'nonconvertible', label: 'Non-convertible' },
]

export const UNIT_DIMENSION_LABELS: Record<UnitDimension, string> = {
  mass: 'Mass',
  volume: 'Volume',
  count: 'Count',
  nonconvertible: 'Non-convertible',
}

export const FACTOR_HINT =
  'Positive number between 0.000001 and 1000000 (1e-6 … 1e6), relative to grams or millilitres.'

const FACTOR_MIN = 1e-6
const FACTOR_MAX = 1e6

/** Mass and volume units convert to the base unit; count and non-convertible units do not. */
export function requiresFactor(dimension: UnitDimension): boolean {
  return dimension === 'mass' || dimension === 'volume'
}

/** Parses the base-factor text input and applies the server's documented bounds. */
export function parseBaseFactorInput(value: string): { factor?: number; error?: string } {
  const trimmed = value.trim()
  if (trimmed.length === 0) {
    return { error: 'Enter a base factor for mass and volume units.' }
  }
  const factor = Number(trimmed)
  if (!Number.isFinite(factor)) {
    return { error: 'Enter a number for the base factor.' }
  }
  if (factor < FACTOR_MIN || factor > FACTOR_MAX) {
    return { error: 'Base factor must be between 0.000001 and 1000000.' }
  }
  return { factor }
}
