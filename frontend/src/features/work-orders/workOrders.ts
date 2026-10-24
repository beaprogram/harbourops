import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { apiRequest } from '../../api/client'
import type {
  ActivityEvent,
  CreateWorkOrderInput,
  PagedResponse,
  PropertySummary,
  Technician,
  WorkOrderDetail,
  WorkOrderListItem,
  WorkOrderPriority,
  WorkOrderStatus,
} from '../../api/types'

export const workOrderKeys = {
  all: ['work-orders'] as const,
  list: (filters: { statuses: WorkOrderStatus[]; page: number; pageSize: number }) =>
    ['work-orders', 'list', filters] as const,
  detail: (id: string) => ['work-orders', 'detail', id] as const,
  activity: (id: string) => ['work-orders', 'activity', id] as const,
}

export function useWorkOrderList(filters: { statuses: WorkOrderStatus[]; page: number; pageSize: number }) {
  return useQuery({
    queryKey: workOrderKeys.list(filters),
    queryFn: () => {
      const params = new URLSearchParams({ page: String(filters.page), pageSize: String(filters.pageSize) })
      filters.statuses.forEach((status) => params.append('status', status))
      return apiRequest<PagedResponse<WorkOrderListItem>>(`/api/work-orders?${params.toString()}`)
    },
    placeholderData: keepPreviousData,
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
