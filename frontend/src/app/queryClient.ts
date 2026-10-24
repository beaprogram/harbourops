import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query'
import { ApiError } from '../api/client'
import { currentUserKey } from '../features/auth/auth'

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
    if (error instanceof ApiError && error.status === 401 && queryClient.getQueryData(currentUserKey)) {
      queryClient.setQueryData(currentUserKey, null)
    }
  }

  return queryClient
}
