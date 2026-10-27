import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, apiRequest, refreshAntiforgeryToken } from '../../api/client'
import type { CurrentUser, DemoAccounts } from '../../api/types'
import { announceSessionChange } from '../../app/sessionSync'

export const currentUserKey = ['current-user'] as const

/** The signed-in user according to the server, or null when signed out. */
export function useCurrentUser() {
  return useQuery({
    queryKey: currentUserKey,
    queryFn: async (): Promise<CurrentUser | null> => {
      try {
        return await apiRequest<CurrentUser>('/api/auth/me')
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) return null
        throw error
      }
    },
    staleTime: 5 * 60_000,
  })
}

export function useLogin() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (credentials: { email: string; password: string }) => {
      const user = await apiRequest<CurrentUser>('/api/auth/login', { method: 'POST', body: credentials })
      // The antiforgery token is bound to the user identity, so get a new one for the signed-in user.
      await refreshAntiforgeryToken()
      return user
    },
    onSuccess: (user) => {
      queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== currentUserKey[0] })
      queryClient.setQueryData(currentUserKey, user)
      announceSessionChange()
    },
  })
}

export function useLogout() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      await apiRequest<void>('/api/auth/logout', { method: 'POST' })
      await refreshAntiforgeryToken()
    },
    onSettled: () => {
      // Drop every cached record from the previous session.
      queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== currentUserKey[0] })
      queryClient.setQueryData(currentUserKey, null)
      announceSessionChange()
    },
  })
}

/** Demo accounts are only available when the server runs in demo mode; otherwise this resolves to null. */
export function useDemoAccounts() {
  return useQuery({
    queryKey: ['demo-accounts'],
    queryFn: async (): Promise<DemoAccounts | null> => {
      try {
        return await apiRequest<DemoAccounts>('/api/demo/accounts')
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) return null
        throw error
      }
    },
    staleTime: Infinity,
    retry: false,
  })
}
