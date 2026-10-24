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
