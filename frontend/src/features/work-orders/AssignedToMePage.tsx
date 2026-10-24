import { Link } from 'react-router'
import type { UserRole, WorkOrderListItem } from '../../api/types'
import { Button } from '../../components/Button'
import { Alert, EmptyState, LoadingState } from '../../components/Feedback'
import { useCurrentUser } from '../auth/auth'
import { WorkOrderCard } from './WorkOrderCard'
import { useWorkOrderList } from './workOrders'

/** The technician's queue: active jobs grouped by what to do next. */
export function AssignedToMePage() {
  const { data: user } = useCurrentUser()
  const active = useWorkOrderList({ statuses: ['InProgress', 'Assigned'], page: 1, pageSize: 50 })

  if (!user) return null

  const inProgress = active.data?.items.filter((i) => i.status === 'InProgress') ?? []
  const readyToStart = active.data?.items.filter((i) => i.status === 'Assigned') ?? []

  return (
    <div className="space-y-8">
      <div>
        <h1 className="text-2xl font-bold text-navy-900">Assigned to me</h1>
        <p className="mt-1 text-slate-600">
          Your active jobs. Finished work is in <Link to="/work-orders" className="font-medium text-teal-800 underline">My jobs</Link>.
        </p>
      </div>

      {active.isPending ? (
        <LoadingState label="Loading your jobs…" />
      ) : active.isError ? (
        <Alert
          tone="error"
          title="Your jobs could not be loaded"
          action={<Button variant="secondary" onClick={() => void active.refetch()}>Try again</Button>}
        >
          {active.error.message}
        </Alert>
      ) : inProgress.length + readyToStart.length === 0 ? (
        <EmptyState title="Nothing assigned right now">New assignments from your manager will appear here.</EmptyState>
      ) : (
        <>
          <QueueSection title="In progress" emptyText="No jobs in progress." items={inProgress} role={user.role} />
          <QueueSection title="Ready to start" emptyText="No jobs waiting to start." items={readyToStart} role={user.role} />
        </>
      )}
    </div>
  )
}

function QueueSection({
  title,
  emptyText,
  items,
  role,
}: {
  title: string
  emptyText: string
  items: WorkOrderListItem[]
  role: UserRole
}) {
  const headingId = `queue-${title.replace(/\s+/g, '-').toLowerCase()}`
  return (
    <section aria-labelledby={headingId}>
      <h2 id={headingId} className="mb-3 text-lg font-semibold text-navy-900">
        {title} <span className="text-slate-500">({items.length})</span>
      </h2>
      {items.length === 0 ? (
        <p className="text-sm text-slate-600">{emptyText}</p>
      ) : (
        <ul className="space-y-3">
          {items.map((item) => (
            <WorkOrderCard key={item.id} item={item} viewerRole={role} />
          ))}
        </ul>
      )}
    </section>
  )
}
