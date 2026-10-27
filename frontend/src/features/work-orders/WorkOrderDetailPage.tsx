import type { ReactNode } from 'react'
import { Link, useLocation, useParams } from 'react-router'
import { ApiError } from '../../api/client'
import { PriorityBadge, StatusBadge } from '../../components/Badges'
import { Button } from '../../components/Button'
import { Alert, LoadingState } from '../../components/Feedback'
import { categoryLabels, formatDateTime, workOrderReference } from '../../lib/format'
import { ActivityTimeline } from './ActivityTimeline'
import { AttachmentsSection } from './AttachmentsSection'
import { CommentsSection } from './CommentsSection'
import { DeadlineBadge, DeadlineValue } from './Deadline'
import { WorkOrderActions } from './WorkOrderActions'
import { useWorkOrder, useWorkOrderActivity } from './workOrders'
import { usePageTitle } from '../../lib/usePageTitle'

export function WorkOrderDetailPage() {
  const { id = '' } = useParams()
  const location = useLocation()
  const flash = (location.state as { flash?: string } | null)?.flash
  const workOrder = useWorkOrder(id)
  const activity = useWorkOrderActivity(id)
  usePageTitle(workOrder.data ? `${workOrderReference(workOrder.data.number)}: ${workOrder.data.title}` : 'Work order')

  if (workOrder.isPending) return <LoadingState label="Loading work order…" />

  if (workOrder.isError) {
    const notFound = workOrder.error instanceof ApiError && workOrder.error.status === 404
    return (
      <div className="mx-auto max-w-xl space-y-4">
        <Alert
          tone="error"
          title={notFound ? 'Work order not found' : 'This work order could not be loaded'}
          action={
            notFound ? (
              <Link to="/work-orders" className="font-medium text-teal-800 underline">
                Back to work orders
              </Link>
            ) : (
              <Button variant="secondary" onClick={() => void workOrder.refetch()}>
                Try again
              </Button>
            )
          }
        >
          {notFound
            ? 'It may not exist, or you may not have access to it.'
            : workOrder.error.message}
        </Alert>
      </div>
    )
  }

  const wo = workOrder.data
  return (
    <div className="space-y-6">
      <Link to="/work-orders" className="text-sm font-medium text-teal-800 underline">
        Back to work orders
      </Link>

      {flash && (
        <Alert tone="success" title={flash} />
      )}

      <header className="space-y-3">
        <p className="text-sm font-semibold text-slate-500">{workOrderReference(wo.number)}</p>
        <h1 className="text-2xl font-bold text-navy-900 sm:text-3xl">{wo.title}</h1>
        <div className="flex flex-wrap gap-2">
          <StatusBadge status={wo.status} />
          <PriorityBadge priority={wo.priority} />
          <DeadlineBadge item={wo} />
        </div>
      </header>

      {/* Mobile order: details, actions, comments, activity. Desktop: actions in a sidebar. */}
      <div className="grid gap-6 lg:grid-cols-3 lg:items-start">
        <div className="space-y-6 lg:col-span-2">
          <section aria-labelledby="details-heading" className="rounded-xl border border-slate-200 bg-white p-6 shadow-xs">
            <h2 id="details-heading" className="sr-only">
              Details
            </h2>
            <p className="whitespace-pre-line text-slate-800">{wo.description}</p>
            <dl className="mt-6 grid gap-4 text-sm sm:grid-cols-2">
              <Detail label="Property">
                {wo.property.name}
                <span className="block text-slate-600">
                  {wo.property.addressLine}, {wo.property.community}, NS
                </span>
              </Detail>
              <Detail label="Category">{categoryLabels[wo.category]}</Detail>
              <Detail label="Requested by">{wo.requester.displayName}</Detail>
              <Detail label="Technician">{wo.assignedTechnician?.displayName ?? 'Not assigned yet'}</Detail>
              <Detail label="Submitted">{formatDateTime(wo.createdAt)}</Detail>
              <Detail label="Due">
                <DeadlineValue item={wo} asOf={wo.asOf} />
              </Detail>
              <Detail label="Last updated">{formatDateTime(wo.updatedAt)}</Detail>
              {wo.closedAt && <Detail label="Closed">{formatDateTime(wo.closedAt)}</Detail>}
            </dl>
          </section>

          {wo.resolutionNote && (
            <section aria-labelledby="resolution-heading" className="rounded-xl border border-teal-200 bg-teal-50 p-6">
              <h2 id="resolution-heading" className="font-semibold text-teal-950">
                Resolution
              </h2>
              <p className="mt-2 whitespace-pre-line text-teal-950">{wo.resolutionNote}</p>
              {wo.resolvedAt && <p className="mt-2 text-sm text-teal-900">Resolved {formatDateTime(wo.resolvedAt)}</p>}
            </section>
          )}
        </div>

        <aside className="lg:col-start-3 lg:row-span-2 lg:row-start-1">
          <WorkOrderActions workOrder={wo} />
        </aside>

        <div className="space-y-6 lg:col-span-2">
          <AttachmentsSection workOrder={wo} />
          <CommentsSection workOrder={wo} />
          <ActivityTimeline events={activity.data} isPending={activity.isPending} error={activity.error} />
        </div>
      </div>
    </div>
  )
}

function Detail({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt className="font-medium text-slate-600">{label}</dt>
      <dd className="mt-0.5 text-navy-900">{children}</dd>
    </div>
  )
}
