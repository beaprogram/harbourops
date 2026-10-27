import { useQuery } from '@tanstack/react-query'
import { apiRequest } from '../../api/client'
import type { Dashboard } from '../../api/types'

export function useDashboard() {
  return useQuery({
    queryKey: ['dashboard'],
    queryFn: () => apiRequest<Dashboard>('/api/dashboard'),
    // Deadlines move with time, so refresh a quiet dashboard every minute.
    refetchInterval: 60_000,
  })
}
