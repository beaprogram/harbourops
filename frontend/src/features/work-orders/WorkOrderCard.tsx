import { Link } from 'react-router'
import type { UserRole, WorkOrderListItem } from '../../api/types'
import { OverdueBadge, PriorityBadge, StatusBadge } from '../../components/Badges'
import { categoryLabels, formatDateTime, workOrderReference } from '../../lib/format'

export function WorkOrderCard({ item, viewerRole }: { item: WorkOrderListItem; viewerRole: UserRole }) {
  return (
    <li className="relative rounded-lg border border-slate-200 bg-white p-4 shadow-xs transition-colors focus-within:border-teal-600 hover:border-teal-600">
      <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
        <div className="min-w-0">
          <p className="text-xs font-semibold text-slate-500">{workOrderReference(item.number)}</p>
          <h3 className="mt-0.5 font-semibold text-navy-900">
            {/* The ::after overlay makes the whole card clickable while keeping one accessible link. */}
            <Link to={`/work-orders/${item.id}`} className="after:absolute after:inset-0 focus-visible:outline-none">
              {item.title}
            </Link>
          </h3>
          <p className="mt-1 text-sm text-slate-600">
            {item.propertyName} · {categoryLabels[item.category]}
            {viewerRole !== 'Requester' && <> · Requested by {item.requesterName}</>}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2 md:justify-end">
          {item.isOverdue && <OverdueBadge />}
          <PriorityBadge priority={item.priority} />
          <StatusBadge status={item.status} />
        </div>
      </div>
      <dl className="mt-3 grid gap-x-6 gap-y-1 text-sm text-slate-600 sm:grid-cols-2">
        {viewerRole !== 'Technician' && (
          <div className="flex gap-1">
            <dt className="font-medium text-slate-700">Technician:</dt>
            <dd>{item.assignedTechnicianName ?? 'Not assigned'}</dd>
          </div>
        )}
        <div className="flex gap-1">
          <dt className="font-medium text-slate-700">Due:</dt>
          <dd>{formatDateTime(item.dueAt)}</dd>
        </div>
      </dl>
    </li>
  )
}
