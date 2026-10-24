// TypeScript mirrors of the API contracts (backend/HarbourOps.Api/Features/*/…Contracts.cs).
// Dates arrive as ISO 8601 strings in UTC.

export type UserRole = 'Manager' | 'Technician' | 'Requester'

export type WorkOrderStatus = 'New' | 'Assigned' | 'InProgress' | 'Resolved' | 'Closed'
export type WorkOrderPriority = 'Normal' | 'High'
export type WorkOrderCategory = 'Plumbing' | 'Electrical' | 'Heating' | 'Appliances' | 'Exterior' | 'General'
export type WorkOrderAction = 'SetPriority' | 'Assign' | 'Start' | 'Resolve' | 'Close' | 'Reopen'

export type AuditEventType =
  | 'Created'
  | 'PriorityChanged'
  | 'Assigned'
  | 'Reassigned'
  | 'Started'
  | 'Resolved'
  | 'Reopened'
  | 'Closed'

export interface CurrentUser {
  id: string
  email: string
  displayName: string
  role: UserRole
  organization: { id: string; name: string }
}

export interface PersonSummary {
  id: string
  displayName: string
}

export interface PropertySummary {
  id: string
  name: string
  addressLine: string
  community: string
}

export interface Technician {
  id: string
  displayName: string
  openAssignments: number
}

export interface WorkOrderListItem {
  id: string
  number: number
  title: string
  status: WorkOrderStatus
  priority: WorkOrderPriority
  category: WorkOrderCategory
  propertyName: string
  requesterName: string
  assignedTechnicianName: string | null
  createdAt: string
  updatedAt: string
  dueAt: string
  isOverdue: boolean
}

export interface PagedResponse<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

export interface WorkOrderDetail {
  id: string
  number: number
  title: string
  description: string
  category: WorkOrderCategory
  priority: WorkOrderPriority
  status: WorkOrderStatus
  property: PropertySummary
  requester: PersonSummary
  assignedTechnician: PersonSummary | null
  createdAt: string
  updatedAt: string
  dueAt: string
  isOverdue: boolean
  resolutionNote: string | null
  resolvedAt: string | null
  closedAt: string | null
  version: number
  allowedActions: WorkOrderAction[]
}

export interface ActivityEvent {
  id: string
  type: AuditEventType
  fromStatus: WorkOrderStatus | null
  toStatus: WorkOrderStatus | null
  summary: string
  note: string | null
  actor: PersonSummary
  actorRole: UserRole
  occurredAt: string
}

export interface DemoAccounts {
  password: string
  accounts: { email: string; displayName: string; role: UserRole; organizationName: string }[]
}

export interface CreateWorkOrderInput {
  propertyId: string
  title: string
  description: string
  category: WorkOrderCategory
}
