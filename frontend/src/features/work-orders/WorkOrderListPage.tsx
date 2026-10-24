import { Link, useSearchParams } from 'react-router'
import type { WorkOrderStatus } from '../../api/types'
import { Button } from '../../components/Button'
import { Alert, EmptyState, LoadingState } from '../../components/Feedback'
import { statusLabels } from '../../lib/format'
import { useCurrentUser } from '../auth/auth'
import { WorkOrderCard } from './WorkOrderCard'
import { useWorkOrderList } from './workOrders'

const PAGE_SIZE = 20
const STATUSES: WorkOrderStatus[] = ['New', 'Assigned', 'InProgress', 'Resolved', 'Closed']

const headings = {
  Manager: { title: 'All work orders', subtitle: 'Every request in your organization, newest first.' },
  Technician: { title: 'My jobs', subtitle: 'Everything assigned to you, newest first.' },
  Requester: { title: 'My requests', subtitle: 'Requests you have submitted, newest first.' },
} as const

/** Filters live in the URL (?status=…&page=…), so views can be bookmarked, shared, and navigated with Back. */
function readFilters(searchParams: URLSearchParams) {
  const statusParam = searchParams.get('status')
  const status = STATUSES.find((s) => s === statusParam) ?? null
  const pageNumber = Number.parseInt(searchParams.get('page') ?? '1', 10)
  return { status, page: Number.isFinite(pageNumber) && pageNumber > 0 ? pageNumber : 1 }
}

export function WorkOrderListPage() {
  const { data: user } = useCurrentUser()
  const [searchParams] = useSearchParams()
  const { status, page } = readFilters(searchParams)
  const list = useWorkOrderList({ statuses: status ? [status] : [], page, pageSize: PAGE_SIZE })

  if (!user) return null
  const heading = headings[user.role]
  const canCreate = user.role !== 'Technician'

  const hrefFor = (next: { status?: WorkOrderStatus | null; page?: number }) => {
    const params = new URLSearchParams()
    const nextStatus = next.status === undefined ? status : next.status
    if (nextStatus) params.set('status', nextStatus)
    const nextPage = next.page ?? 1
    if (nextPage > 1) params.set('page', String(nextPage))
    const query = params.toString()
    return query ? `/work-orders?${query}` : '/work-orders'
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">{heading.title}</h1>
          <p className="mt-1 text-slate-600">{heading.subtitle}</p>
        </div>
        {canCreate && (
          <Link
            to="/work-orders/new"
            className="inline-flex min-h-10 items-center justify-center rounded-md bg-teal-700 px-4 py-2 text-sm font-semibold text-white hover:bg-teal-800"
          >
            New request
          </Link>
        )}
      </div>

      <nav aria-label="Filter by status" className="-mx-1 overflow-x-auto">
        <ul className="flex gap-1 px-1 pb-1">
          {[null, ...STATUSES].map((value) => {
            const active = value === status
            return (
              <li key={value ?? 'all'}>
                <Link
                  to={hrefFor({ status: value, page: 1 })}
                  aria-current={active ? 'page' : undefined}
                  className={`inline-flex min-h-9 items-center rounded-full px-3.5 text-sm font-medium whitespace-nowrap ${
                    active ? 'bg-navy-800 text-white' : 'bg-white text-navy-800 ring-1 ring-slate-300 hover:bg-navy-50'
                  }`}
                >
                  {value ? statusLabels[value] : 'All'}
                </Link>
              </li>
            )
          })}
        </ul>
      </nav>

      {list.isPending ? (
        <LoadingState label="Loading work orders…" />
      ) : list.isError ? (
        <Alert
          tone="error"
          title="Work orders could not be loaded"
          action={
            <Button variant="secondary" onClick={() => void list.refetch()}>
              Try again
            </Button>
          }
        >
          {list.error.message}
        </Alert>
      ) : list.data.items.length === 0 ? (
        <EmptyState title={status ? `No ${statusLabels[status].toLowerCase()} work orders` : 'No work orders yet'}>
          {canCreate ? 'Submitted requests will appear here.' : 'Jobs assigned to you will appear here.'}
        </EmptyState>
      ) : (
        <section aria-label="Work orders" aria-busy={list.isFetching}>
          <ul className="space-y-3">
            {list.data.items.map((item) => (
              <WorkOrderCard key={item.id} item={item} viewerRole={user.role} />
            ))}
          </ul>
          <nav aria-label="Pagination" className="mt-6 flex items-center justify-between gap-4 text-sm">
            <p className="text-slate-600">
              Page {list.data.page} of {Math.max(list.data.totalPages, 1)} · {list.data.totalCount} total
            </p>
            <div className="flex gap-2">
              {page > 1 && (
                <Link className="rounded-md px-3 py-2 font-medium text-navy-700 ring-1 ring-slate-300 hover:bg-navy-50" to={hrefFor({ page: page - 1 })}>
                  Previous
                </Link>
              )}
              {page < list.data.totalPages && (
                <Link className="rounded-md px-3 py-2 font-medium text-navy-700 ring-1 ring-slate-300 hover:bg-navy-50" to={hrefFor({ page: page + 1 })}>
                  Next
                </Link>
              )}
            </div>
          </nav>
        </section>
      )}
    </div>
  )
}
