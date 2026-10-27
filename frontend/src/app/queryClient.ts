import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query'
import { ApiError, setExpectedUser } from '../api/client'
import type { CurrentUser, DemoSession } from '../api/types'
import { currentUserKey } from '../features/auth/auth'
import { demoSessionKey } from '../features/demo/demo'

/**
 * Server state lives in TanStack Query. If any request reports 401 (session expired or revoked),
 * we mark the user as signed out and the route guard sends them to the login page.
 */
export function createQueryClient(): QueryClient {
  const queryClient: QueryClient = new QueryClient({
    queryCache: new QueryCache({ onError: handleUnauthenticated }),
    mutationCache: new MutationCache({ onError: handleUnauthenticated }),
    defaultOptions: {
      queries: {
        staleTime: 15_000,
        // Retry network/server failures, but never client errors like 403/404 that will not change.
        retry: (failureCount, error) => !(error instanceof ApiError && error.status < 500) && failureCount < 2,
      },
    },
  })

  function handleUnauthenticated(error: Error) {
    if (!(error instanceof ApiError) || error.status !== 401) return
    // A demo that has ended (expired, reset in another tab, or ended) gets its own screen, not the sign-in page.
    if (error.code === 'demo_expired') {
      queryClient.setQueryData<DemoSession | null>(demoSessionKey, (previous) =>
        previous ? { ...previous, status: 'ended', session: null } : { status: 'ended', lifetimeMinutes: 60, session: null },
      )
    }
    if (queryClient.getQueryData(currentUserKey)) {
      queryClient.setQueryData(currentUserKey, null)
    }
  }

  // The API client checks this before retrying a write with a fresh token, so a tab never acts as a
  // different person than the one it shows (see apiRequest).
  queryClient.getQueryCache().subscribe((event) => {
    if (event.query.queryKey[0] === currentUserKey[0] && event.type === 'updated' && event.query.state.status === 'success') {
      setExpectedUser((event.query.state.data as CurrentUser | null)?.id ?? null)
    }
  })

  return queryClient
}
