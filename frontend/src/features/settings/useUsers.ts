import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  createUser,
  disableUser,
  enableUser,
  fetchUsers,
  resetUserPassword,
  updateUser,
  updateUserPermissions,
  type CreateUserRequest,
  type UpdateUserRequest,
  type UserSummary,
} from '../../api/users'

export const usersQueryKey = ['users'] as const

export function useUsers(enabled = true) {
  return useQuery<UserSummary[]>({
    queryKey: usersQueryKey,
    queryFn: fetchUsers,
    retry: false,
    enabled,
  })
}

function useRefreshUsers() {
  const queryClient = useQueryClient()
  return async () => {
    await queryClient.invalidateQueries({ queryKey: usersQueryKey })
  }
}
export function useCreateUser() {
  const refresh = useRefreshUsers()
  return useMutation({
    mutationFn: (request: CreateUserRequest) => createUser(request),
    onSuccess: refresh,
  })
}

export function useUpdateUser() {
  const refresh = useRefreshUsers()
  return useMutation({
    mutationFn: ({ id, request }: { id: number; request: UpdateUserRequest }) =>
      updateUser(id, request),
    onSuccess: refresh,
  })
}

export function useUpdateUserPermissions() {
  const refresh = useRefreshUsers()
  return useMutation({
    mutationFn: ({ id, permissions }: { id: number; permissions: string[] }) =>
      updateUserPermissions(id, permissions),
    onSuccess: refresh,
  })
}

export function useDisableUser() {
  const refresh = useRefreshUsers()
  return useMutation({ mutationFn: (id: number) => disableUser(id), onSuccess: refresh })
}

export function useEnableUser() {
  const refresh = useRefreshUsers()
  return useMutation({ mutationFn: (id: number) => enableUser(id), onSuccess: refresh })
}

export function useResetUserPassword() {
  const refresh = useRefreshUsers()
  return useMutation({
    mutationFn: (id: number) => resetUserPassword(id),
    // Refetch so the "Must change password" tag appears immediately.
    onSuccess: refresh,
  })
}
