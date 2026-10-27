/** Permission keys and presets for the member permission editor (docs/PRODUCT_SPEC.md §4). */

export const PERMISSION_KEYS = [
  'calendar.view',
  'calendar.edit',
  'groceries.view',
  'groceries.edit',
  'recipes.view',
  'recipes.edit',
  'reference_data.edit',
] as const

export type PermissionKey = (typeof PERMISSION_KEYS)[number]

export const PERMISSION_LABELS: Record<PermissionKey, string> = {
  'calendar.view': 'Calendar — View',
  'calendar.edit': 'Calendar — Edit',
  'groceries.view': 'Groceries — View',
  'groceries.edit': 'Groceries — Edit',
  'recipes.view': 'Recipes — View',
  'recipes.edit': 'Recipes — Edit',
  'reference_data.edit': 'Reference data — Edit',
}

export const VIEW_ONLY_PERMISSIONS: string[] = ['calendar.view', 'groceries.view', 'recipes.view']

/** Full member preset: view+edit for calendar/groceries/recipes plus reference data edit. */
export const FULL_MEMBER_PERMISSIONS: string[] = [
  'calendar.view',
  'calendar.edit',
  'groceries.view',
  'groceries.edit',
  'recipes.view',
  'recipes.edit',
  'reference_data.edit',
]

export type PermissionPreset = 'view' | 'full' | 'custom'

function sameSet(left: string[], right: string[]): boolean {
  return left.length === right.length && left.every((value) => right.includes(value))
}

export function presetFor(permissions: string[]): PermissionPreset {
  if (sameSet(permissions, VIEW_ONLY_PERMISSIONS)) {
    return 'view'
  }
  if (sameSet(permissions, FULL_MEMBER_PERMISSIONS)) {
    return 'full'
  }
  return 'custom'
}

export function permissionsSummary(permissions: string[], role: string): string {
  if (role === 'admin') {
    return 'All permissions (Admin)'
  }
  const preset = presetFor(permissions)
  if (preset === 'view') {
    return `View only (${permissions.length})`
  }
  if (preset === 'full') {
    return `Full member (${permissions.length})`
  }
  return `Custom (${permissions.length})`
}
