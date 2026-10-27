import type { WorkOrderPriority, WorkOrderStatus } from '../../api/types'

/**
 * The work-order list's search, filters, sort, and page, stored in the URL so they survive refresh,
 * back navigation, and sharing a link. The URL parameter names are the API's parameter names, so a
 * dashboard link produced by the server opens exactly the list it counted.
 */

export type SortField = 'created' | 'updated' | 'due' | 'priority'
export type SortDirection = 'asc' | 'desc'
export type DeadlineFilter = 'overdue' | 'dueSoon'

export interface WorkOrderListFilters {
  q: string
  statuses: WorkOrderStatus[]
  priorities: WorkOrderPriority[]
  propertyId: string | null
  technicianId: string | null
  unassigned: boolean
  deadline: DeadlineFilter | null
  sort: SortField
  direction: SortDirection
  page: number
}

export const ALL_STATUSES: WorkOrderStatus[] = ['New', 'Assigned', 'InProgress', 'Resolved', 'Closed']
export const ACTIVE_STATUSES: WorkOrderStatus[] = ['New', 'Assigned', 'InProgress']
const PRIORITIES: WorkOrderPriority[] = ['High', 'Normal']
const SORT_FIELDS: SortField[] = ['created', 'updated', 'due', 'priority']
/** Must match WorkOrderSearch.MaxLength on the server. */
export const SEARCH_MAX_LENGTH = 100

/** Mirrors the server: due dates sort soonest-first by default, everything else newest/highest first. */
export function defaultDirection(sort: SortField): SortDirection {
  return sort === 'due' ? 'asc' : 'desc'
}

export const DEFAULT_FILTERS: WorkOrderListFilters = {
  q: '',
  statuses: [],
  priorities: [],
  propertyId: null,
  technicianId: null,
  unassigned: false,
  deadline: null,
  sort: 'created',
  direction: 'desc',
  page: 1,
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/**
 * Reads filters from the URL. Unknown or malformed values are dropped rather than sent to the API,
 * so a hand-edited URL shows a working list instead of an error. The server still validates everything.
 */
export function parseListFilters(params: URLSearchParams): WorkOrderListFilters {
  const pick = <T extends string>(values: string[], allowed: readonly T[]) =>
    [...new Set(values.filter((v): v is T => (allowed as readonly string[]).includes(v)))]
  const id = (value: string | null) => (value && GUID.test(value) ? value : null)

  const sortParam = params.get('sort')
  const sort = SORT_FIELDS.find((s) => s === sortParam) ?? 'created'
  const directionParam = params.get('direction')
  const direction = directionParam === 'asc' || directionParam === 'desc' ? directionParam : defaultDirection(sort)
  const deadlineParam = params.get('deadline')
  const page = Number.parseInt(params.get('page') ?? '1', 10)
  const technicianId = id(params.get('technicianId'))

  return {
    q: (params.get('q') ?? '').slice(0, SEARCH_MAX_LENGTH),
    statuses: pick(params.getAll('status'), ALL_STATUSES),
    priorities: pick(params.getAll('priority'), PRIORITIES),
    propertyId: id(params.get('propertyId')),
    technicianId,
    // The API rejects both together; a technician choice wins.
    unassigned: params.get('unassigned') === 'true' && technicianId === null,
    deadline: deadlineParam === 'overdue' || deadlineParam === 'dueSoon' ? deadlineParam : null,
    sort,
    direction,
    page: Number.isFinite(page) && page > 0 ? Math.min(page, 10_000) : 1,
  }
}

/** Builds the URL / API query. Defaults are omitted so URLs stay short and readable. */
export function toSearchParams(filters: WorkOrderListFilters): URLSearchParams {
  const params = new URLSearchParams()
  if (filters.q.trim()) params.set('q', filters.q)
  filters.statuses.forEach((status) => params.append('status', status))
  filters.priorities.forEach((priority) => params.append('priority', priority))
  if (filters.propertyId) params.set('propertyId', filters.propertyId)
  if (filters.technicianId) params.set('technicianId', filters.technicianId)
  if (filters.unassigned) params.set('unassigned', 'true')
  if (filters.deadline) params.set('deadline', filters.deadline)
  if (filters.sort !== 'created' || filters.direction !== 'desc') {
    params.set('sort', filters.sort)
    params.set('direction', filters.direction)
  }
  if (filters.page > 1) params.set('page', String(filters.page))
  return params
}

/** True when anything narrows the results (sorting and paging don't count). */
export function hasActiveFilters(filters: WorkOrderListFilters): boolean {
  return (
    filters.q.trim() !== '' ||
    filters.statuses.length > 0 ||
    filters.priorities.length > 0 ||
    filters.propertyId !== null ||
    filters.technicianId !== null ||
    filters.unassigned ||
    filters.deadline !== null
  )
}

/** Clears search and filters but keeps the chosen sort order. */
export function clearFilters(filters: WorkOrderListFilters): WorkOrderListFilters {
  return { ...DEFAULT_FILTERS, sort: filters.sort, direction: filters.direction }
}
