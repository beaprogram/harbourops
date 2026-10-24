import type { WorkOrderPriority, WorkOrderStatus } from '../api/types'
import { statusLabels } from '../lib/format'

// Status is always written out as text; colour only reinforces it.
const statusStyles: Record<WorkOrderStatus, string> = {
  New: 'bg-sky-100 text-sky-900 ring-sky-300',
  Assigned: 'bg-navy-100 text-navy-900 ring-navy-300',
  InProgress: 'bg-amber-100 text-amber-900 ring-amber-300',
  Resolved: 'bg-teal-100 text-teal-900 ring-teal-300',
  Closed: 'bg-slate-200 text-slate-800 ring-slate-300',
}

const badgeBase = 'inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-semibold ring-1 ring-inset whitespace-nowrap'

export function StatusBadge({ status }: { status: WorkOrderStatus }) {
  return <span className={`${badgeBase} ${statusStyles[status]}`}>{statusLabels[status]}</span>
}

export function PriorityBadge({ priority }: { priority: WorkOrderPriority }) {
  return priority === 'High' ? (
    <span className={`${badgeBase} bg-red-50 text-red-800 ring-red-300`}>
      <span aria-hidden="true">▲</span> High priority
    </span>
  ) : (
    <span className={`${badgeBase} bg-white text-slate-700 ring-slate-300`}>Normal priority</span>
  )
}

export function OverdueBadge() {
  return <span className={`${badgeBase} bg-red-700 text-white ring-red-700`}>Overdue</span>
}
