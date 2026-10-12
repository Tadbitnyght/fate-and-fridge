import type {
  GroceryDepartment,
  GroceryItem,
  GroceryPreferences,
  GroceryRange,
  GroceryStaple,
} from '../api/groceries'
import { setupCompleteStatus } from './fixtures'
import { installMockFetch, type MockFetch, type MockResponse, type MockRoute } from './mockFetch'

/**
 * Stateful grocery API mock for the M8-07 UI tests. It mirrors the wire
 * contract in `docs/API.md §13` closely enough to drive check/uncheck,
 * preferences, manual items, staples, and add-to-list flows: each mutation
 * updates the mocked server state so the following list read reflects it, and
 * list reads re-apply the server's unchecked-then-checked ordering.
 */

export const GROCERY_TODAY = '2026-10-12'
export const GROCERY_RANGE: GroceryRange = {
  preset: 'next_7_days',
  start: GROCERY_TODAY,
  end: '2026-10-18',
  day_count: 7,
}

/** Builds a realistic 64-char lowercase hex identity from a repeat character. */
export function hexIdentity(fill: string): string {
  return fill.repeat(64).slice(0, 64)
}

export function manualIdentity(id: number): string {
  return id.toString(16).padStart(64, '0')
}

export function groceryItem(overrides: Partial<GroceryItem> & { identity: string }): GroceryItem {
  return {
    name: 'Item',
    quantity_text: '',
    quantity: null,
    unit: null,
    ingredient_id: null,
    department_id: null,
    checked: false,
    source_type: 'recipe',
    manual_item_id: null,
    sources: [],
    ...overrides,
  }
}

export function defaultPreferences(): GroceryPreferences {
  return { hide_checked: false, range_preset: 'next_7_days', range_start: null, range_days: null }
}

export function defaultStaples(): GroceryStaple[] {
  return [
    {
      id: 3,
      name: 'Milk',
      ingredient_id: 42,
      department_id: 1,
      default_quantity_numeric: 1,
      unit_id: 7,
      is_active: true,
      created_at: '2026-10-10T00:00:00+00:00',
      updated_at: '2026-10-10T00:00:00+00:00',
    },
  ]
}

export function defaultDepartments(): GroceryDepartment[] {
  return [
    {
      department_id: 1,
      name: 'Produce',
      sort_order: 1,
      is_other: false,
      items: [
        groceryItem({
          identity: hexIdentity('a'),
          name: 'Onion',
          quantity_text: '2',
          quantity: 2,
          ingredient_id: 42,
          department_id: 1,
          sources: [{ recipe_id: 5, recipe_name: 'Tacos', date: '2026-10-13' }],
        }),
        groceryItem({
          identity: hexIdentity('b'),
          name: 'Carrot',
          checked: true,
          department_id: 1,
        }),
      ],
    },
    {
      department_id: 2,
      name: 'Bakery',
      sort_order: 2,
      is_other: false,
      items: [groceryItem({ identity: hexIdentity('c'), name: 'Bread', department_id: 2 })],
    },
    {
      department_id: null,
      name: 'Other',
      sort_order: 2147483647,
      is_other: true,
      items: [
        groceryItem({
          identity: manualIdentity(5),
          name: 'Paper towels',
          source_type: 'manual',
          manual_item_id: 5,
          quantity_text: '2',
          quantity: 2,
        }),
      ],
    },
  ]
}

const REFERENCE_DEPARTMENTS = [
  { id: 1, name: 'Produce', sort_order: 1, is_active: true, created_at: '', updated_at: '' },
  { id: 2, name: 'Bakery', sort_order: 2, is_active: true, created_at: '', updated_at: '' },
]

const REFERENCE_UNITS = [
  {
    id: 7,
    symbol: 'lb',
    display_name: 'Pound',
    dimension: 'mass',
    base_factor: 453.592,
    is_custom: false,
    is_active: true,
    aliases: [],
  },
]

const REFERENCE_INGREDIENTS = [
  {
    id: 42,
    canonical_name: 'Milk',
    department_id: 1,
    is_active: true,
    created_at: '',
    updated_at: '',
    aliases: [],
  },
]

export interface GroceryMockOptions {
  session?: unknown
  preferences?: GroceryPreferences
  departments?: GroceryDepartment[]
  staples?: GroceryStaple[]
  /** Force the preferences `PATCH` to fail (e.g. a range error). */
  preferencesPatchError?: MockResponse
  /** Force `POST /groceries/check` to fail (e.g. a 403). */
  checkError?: MockResponse
  /** Force `DELETE /groceries/manual/{id}` to fail (e.g. a 404). */
  manualDeleteError?: MockResponse
  /** Force `PATCH /groceries/staples/{id}` to fail. */
  staplePatchError?: MockResponse
}

export interface GroceryMockState {
  departments: GroceryDepartment[]
  preferences: GroceryPreferences
  staples: GroceryStaple[]
  requests: Array<{ method: string; path: string; body?: Record<string, unknown> }>
  manualCounter: number
  stapleCounter: number
}

export interface GroceryMockHandle extends MockFetch {
  state: GroceryMockState
}

function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

function requestBody(init: RequestInit): Record<string, unknown> {
  if (typeof init.body !== 'string' || init.body.length === 0) {
    return {}
  }
  return JSON.parse(init.body) as Record<string, unknown>
}

/** Unchecked first, then checked; alphabetical by name within each group. */
function orderItems(items: GroceryItem[]): GroceryItem[] {
  return [...items].sort((left, right) => {
    if (left.checked !== right.checked) {
      return left.checked ? 1 : -1
    }
    return left.name.localeCompare(right.name)
  })
}

const PRESET_DAYS: Record<string, number> = {
  next_3_days: 3,
  next_7_days: 7,
  next_14_days: 14,
  rest_of_month: 15,
}

function resolveRange(preferences: GroceryPreferences): GroceryRange {
  const preset = preferences.range_preset
  if (preset === 'custom' && preferences.range_start !== null && preferences.range_days !== null) {
    const start = new Date(`${preferences.range_start}T00:00:00`)
    const end = new Date(start)
    end.setDate(end.getDate() + preferences.range_days - 1)
    const iso = (date: Date): string =>
      `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(
        date.getDate(),
      ).padStart(2, '0')}`
    return {
      preset: 'custom',
      start: preferences.range_start,
      end: iso(end),
      day_count: preferences.range_days,
    }
  }
  return {
    ...GROCERY_RANGE,
    preset: preset !== null && preset !== 'custom' ? preset : 'next_7_days',
    day_count: PRESET_DAYS[preset ?? 'next_7_days'] ?? 7,
  }
}

function findItem(departments: GroceryDepartment[], identity: string): GroceryItem | undefined {
  for (const department of departments) {
    const item = department.items.find((row) => row.identity === identity)
    if (item) {
      return item
    }
  }
  return undefined
}

function departmentFor(
  departments: GroceryDepartment[],
  departmentId: number | null,
): GroceryDepartment {
  return (
    departments.find((row) => row.department_id === departmentId) ??
    departments[departments.length - 1]
  )
}

function unitSymbol(unitId: number | null): string | null {
  if (unitId === null) {
    return null
  }
  return REFERENCE_UNITS.find((unit) => unit.id === unitId)?.symbol ?? null
}

function unitIdForSymbol(symbol: string | null): number | null {
  if (symbol === null) {
    return null
  }
  return REFERENCE_UNITS.find((unit) => unit.symbol === symbol)?.id ?? null
}

function departmentContaining(
  departments: GroceryDepartment[],
  identity: string,
): GroceryDepartment | undefined {
  return departments.find((department) => department.items.some((row) => row.identity === identity))
}

function addManualItem(
  state: GroceryMockState,
  fields: {
    name: string
    quantity: number | null
    unit_id: number | null
    ingredient_id: number | null
    department_id: number | null
    range_start: string
    range_days: number
    save_for_future: boolean
  },
): Record<string, unknown> {
  const id = state.manualCounter++
  const identity = manualIdentity(id)
  const department = departmentFor(state.departments, fields.department_id)
  department.items.push(
    groceryItem({
      identity,
      name: fields.name,
      quantity_text: fields.quantity !== null ? String(fields.quantity) : '',
      quantity: fields.quantity,
      unit: unitSymbol(fields.unit_id),
      ingredient_id: fields.ingredient_id,
      department_id: department.department_id,
      source_type: 'manual',
      manual_item_id: id,
    }),
  )
  return {
    id,
    name: fields.name,
    quantity_numeric: fields.quantity,
    unit_id: fields.unit_id,
    ingredient_id: fields.ingredient_id,
    department_id: department.department_id,
    range_start: fields.range_start,
    range_days: fields.range_days,
    save_for_future: fields.save_for_future,
    created_at: '2026-10-12T00:00:00+00:00',
    updated_at: '2026-10-12T00:00:00+00:00',
  }
}

export function installGroceryMocks(options: GroceryMockOptions = {}): GroceryMockHandle {
  const state: GroceryMockState = {
    departments: clone(options.departments ?? defaultDepartments()),
    preferences: clone(options.preferences ?? defaultPreferences()),
    staples: clone(options.staples ?? defaultStaples()),
    requests: [],
    manualCounter: 100,
    stapleCounter: 10,
  }

  const record = (method: string, path: string, init: RequestInit): Record<string, unknown> => {
    const body = init.body === undefined ? undefined : requestBody(init)
    state.requests.push(body === undefined ? { method, path } : { method, path, body })
    return body ?? {}
  }

  const session = options.session ?? undefined
  const routes: MockRoute[] = [
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: session },
    { path: '/health/db', body: { status: 'ok', database: 'connected' } },
    { path: '/health', body: { status: 'ok' } },
    {
      path: '/groceries/preferences',
      method: 'GET',
      respond: () => ({ body: state.preferences }),
    },
    {
      path: '/groceries/preferences',
      method: 'PATCH',
      respond: (init) => {
        const body = record('PATCH', '/groceries/preferences', init)
        if (options.preferencesPatchError) {
          return options.preferencesPatchError
        }
        // The server caps a custom range at 366 days (docs/M8_PLAN.md §1.7);
        // mirror that so range-boundary tests exercise the real error surface.
        if (
          body['range_preset'] === 'custom' &&
          typeof body['range_days'] === 'number' &&
          body['range_days'] > 366
        ) {
          return {
            status: 400,
            body: {
              error: {
                code: 'GROCERY_RANGE_TOO_LARGE',
                message: 'The range is too long.',
                details: {},
              },
            },
          }
        }
        if (typeof body['hide_checked'] === 'boolean') {
          state.preferences.hide_checked = body['hide_checked']
        }
        if ('range_preset' in body) {
          const preset = body['range_preset'] as GroceryPreferences['range_preset']
          state.preferences.range_preset = preset
          if (preset === 'custom') {
            state.preferences.range_start = (body['range_start'] as string | null) ?? null
            state.preferences.range_days = (body['range_days'] as number | null) ?? null
          } else {
            state.preferences.range_start = null
            state.preferences.range_days = null
          }
        }
        return { body: state.preferences }
      },
    },
    {
      path: '/groceries/staples/',
      method: 'POST',
      respond: (init, _count, url) => {
        record('POST', '/groceries/staples/add-to-list', init)
        const match = /\/staples\/(\d+)\/add-to-list/.exec(url)
        const id = match ? Number(match[1]) : 0
        const staple = state.staples.find((row) => row.id === id)
        if (!staple || !staple.is_active) {
          return { status: 404, body: { error: { code: 'NOT_FOUND', message: 'missing' } } }
        }
        const body = requestBody(init)
        const manual = addManualItem(state, {
          name: staple.name,
          quantity: staple.default_quantity_numeric,
          unit_id: staple.unit_id,
          ingredient_id: staple.ingredient_id,
          department_id: staple.department_id,
          range_start: String(body['range_start']),
          range_days: Number(body['range_days']),
          save_for_future: false,
        })
        return { status: 201, body: manual }
      },
    },
    {
      path: '/groceries/staples',
      method: 'PATCH',
      respond: (init, _count, url) => {
        const body = record('PATCH', '/groceries/staples', init)
        if (options.staplePatchError) {
          return options.staplePatchError
        }
        const match = /\/staples\/(\d+)/.exec(url)
        const staple = state.staples.find((row) => row.id === (match ? Number(match[1]) : 0))
        if (!staple) {
          return { status: 404, body: { error: { code: 'NOT_FOUND', message: 'missing' } } }
        }
        if (typeof body['name'] === 'string') {
          staple.name = body['name']
        }
        if ('ingredient_id' in body) {
          staple.ingredient_id = (body['ingredient_id'] as number | null) ?? null
        }
        if ('department_id' in body) {
          staple.department_id = (body['department_id'] as number | null) ?? null
        }
        if ('default_quantity_numeric' in body) {
          staple.default_quantity_numeric =
            (body['default_quantity_numeric'] as number | null) ?? null
        }
        if ('unit_id' in body) {
          staple.unit_id = (body['unit_id'] as number | null) ?? null
        }
        if (typeof body['is_active'] === 'boolean') {
          staple.is_active = body['is_active']
        }
        return { body: staple }
      },
    },
    {
      path: '/groceries/staples',
      method: 'GET',
      respond: (_init, _count, url) => ({
        body: {
          staples: url.includes('include_inactive=true')
            ? state.staples
            : state.staples.filter((row) => row.is_active),
        },
      }),
    },
    {
      path: '/groceries/staples',
      method: 'POST',
      respond: (init) => {
        const body = record('POST', '/groceries/staples', init)
        const name = String(body['name'] ?? '')
        const duplicate = state.staples.find(
          (row) => row.is_active && row.name.toLowerCase() === name.toLowerCase(),
        )
        if (duplicate) {
          return {
            status: 409,
            body: {
              error: {
                code: 'GROCERY_STAPLE_EXISTS',
                message: 'duplicate',
                details: { existing_id: duplicate.id },
              },
            },
          }
        }
        const staple: GroceryStaple = {
          id: state.stapleCounter++,
          name,
          ingredient_id: (body['ingredient_id'] as number | null) ?? null,
          department_id: (body['department_id'] as number | null) ?? null,
          default_quantity_numeric: (body['default_quantity_numeric'] as number | null) ?? null,
          unit_id: (body['unit_id'] as number | null) ?? null,
          is_active: true,
          created_at: '2026-10-12T00:00:00+00:00',
          updated_at: '2026-10-12T00:00:00+00:00',
        }
        state.staples.push(staple)
        return { status: 201, body: staple }
      },
    },
    {
      path: '/groceries/manual',
      method: 'POST',
      respond: (init) => {
        const body = record('POST', '/groceries/manual', init)
        const manual = addManualItem(state, {
          name: String(body['name']),
          quantity: (body['quantity_numeric'] as number | null) ?? null,
          unit_id: (body['unit_id'] as number | null) ?? null,
          ingredient_id: (body['ingredient_id'] as number | null) ?? null,
          department_id: (body['department_id'] as number | null) ?? null,
          range_start: String(body['range_start']),
          range_days: Number(body['range_days']),
          save_for_future: body['save_for_future'] === true,
        })
        return { status: 201, body: manual }
      },
    },
    {
      path: '/groceries/manual/',
      method: 'PATCH',
      respond: (init, _count, url) => {
        const body = record('PATCH', '/groceries/manual', init)
        const match = /\/manual\/(\d+)/.exec(url)
        const id = match ? Number(match[1]) : 0
        const identity = manualIdentity(id)
        let department = departmentContaining(state.departments, identity)
        const item = department?.items.find((row) => row.identity === identity)
        if (!item || !department) {
          return { status: 404, body: { error: { code: 'NOT_FOUND', message: 'missing' } } }
        }
        if (typeof body['name'] === 'string') {
          item.name = body['name']
        }
        if ('quantity_numeric' in body) {
          const quantity = (body['quantity_numeric'] as number | null) ?? null
          item.quantity = quantity
          item.quantity_text = quantity !== null ? String(quantity) : ''
        }
        if ('unit_id' in body) {
          item.unit = unitSymbol((body['unit_id'] as number | null) ?? null)
        }
        if ('ingredient_id' in body) {
          item.ingredient_id = (body['ingredient_id'] as number | null) ?? null
        }
        if ('department_id' in body) {
          const target = departmentFor(
            state.departments,
            (body['department_id'] as number | null) ?? null,
          )
          if (target !== department) {
            department.items = department.items.filter((row) => row.identity !== identity)
            target.items.push(item)
            department = target
          }
          item.department_id = department.department_id
        }
        return {
          body: {
            id,
            name: item.name,
            quantity_numeric: item.quantity,
            unit_id: unitIdForSymbol(item.unit),
            ingredient_id: item.ingredient_id,
            department_id: item.department_id,
            range_start: GROCERY_TODAY,
            range_days: 7,
            save_for_future: false,
            created_at: '2026-10-12T00:00:00+00:00',
            updated_at: '2026-10-12T00:00:00+00:00',
          },
        }
      },
    },
    {
      path: '/groceries/manual/',
      method: 'DELETE',
      respond: (init, _count, url) => {
        record('DELETE', '/groceries/manual', init)
        if (options.manualDeleteError) {
          return options.manualDeleteError
        }
        const match = /\/manual\/(\d+)/.exec(url)
        const identity = manualIdentity(match ? Number(match[1]) : 0)
        for (const department of state.departments) {
          department.items = department.items.filter((row) => row.identity !== identity)
        }
        return { status: 204, body: null }
      },
    },
    {
      path: '/groceries/check',
      method: 'POST',
      respond: (init) => {
        const body = record('POST', '/groceries/check', init)
        if (options.checkError) {
          return options.checkError
        }
        const identity = String(body['identity'])
        const item = findItem(state.departments, identity)
        if (item) {
          item.checked = true
        }
        return {
          body: {
            identity,
            checked: true,
            checked_at: '2026-10-12T00:00:00+00:00',
            checked_by_user_id: 1,
          },
        }
      },
    },
    {
      path: '/groceries/uncheck',
      method: 'POST',
      respond: (init) => {
        const body = record('POST', '/groceries/uncheck', init)
        const identity = String(body['identity'])
        const item = findItem(state.departments, identity)
        if (item) {
          item.checked = false
        }
        return { body: { identity, checked: false, checked_at: null, checked_by_user_id: 1 } }
      },
    },
    { path: '/departments', body: REFERENCE_DEPARTMENTS },
    { path: '/units', body: REFERENCE_UNITS },
    { path: '/ingredients', body: REFERENCE_INGREDIENTS },
    {
      path: '/groceries',
      method: 'GET',
      respond: () => ({
        body: {
          range: resolveRange(state.preferences),
          departments: state.departments.map((department) => ({
            ...department,
            items: orderItems(department.items),
          })),
        },
      }),
    },
  ]

  const mock = installMockFetch(routes)
  return { ...mock, state }
}
