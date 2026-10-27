import type { AuditEventType, UserRole, WorkOrderCategory, WorkOrderStatus } from '../api/types'

/** Every timestamp is stored in UTC and shown in Halifax time (AST/ADT), whatever the viewer's device zone. */
export const DISPLAY_TIME_ZONE = 'America/Halifax'

const dateTimeFormatter = new Intl.DateTimeFormat('en-CA', {
  timeZone: DISPLAY_TIME_ZONE,
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
  timeZoneName: 'short',
})

export function formatDateTime(isoUtc: string): string {
  return dateTimeFormatter.format(new Date(isoUtc))
}

export const statusLabels: Record<WorkOrderStatus, string> = {
  New: 'New',
  Assigned: 'Assigned',
  InProgress: 'In progress',
  Resolved: 'Resolved',
  Closed: 'Closed',
}

export const categoryLabels: Record<WorkOrderCategory, string> = {
  Plumbing: 'Plumbing',
  Electrical: 'Electrical',
  Heating: 'Heating',
  Appliances: 'Appliances',
  Exterior: 'Exterior & grounds',
  General: 'General repairs',
}

export const roleLabels: Record<UserRole, string> = {
  Manager: 'Manager',
  Technician: 'Technician',
  Requester: 'Requester',
}

export const activityLabels: Record<AuditEventType, string> = {
  Created: 'Submitted',
  PriorityChanged: 'Priority changed',
  Assigned: 'Assigned',
  Reassigned: 'Reassigned',
  Started: 'Started',
  Resolved: 'Resolved',
  Reopened: 'Reopened',
  Closed: 'Closed',
}

export function workOrderReference(number: number): string {
  return `WO-${number}`
}

const MINUTE = 60_000
const HOUR = 60 * MINUTE
const DAY = 24 * HOUR

/** "less than a minute", "45 min", "5 h 20 min", "2 days 3 h". */
export function formatDuration(ms: number): string {
  const value = Math.abs(ms)
  if (value < MINUTE) return 'less than a minute'
  if (value < HOUR) return `${Math.floor(value / MINUTE)} min`
  if (value < DAY) {
    const hours = Math.floor(value / HOUR)
    const minutes = Math.floor((value % HOUR) / MINUTE)
    return minutes > 0 ? `${hours} h ${minutes} min` : `${hours} h`
  }
  const days = Math.floor(value / DAY)
  const hours = Math.floor((value % DAY) / HOUR)
  return `${days} ${days === 1 ? 'day' : 'days'}${hours > 0 ? ` ${hours} h` : ''}`
}

export interface DeadlineFields {
  status: WorkOrderStatus
  dueAt: string
  isOverdue: boolean
  isDueSoon: boolean
}

/**
 * A relative deadline label measured from the server's `asOf`, so it always agrees with the
 * server's overdue / due-soon flags. Finished work (Resolved, Closed) has no current deadline.
 */
export function deadlineLabel(item: DeadlineFields, asOf: string): string | null {
  if (item.status === 'Resolved' || item.status === 'Closed') return null
  const remaining = new Date(item.dueAt).getTime() - new Date(asOf).getTime()
  if (item.isOverdue) return `Overdue by ${formatDuration(remaining)}`
  if (remaining < MINUTE) return 'Due now'
  return `Due in ${formatDuration(remaining)}`
}
