import { describe, expect, it } from 'vitest'

import { formatQuantity } from './quantity'

describe('formatQuantity', () => {
  it('renders common decimals as fractions', () => {
    expect(formatQuantity(0.5)).toBe('1/2')
    expect(formatQuantity(1.5)).toBe('1 1/2')
    expect(formatQuantity(2.25)).toBe('2 1/4')
    expect(formatQuantity(0.25)).toBe('1/4')
    expect(formatQuantity(0.75)).toBe('3/4')
    expect(formatQuantity(0.125)).toBe('1/8')
    expect(formatQuantity(2.5)).toBe('2 1/2')
  })

  it('matches repeating thirds from precise stored decimals', () => {
    expect(formatQuantity(0.33333333333333)).toBe('1/3')
    expect(formatQuantity(1.66666666666667)).toBe('1 2/3')
  })

  it('leaves whole numbers unchanged', () => {
    expect(formatQuantity(0)).toBe('0')
    expect(formatQuantity(2)).toBe('2')
    expect(formatQuantity(10)).toBe('10')
    expect(formatQuantity(1.0)).toBe('1')
  })

  it('returns empty text for null, undefined, and empty input', () => {
    expect(formatQuantity(null)).toBe('')
    expect(formatQuantity(undefined)).toBe('')
    expect(formatQuantity('')).toBe('')
  })

  it('formats numeric strings and leaves non-fraction decimals as decimals', () => {
    expect(formatQuantity('1.5')).toBe('1 1/2')
    expect(formatQuantity(0.3)).toBe('0.3')
    expect(formatQuantity(1.125)).toBe('1 1/8')
  })

  it('passes through non-numeric strings unchanged', () => {
    expect(formatQuantity('to taste')).toBe('to taste')
  })
})
