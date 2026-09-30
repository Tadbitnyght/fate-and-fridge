import { describe, expect, it } from 'vitest'

import { extractServerFieldErrors } from '../../utils/fieldErrors'
import { validateAllowlistRange, validateAllowlistText } from '../../utils/ipRanges'
import {
  buildRequest,
  clearFieldError,
  clearStepErrors,
  stepForFieldErrors,
  validateStep,
  wizardSteps,
  type SetupFormValues,
  type ValidationContext,
} from './validation'

const context: ValidationContext = {
  timezoneOptions: ['UTC', 'America/Chicago', 'Europe/Berlin'],
}

function values(overrides: Partial<SetupFormValues> = {}): SetupFormValues {
  return {
    authMode: 'multiuser',
    householdName: 'The Smiths',
    timezone: 'America/Chicago',
    weekStartsOn: 0,
    defaultDinerCount: '4',
    startupPage: 'calendar',
    adminUsername: 'smith',
    adminPassword: 'correct horse battery staple',
    adminPasswordConfirm: 'correct horse battery staple',
    allowlistEnabled: false,
    allowlistRangesText: '',
    updateCheckEnabled: true,
    ...overrides,
  }
}

describe('wizard steps', () => {
  it('includes the admin step only for multiuser', () => {
    expect(wizardSteps('multiuser')).toEqual(['mode', 'household', 'admin', 'allowlist', 'review'])
    expect(wizardSteps('single_user')).toEqual(['mode', 'household', 'allowlist', 'review'])
    expect(wizardSteps(null)).not.toContain('admin')
  })
})

describe('mode validation', () => {
  it('requires an explicit installation mode', () => {
    expect(validateStep('mode', values({ authMode: null }), context)).toEqual({
      auth_mode: 'Choose an installation mode.',
    })
    expect(validateStep('mode', values(), context)).toEqual({})
  })
})

describe('household validation', () => {
  it('requires a non-empty name without control characters', () => {
    expect(validateStep('household', values({ householdName: '   ' }), context)).toHaveProperty(
      'household_name',
    )
    expect(
      validateStep('household', values({ householdName: 'Bad\u0000Name' }), context),
    ).toHaveProperty('household_name')
    expect(
      validateStep('household', values({ householdName: 'x'.repeat(101) }), context),
    ).toHaveProperty('household_name')
  })

  it('accepts known timezones case-insensitively and rejects unknown ones', () => {
    expect(
      validateStep('household', values({ timezone: 'america/chicago' }), context),
    ).not.toHaveProperty('timezone')
    expect(validateStep('household', values({ timezone: 'Mars/Olympus' }), context)).toHaveProperty(
      'timezone',
    )
    expect(validateStep('household', values({ timezone: '' }), context)).toHaveProperty('timezone')
    // Without an enumerable list only emptiness is checked (text input fallback).
    expect(
      validateStep('household', values({ timezone: 'Mars/Olympus' }), {
        timezoneOptions: null,
      }),
    ).not.toHaveProperty('timezone')
  })

  it('bounds the diner count, week start, and startup page', () => {
    expect(validateStep('household', values({ defaultDinerCount: '0' }), context)).toHaveProperty(
      'default_diner_count',
    )
    expect(validateStep('household', values({ defaultDinerCount: '21' }), context)).toHaveProperty(
      'default_diner_count',
    )
    expect(validateStep('household', values({ defaultDinerCount: '4.5' }), context)).toHaveProperty(
      'default_diner_count',
    )
    expect(
      validateStep('household', values({ defaultDinerCount: '1' }), context),
    ).not.toHaveProperty('default_diner_count')
    expect(
      validateStep('household', values({ defaultDinerCount: '20' }), context),
    ).not.toHaveProperty('default_diner_count')
    expect(validateStep('household', values({ weekStartsOn: 7 }), context)).toHaveProperty(
      'week_starts_on',
    )
  })
})

describe('admin validation', () => {
  it('is skipped in single-user mode', () => {
    expect(
      validateStep(
        'admin',
        values({ authMode: 'single_user', adminUsername: '', adminPassword: '' }),
        context,
      ),
    ).toEqual({})
  })

  it('mirrors the username and password policy', () => {
    expect(validateStep('admin', values({ adminUsername: 'ab' }), context)).toHaveProperty(
      'admin.username',
    )
    expect(validateStep('admin', values({ adminUsername: 'bad user!' }), context)).toHaveProperty(
      'admin.username',
    )
    expect(
      validateStep('admin', values({ adminUsername: 'a'.repeat(33) }), context),
    ).toHaveProperty('admin.username')
    expect(
      validateStep('admin', values({ adminUsername: 'okay.name-1_2' }), context),
    ).not.toHaveProperty('admin.username')
    expect(validateStep('admin', values({ adminPassword: 'short' }), context)).toHaveProperty(
      'admin.password',
    )
    expect(
      validateStep('admin', values({ adminPasswordConfirm: 'something else entirely' }), context),
    ).toHaveProperty('admin.password_confirm')
  })
})

describe('allowlist validation', () => {
  it('requires at least one range when enabled', () => {
    expect(
      validateStep(
        'allowlist',
        values({ allowlistEnabled: true, allowlistRangesText: '\n\n' }),
        context,
      ),
    ).toHaveProperty('ip_allowlist.ranges')
    expect(validateStep('allowlist', values({ allowlistEnabled: false }), context)).toEqual({})
  })

  it('validates each line with a line number', () => {
    const result = validateAllowlistText('10.0.0.0/8\nnot-an-ip\n::ffff:10.0.0.1/80')
    expect(result.ranges).toEqual(['10.0.0.0/8'])
    expect(result.errors).toHaveLength(2)
    expect(result.errors[0]).toContain('Line 2')
    expect(result.errors[1]).toContain('Line 3')
  })

  it('accepts IPv4/IPv6 addresses and CIDRs', () => {
    for (const range of [
      '10.0.0.1',
      '192.168.1.0/24',
      '0.0.0.0/0',
      '::1',
      '2001:db8::/32',
      '::ffff:10.0.0.0/96',
      '0:0:0:0:0:ffff:1.2.3.4/96',
      'fe80::1',
    ]) {
      expect(validateAllowlistRange(range), range).toBeUndefined()
    }
  })

  it('rejects malformed or unsafe ranges', () => {
    for (const range of [
      '10',
      '10.0.0.1/33',
      '010.0.0.1',
      '192.168.1.01/24',
      '1.2.3.4/032',
      '::ffff:10.0.0.0/95',
      '::ffff:0:0/80',
      '0:0:0:0:0:ffff:1.2.3.4/90',
      '::ffff:1.2.3.4/95',
      '2001:db8::/129',
      'fe80::1%eth0',
      'not-an-ip',
      '0x7f000001',
      '10.0.0.1/aa',
    ]) {
      expect(validateAllowlistRange(range), range).toBeDefined()
    }
  })
})

describe('field error mapping', () => {
  it('finds the first step that owns an error', () => {
    expect(stepForFieldErrors({ 'admin.username': 'taken' })).toBe('admin')
    expect(stepForFieldErrors({ 'ip_allowlist.ranges': 'bad' })).toBe('allowlist')
    expect(stepForFieldErrors({ household_name: 'required' })).toBe('household')
    expect(stepForFieldErrors({ body: 'bad' })).toBe('review')
  })

  it('extracts the first message per field from the server envelope', () => {
    expect(
      extractServerFieldErrors({
        field_errors: {
          'admin.username': ['first message', 'second message'],
          'ip_allowlist.ranges': ['range message'],
        },
      }),
    ).toEqual({
      'admin.username': 'first message',
      'ip_allowlist.ranges': 'range message',
    })
    expect(extractServerFieldErrors({})).toEqual({})
  })

  it('clears only the errors that belong to the edited field', () => {
    const cleared = clearFieldError('adminUsername', {
      'admin.username': 'taken',
      'admin.password': 'short',
      timezone: 'bad',
    })
    expect(cleared).toEqual({ 'admin.password': 'short', timezone: 'bad' })
  })

  it('clears only the errors owned by a step', () => {
    expect(
      clearStepErrors('admin', {
        'admin.username': 'taken',
        'ip_allowlist.ranges': 'bad',
      }),
    ).toEqual({ 'ip_allowlist.ranges': 'bad' })
  })
})

describe('buildRequest', () => {
  it('builds a multiuser payload with admin credentials', () => {
    expect(buildRequest(values())).toEqual({
      household_name: 'The Smiths',
      timezone: 'America/Chicago',
      week_starts_on: 0,
      default_diner_count: 4,
      startup_page: 'calendar',
      auth_mode: 'multiuser',
      admin: { username: 'smith', password: 'correct horse battery staple' },
      ip_allowlist: { enabled: false, ranges: [] },
      update_check_enabled: true,
    })
  })

  it('omits admin for single-user and trims allowlist lines', () => {
    const request = buildRequest(
      values({
        authMode: 'single_user',
        adminUsername: 'ignored',
        allowlistEnabled: true,
        allowlistRangesText: ' 10.0.0.0/8 \n\n192.168.1.5\n',
      }),
    )
    expect(request.auth_mode).toBe('single_user')
    expect('admin' in request).toBe(false)
    expect(request.ip_allowlist).toEqual({
      enabled: true,
      ranges: ['10.0.0.0/8', '192.168.1.5'],
    })
  })

  it('refuses to build a request without a mode', () => {
    expect(() => buildRequest(values({ authMode: null }))).toThrow(/installation mode/i)
  })
})
