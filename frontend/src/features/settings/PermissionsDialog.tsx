import { useState } from 'react'

import { isApiError } from '../../api/client'
import type { UserSummary } from '../../api/users'
import { Modal } from '../../components/Modal'
import {
  FULL_MEMBER_PERMISSIONS,
  PERMISSION_KEYS,
  PERMISSION_LABELS,
  VIEW_ONLY_PERMISSIONS,
  presetFor,
  type PermissionKey,
  type PermissionPreset,
} from './permissions'
import { useUpdateUserPermissions } from './useUsers'

const PRESET_LABELS: Record<Exclude<PermissionPreset, 'custom'>, string> = {
  view: 'View only',
  full: 'Full member',
}

export function PermissionsDialog({ user, onClose }: { user: UserSummary; onClose: () => void }) {
  const updatePermissions = useUpdateUserPermissions()
  const [selected, setSelected] = useState<string[]>(
    user.permissions.filter((permission) =>
      (PERMISSION_KEYS as readonly string[]).includes(permission),
    ),
  )
  const [error, setError] = useState<string | null>(null)

  const isAdmin = user.role === 'admin'
  const preset = presetFor(selected)

  function toggle(permission: PermissionKey, checked: boolean): void {
    setSelected((current) =>
      checked ? [...current, permission] : current.filter((value) => value !== permission),
    )
  }

  async function handleSave(): Promise<void> {
    setError(null)
    try {
      await updatePermissions.mutateAsync({ id: user.id, permissions: selected })
      onClose()
    } catch (caught) {
      setError(
        isApiError(caught) ? caught.message : 'Saving failed. Check the server and try again.',
      )
    }
  }

  return (
    <Modal title={`Permissions for ${user.username}`} onClose={onClose}>
      {isAdmin ? (
        <p>Admins always hold every permission; this account cannot be edited here.</p>
      ) : (
        <>
          <fieldset className="form__fieldset">
            <legend>Presets</legend>
            {(['view', 'full'] as const).map((value) => (
              <label className="form__choice" key={value}>
                <input
                  type="radio"
                  name="permission-preset"
                  value={value}
                  checked={preset === value}
                  onChange={() =>
                    setSelected(
                      value === 'view' ? [...VIEW_ONLY_PERMISSIONS] : [...FULL_MEMBER_PERMISSIONS],
                    )
                  }
                />
                <span>{PRESET_LABELS[value]}</span>
              </label>
            ))}
            <p className="form__hint">
              {preset === 'custom' ? 'Custom selection' : PRESET_LABELS[preset]}
            </p>
          </fieldset>

          <fieldset className="form__fieldset">
            <legend>Permissions</legend>
            {PERMISSION_KEYS.map((permission) => (
              <label className="form__choice" key={permission}>
                <input
                  type="checkbox"
                  checked={selected.includes(permission)}
                  onChange={(event) => toggle(permission, event.target.checked)}
                />
                <span>{PERMISSION_LABELS[permission]}</span>
              </label>
            ))}
            <p className="form__hint">
              Edit implies view: granting an edit key also grants its view.
            </p>
          </fieldset>
        </>
      )}

      {error && (
        <p className="form__error" role="alert">
          {error}
        </p>
      )}

      <div className="modal__actions">
        <button type="button" onClick={onClose} disabled={updatePermissions.isPending}>
          {isAdmin ? 'Close' : 'Cancel'}
        </button>
        {!isAdmin && (
          <button
            type="button"
            className="button--primary"
            onClick={() => void handleSave()}
            disabled={updatePermissions.isPending}
          >
            {updatePermissions.isPending ? 'Saving…' : 'Save permissions'}
          </button>
        )}
      </div>
    </Modal>
  )
}
