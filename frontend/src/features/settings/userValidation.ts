import type { FieldErrors } from '../../utils/fieldErrors'
import { validateAdminPassword, validateAdminUsername } from '../../utils/validation'

/** Client mirror of the server username/password policy for user dialogs. */
export function validateUserForm(values: {
  username: string
  password: string
  requirePassword: boolean
}): FieldErrors {
  const errors: FieldErrors = {}
  const username = validateAdminUsername(values.username)
  if (username) {
    errors['username'] = username
  }
  if (values.requirePassword || values.password.length > 0) {
    const password = validateAdminPassword(values.password)
    if (password) {
      errors['password'] = password
    }
  }
  return errors
}
