import { describe, expect, it } from 'vitest'

import { DEFAULT_RETURN_TO, sanitizeReturnTo } from './redirect'

describe('sanitizeReturnTo', () => {
  it('accepts internal paths with search and hash', () => {
    expect(sanitizeReturnTo('/recipes')).toBe('/recipes')
    expect(sanitizeReturnTo('/recipes?sort=name#top')).toBe('/recipes?sort=name#top')
    expect(sanitizeReturnTo('/settings/users/3')).toBe('/settings/users/3')
  })

  it('falls back to / for missing or non-string values', () => {
    expect(sanitizeReturnTo(undefined)).toBe(DEFAULT_RETURN_TO)
    expect(sanitizeReturnTo(null)).toBe(DEFAULT_RETURN_TO)
    expect(sanitizeReturnTo('')).toBe(DEFAULT_RETURN_TO)
    expect(sanitizeReturnTo(42)).toBe(DEFAULT_RETURN_TO)
    expect(sanitizeReturnTo({ from: '/recipes' })).toBe(DEFAULT_RETURN_TO)
  })

  it('rejects relative paths that do not start with a slash', () => {
    expect(sanitizeReturnTo('recipes')).toBe(DEFAULT_RETURN_TO)
    expect(sanitizeReturnTo('https://evil.com')).toBe(DEFAULT_RETURN_TO)
    expect(sanitizeReturnTo('javascript:alert(1)')).toBe(DEFAULT_RETURN_TO)
  })

  it('rejects protocol-relative and backslash variants', () => {
    expect(sanitizeReturnTo('//evil.com')).toBe(DEFAULT_RETURN_TO)
    expect(sanitizeReturnTo('//evil.com/recipes')).toBe(DEFAULT_RETURN_TO)
    expect(sanitizeReturnTo('/\\evil.com')).toBe(DEFAULT_RETURN_TO)
    expect(sanitizeReturnTo('/\\/evil.com')).toBe(DEFAULT_RETURN_TO)
    expect(sanitizeReturnTo('///evil.com')).toBe(DEFAULT_RETURN_TO)
  })
})
