import { useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { UserRole } from '../../api/auth'
import type { UserSummary } from '../../api/users'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { Modal } from '../../components/Modal'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { validateUserForm } from './userValidation'
import { useCreateUser, useUpdateUser } from './useUsers'

/**
 * Create (password optional → one-time temporary password) and edit
 * (username/role) dialogs for household users (docs/API.md §6).
 */
export function UserFormDialog({
  mode,
  user,
  onClose,
  onSaved,
}: {
  mode: 'create' | 'edit'
  user?: UserSummary
  onClose: () => void
  onSaved: (result: UserSummary) => void
}) {
  const createUser = useCreateUser()
  const updateUser = useUpdateUser()

  const [username, setUsername] = useState(user?.username ?? '')
  const [role, setRole] = useState<UserRole>(user?.role ?? 'member')
  const [password, setPassword] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const pending = createUser.isPending || updateUser.isPending

  const demoting = mode === 'edit' && user?.role === 'admin' && role === 'member'

  function clearError(field: string): void {
    setErrors((current) => {
      if (!(field in current)) {
        return current
      }
      const next = { ...current }
      delete next[field]
      return next
    })
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)
    const clientErrors = validateUserForm({
      username,
      password,
      requirePassword: false,
    })
    if (Object.keys(clientErrors).length > 0) {
      setErrors(clientErrors)
      return
    }

    try {
      if (mode === 'create') {
        const created = await createUser.mutateAsync({
          username: username.trim(),
          role,
          ...(password.length > 0 ? { password } : {}),
        })
        onSaved(created)
      } else if (user) {
        const updated = await updateUser.mutateAsync({
          id: user.id,
          request: { username: username.trim(), role },
        })
        onSaved(updated)
      }
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.ValidationFailed) {
        const serverErrors = extractServerFieldErrors(error.details)
        if (Object.keys(serverErrors).length > 0) {
          setErrors(serverErrors)
          return
        }
      }
      setErrors({})
      setFormError(
        isApiError(error) ? error.message : 'Saving failed. Check the connection and try again.',
      )
    }
  }

  return (
    <Modal
      title={mode === 'create' ? 'Add user' : `Edit ${user?.username ?? 'user'}`}
      onClose={onClose}
    >
      {formError && (
        <p className="form__error" role="alert">
          {formError}
        </p>
      )}

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <Field
          id="user-username"
          label="Username"
          error={errors['username']}
          hint="3–32 characters: letters, digits, “.”, “_” or “-”."
        >
          <input
            type="text"
            autoComplete="off"
            spellCheck={false}
            {...fieldProps('user-username', { error: errors['username'], hint: true })}
            value={username}
            onChange={(event) => {
              setUsername(event.target.value)
              clearError('username')
            }}
          />
        </Field>

        <Field id="user-role" label="Role">
          <select
            {...fieldProps('user-role')}
            value={role}
            onChange={(event) => setRole(event.target.value as UserRole)}
          >
            <option value="member">Member</option>
            <option value="admin">Admin</option>
          </select>
        </Field>

        {mode === 'create' && (
          <Field
            id="user-password"
            label="Password (optional)"
            error={errors['password']}
            hint="Leave blank to generate a one-time temporary password. 12–128 characters."
          >
            <input
              type="password"
              autoComplete="new-password"
              {...fieldProps('user-password', { error: errors['password'], hint: true })}
              value={password}
              onChange={(event) => {
                setPassword(event.target.value)
                clearError('password')
              }}
            />
          </Field>
        )}

        {demoting && (
          <div className="notice notice--warning" role="note">
            <p>
              Changing an Admin to Member revokes their sessions. They keep existing permission
              grants; view-only defaults are seeded only when they have none.
            </p>
          </div>
        )}

        <div className="modal__actions">
          <button type="button" onClick={onClose} disabled={pending}>
            Cancel
          </button>
          <button type="submit" className="button--primary" disabled={pending}>
            {pending ? 'Saving…' : mode === 'create' ? 'Add user' : 'Save changes'}
          </button>
        </div>
      </form>
    </Modal>
  )
}
