import type { ActivityEvent, CurrentUser, WorkOrderDetail } from '../api/types'

export const manager: CurrentUser = {
  id: 'u-manager',
  email: 'manager@harbourview.example',
  displayName: 'Morgan Ellis',
  role: 'Manager',
  organization: { id: 'org-a', name: 'Harbourview Property Group' },
}

export const technician: CurrentUser = {
  id: 'u-tech',
  email: 'tech.sam@harbourview.example',
  displayName: 'Sam Okafor',
  role: 'Technician',
  organization: { id: 'org-a', name: 'Harbourview Property Group' },
}

export function workOrder(overrides: Partial<WorkOrderDetail> = {}): WorkOrderDetail {
  return {
    id: 'wo-1',
    number: 1042,
    title: 'No heat from bedroom radiator',
    description: 'Radiator stays cold.',
    category: 'Heating',
    priority: 'High',
    status: 'Resolved',
    property: { id: 'p-1', name: 'Tidewater Court', addressLine: '240 Tidewater Court', community: 'Halifax' },
    requester: { id: 'u-req', displayName: 'Alex MacIsaac' },
    assignedTechnician: { id: 'u-tech', displayName: 'Sam Okafor' },
    createdAt: '2026-01-15T14:00:00Z',
    updatedAt: '2026-01-15T18:00:00Z',
    dueAt: '2026-01-16T14:00:00Z',
    isOverdue: false,
    resolutionNote: 'Bled the radiator and replaced the valve.',
    resolvedAt: '2026-01-15T18:00:00Z',
    closedAt: null,
    version: 7,
    allowedActions: ['Close'],
    ...overrides,
  }
}

export const activity: ActivityEvent[] = [
  {
    id: 'a-1',
    type: 'Created',
    fromStatus: null,
    toStatus: 'New',
    summary: 'Request submitted.',
    note: null,
    actor: { id: 'u-req', displayName: 'Alex MacIsaac' },
    actorRole: 'Requester',
    occurredAt: '2026-01-15T14:00:00Z',
  },
  {
    id: 'a-2',
    type: 'Resolved',
    fromStatus: 'InProgress',
    toStatus: 'Resolved',
    summary: 'Marked resolved.',
    note: 'Bled the radiator and replaced the valve.',
    actor: { id: 'u-tech', displayName: 'Sam Okafor' },
    actorRole: 'Technician',
    occurredAt: '2026-01-15T18:00:00Z',
  },
]
