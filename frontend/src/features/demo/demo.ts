import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { ApiError, apiRequest, refreshAntiforgeryToken } from '../../api/client'
import type { DemoPersonaKey, DemoSession, DemoSignIn } from '../../api/types'
import { announceSessionChange } from '../../app/sessionSync'
import { currentUserKey } from '../auth/auth'

export const demoSessionKey = ['demo-session'] as const

/**
 * This browser's demo status, or null when the server has no demo mode (the endpoint answers 404).
 * The demo credential is an HttpOnly cookie the browser sends only to /api/demo; JavaScript never sees it.
 */
export function useDemoSession({ enabled = true }: { enabled?: boolean } = {}) {
  return useQuery({
    enabled,
    queryKey: demoSessionKey,
    queryFn: async (): Promise<DemoSession | null> => {
      try {
        return await apiRequest<DemoSession>('/api/demo/session')
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) return null
        throw error
      }
    },
    staleTime: 30_000,
    retry: false,
  })
}

/** After any change of person: new antiforgery token, nothing cached from before, and other tabs told. */
function adoptSignIn(queryClient: QueryClient, result: DemoSignIn) {
  queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== currentUserKey[0] && query.queryKey[0] !== demoSessionKey[0] })
  queryClient.setQueryData(currentUserKey, result.user)
  queryClient.setQueryData(demoSessionKey, result.session)
  announceSessionChange()
}

function useDemoSignIn<TVariables>(path: string, body?: (variables: TVariables) => unknown) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (variables: TVariables) => {
      const result = await apiRequest<DemoSignIn>(path, { method: 'POST', body: body ? body(variables) : {} })
      // The antiforgery token is bound to the signed-in person, who just changed.
      await refreshAntiforgeryToken()
      return result
    },
    onSuccess: (result) => adoptSignIn(queryClient, result),
  })
}

/** "Try the demo": a new private sandbox, or this browser's current one if it is still active. */
export const useStartDemo = () => useDemoSignIn<void>('/api/demo/sessions')

/** Act as another of the demo's predefined people. Applies to every tab in this browser. */
export const useSwitchPersona = () => useDemoSignIn<DemoPersonaKey>('/api/demo/persona', (persona) => ({ persona }))

/** Replace this browser's demo data with a fresh copy, keeping the current person. */
export const useResetDemo = () => useDemoSignIn<void>('/api/demo/reset')

/** End the demo now: the server deletes the sandbox and signs this browser out. */
export function useEndDemo() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      await apiRequest<void>('/api/demo/end', { method: 'POST' })
      await refreshAntiforgeryToken()
    },
    onSettled: () => {
      queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== currentUserKey[0] })
      queryClient.setQueryData(currentUserKey, null)
      queryClient.setQueryData<DemoSession | null>(demoSessionKey, (previous) =>
        previous ? { ...previous, status: 'ended', session: null } : previous,
      )
      announceSessionChange()
    },
  })
}

/** A short, friendly explanation for the demo errors the server can return. */
export function demoErrorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Something went wrong. Please try again.'
  switch (error.code) {
    case 'demo_capacity_reached':
      return 'The demo is busy right now. Please try again in a few minutes.'
    case 'rate_limited':
      return 'Too many demo sessions were started from this network. Please wait a while and try again.'
    default:
      return error.message
  }
}
