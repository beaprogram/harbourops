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
  /** Present only for a demo persona: when the visitor's private demo (and this session) ends. */
  demo?: { expiresAt: string } | null
}

export type DemoPersonaKey = 'manager' | 'technician' | 'requester' | 'otherManager'

export interface DemoPersonaOption {
  key: DemoPersonaKey
  displayName: string
  role: UserRole
  organizationName: string
  description: string
}

export interface DemoSession {
  /** "none": no demo in this browser; "active"; "ended": expired, reset elsewhere, or ended. */
  status: 'none' | 'active' | 'ended'
  lifetimeMinutes: number
  session: {
    expiresAt: string
    currentPersona: DemoPersonaKey | null
    personas: DemoPersonaOption[]
    usage: {
      workOrders: number
      maxWorkOrders: number
      comments: number
      maxComments: number
      attachments: number
      maxAttachments: number
      storedBytes: number
      maxStoredBytes: number
      exports: number
      maxExports: number
    }
  } | null
}

export interface DemoSignIn {
  user: CurrentUser
  session: DemoSession
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
  isDueSoon: boolean
}

export interface PagedResponse<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
  /** The server's "now" for every deadline flag in this response. */
  asOf: string
}

export interface FilterChoice {
  id: string
  name: string
}

export interface WorkOrderFilterOptions {
  properties: FilterChoice[]
  technicians: FilterChoice[]
  canFilterByTechnician: boolean
  /** Only sent to managers, the only role allowed to export. */
  exportRowLimit: number | null
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
  isDueSoon: boolean
  asOf: string
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

export interface DashboardCard {
  key: 'active' | 'unassigned' | 'overdue' | 'dueSoon'
  title: string
  description: string
  count: number
  /** The work-order list query that produced `count`. */
  listQuery: string
}

export interface TechnicianWorkload {
  id: string
  displayName: string
  isActive: boolean
  notStartedCount: number
  inProgressCount: number
  activeCount: number
  overdueCount: number
  activeListQuery: string
  overdueListQuery: string
}

export interface OverdueWorkOrder {
  id: string
  number: number
  title: string
  propertyName: string
  priority: WorkOrderPriority
  status: WorkOrderStatus
  assignedTechnicianName: string | null
  dueAt: string
}

export interface Dashboard {
  asOf: string
  cards: DashboardCard[]
  workload: TechnicianWorkload[]
  oldestOverdue: OverdueWorkOrder[]
  overdueListQuery: string
}

export interface CommentItem {
  id: string
  body: string
  author: PersonSummary
  authorRole: UserRole
  createdAt: string
}

/** Chronological (oldest first) page; `hasOlder` means earlier comments exist before items[0]. */
export interface CommentPage {
  items: CommentItem[]
  hasOlder: boolean
  totalCount: number
}

export interface AttachmentItem {
  id: string
  fileName: string
  mediaType: string
  sizeBytes: number
  width: number
  height: number
  previewWidth: number
  previewHeight: number
  uploadedBy: PersonSummary
  uploaderRole: UserRole
  uploadedAt: string
  /** Small image for the gallery; the full image is only loaded when a photo is opened. */
  previewUrl: string
  contentUrl: string
}

export interface AttachmentList {
  items: AttachmentItem[]
  limits: { maxPerWorkOrder: number; maxFileSizeBytes: number; maxDimension: number; maxPixels: number; acceptedTypes: string[] }
  canUpload: boolean
  cannotUploadReason: string | null
}
