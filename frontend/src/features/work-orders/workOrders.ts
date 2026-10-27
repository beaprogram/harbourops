import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { apiRequest } from '../../api/client'
import type {
  ActivityEvent,
  CreateWorkOrderInput,
  PagedResponse,
  PropertySummary,
  Technician,
  WorkOrderDetail,
  WorkOrderFilterOptions,
  WorkOrderListItem,
  WorkOrderPriority,
} from '../../api/types'
import { toSearchParams, type WorkOrderListFilters } from './listFilters'

export const workOrderKeys = {
  all: ['work-orders'] as const,
  list: (query: string) => ['work-orders', 'list', query] as const,
  filterOptions: ['work-orders', 'filter-options'] as const,
  detail: (id: string) => ['work-orders', 'detail', id] as const,
  activity: (id: string) => ['work-orders', 'activity', id] as const,
}

/** The API query for a list view: the URL filters plus the page size. */
export function listQueryString(filters: WorkOrderListFilters, pageSize: number): string {
  const params = toSearchParams(filters)
  params.set('pageSize', String(pageSize))
  return params.toString()
}

export function useWorkOrderList(filters: WorkOrderListFilters, pageSize: number) {
  const query = listQueryString(filters, pageSize)
  return useQuery({
    queryKey: workOrderKeys.list(query),
    queryFn: () => apiRequest<PagedResponse<WorkOrderListItem>>(`/api/work-orders?${query}`),
    // Keep showing the previous results while a new filter loads, instead of flashing a spinner.
    placeholderData: keepPreviousData,
  })
}

export function useFilterOptions() {
  return useQuery({
    queryKey: workOrderKeys.filterOptions,
    queryFn: () => apiRequest<WorkOrderFilterOptions>('/api/work-orders/filter-options'),
    staleTime: 60_000,
  })
}

export function useWorkOrder(id: string) {
  return useQuery({
    queryKey: workOrderKeys.detail(id),
    queryFn: () => apiRequest<WorkOrderDetail>(`/api/work-orders/${id}`),
  })
}

export function useWorkOrderActivity(id: string) {
  return useQuery({
    queryKey: workOrderKeys.activity(id),
    queryFn: () => apiRequest<ActivityEvent[]>(`/api/work-orders/${id}/activity`),
  })
}

export function useProperties() {
  return useQuery({
    queryKey: ['properties'],
    queryFn: () => apiRequest<PropertySummary[]>('/api/properties'),
    staleTime: 5 * 60_000,
  })
}

export function useTechnicians(enabled: boolean) {
  return useQuery({
    queryKey: ['technicians'],
    queryFn: () => apiRequest<Technician[]>('/api/technicians'),
    enabled,
  })
}

export function useCreateWorkOrder() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: CreateWorkOrderInput) =>
      apiRequest<WorkOrderDetail>('/api/work-orders', { method: 'POST', body: input }),
    onSuccess: (workOrder) => {
      queryClient.setQueryData(workOrderKeys.detail(workOrder.id), workOrder)
      void queryClient.invalidateQueries({ queryKey: [...workOrderKeys.all, 'list'] })
    },
  })
}

/** One request shape per workflow command; every command carries the version the user is looking at. */
export type WorkOrderCommand =
  | { action: 'priority'; priority: WorkOrderPriority }
  | { action: 'assign'; technicianId: string }
  | { action: 'start' }
  | { action: 'resolve'; resolutionNote: string }
  | { action: 'close' }
  | { action: 'reopen'; reason: string }

export function useWorkOrderCommand(workOrder: WorkOrderDetail) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ action, ...fields }: WorkOrderCommand) =>
      apiRequest<WorkOrderDetail>(`/api/work-orders/${workOrder.id}/${action}`, {
        method: 'POST',
        body: { ...fields, version: workOrder.version },
      }),
    onSuccess: (updated) => {
      queryClient.setQueryData(workOrderKeys.detail(updated.id), updated)
      void queryClient.invalidateQueries({ queryKey: workOrderKeys.activity(updated.id) })
      void queryClient.invalidateQueries({ queryKey: [...workOrderKeys.all, 'list'] })
      void queryClient.invalidateQueries({ queryKey: ['technicians'] })
    },
  })
}
