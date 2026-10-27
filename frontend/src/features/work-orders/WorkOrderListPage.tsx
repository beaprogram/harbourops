import { useRef } from 'react'
import { Link, useSearchParams } from 'react-router'
import { Button } from '../../components/Button'
import { Alert, EmptyState, LoadingState } from '../../components/Feedback'
import { useCurrentUser } from '../auth/auth'
import { ExportButton } from './ExportButton'
import { clearFilters, hasActiveFilters, parseListFilters, toSearchParams, type WorkOrderListFilters } from './listFilters'
import { WorkOrderCard } from './WorkOrderCard'
import { WorkOrderFilters } from './WorkOrderFilters'
import { useFilterOptions, useWorkOrderList } from './workOrders'
import { usePageTitle } from '../../lib/usePageTitle'

const PAGE_SIZE = 20

const headings = {
  Manager: { title: 'All work orders', subtitle: 'Every request in your organization.' },
  Technician: { title: 'My jobs', subtitle: 'Everything assigned to you.' },
  Requester: { title: 'My requests', subtitle: 'Requests you have submitted.' },
} as const

const pageLinkClass = 'rounded-md px-3 py-2 font-medium text-navy-700 ring-1 ring-slate-300 hover:bg-navy-50'

export function WorkOrderListPage() {
  const { data: user } = useCurrentUser()
  const [searchParams, setSearchParams] = useSearchParams()
  const filters = parseListFilters(searchParams)
  const list = useWorkOrderList(filters, PAGE_SIZE)
  const filterOptions = useFilterOptions()
  const searchInputRef = useRef<HTMLInputElement>(null)
  usePageTitle(user ? headings[user.role].title : 'Work orders')

  if (!user) return null
  const heading = headings[user.role]
  const canCreate = user.role !== 'Technician'
  const filtered = hasActiveFilters(filters)

  const update = (next: WorkOrderListFilters, options?: { replace?: boolean }) =>
    setSearchParams(toSearchParams(next), { replace: options?.replace ?? false })
  const pageHref = (page: number) => {
    const query = toSearchParams({ ...filters, page }).toString()
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

      <WorkOrderFilters
        filters={filters}
        options={filterOptions.data}
        onChange={update}
        searchInputRef={searchInputRef}
      />

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
      ) : (
        <section aria-label="Work orders" aria-busy={list.isFetching}>
          <div className="mb-3 flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
            <p role="status" className="text-sm text-slate-700">
              {resultSummary(list.data.totalCount, list.data.page, list.data.pageSize, list.data.items.length, filtered)}
              {list.isPlaceholderData && <span className="text-slate-500"> · Updating…</span>}
            </p>
            {user.role === 'Manager' && filterOptions.data?.exportRowLimit != null && (
              <ExportButton filters={filters} matchingCount={list.data.totalCount} rowLimit={filterOptions.data.exportRowLimit} />
            )}
          </div>

          {list.data.items.length === 0 ? (
            filtered ? (
              <EmptyState title="No work orders match these filters">
                <p>Try a different search or remove a filter.</p>
                <Button
                  variant="secondary"
                  className="mt-4"
                  onClick={() => {
                    update(clearFilters(filters))
                    searchInputRef.current?.focus()
                  }}
                >
                  Clear filters
                </Button>
              </EmptyState>
            ) : (
              <EmptyState title="No work orders yet">
                {canCreate ? 'Submitted requests will appear here.' : 'Jobs assigned to you will appear here.'}
              </EmptyState>
            )
          ) : (
            <ul className="space-y-3">
              {list.data.items.map((item) => (
                <WorkOrderCard key={item.id} item={item} viewerRole={user.role} asOf={list.data.asOf} />
              ))}
            </ul>
          )}

          {list.data.totalPages > 1 && (
            <nav aria-label="Pagination" className="mt-6 flex items-center justify-between gap-4 text-sm">
              <p className="text-slate-600">
                Page {list.data.page} of {list.data.totalPages}
              </p>
              <div className="flex gap-2">
                {filters.page > 1 && (
                  <Link className={pageLinkClass} to={pageHref(filters.page - 1)}>
                    Previous
                  </Link>
                )}
                {filters.page < list.data.totalPages && (
                  <Link className={pageLinkClass} to={pageHref(filters.page + 1)}>
                    Next
                  </Link>
                )}
              </div>
            </nav>
          )}
        </section>
      )}
    </div>
  )
}

function resultSummary(total: number, page: number, pageSize: number, shown: number, filtered: boolean): string {
  const noun = total === 1 ? 'work order' : 'work orders'
  if (total === 0) return filtered ? 'No matching work orders' : 'No work orders'
  if (shown === 0) return `${total} ${noun}${filtered ? ' match' : ''} · this page is past the last result`
  const first = (page - 1) * pageSize + 1
  const range = total > pageSize ? `Showing ${first}–${first + shown - 1} of ` : ''
  return `${range}${total} ${noun}${filtered ? (total === 1 ? ' matches' : ' match') : ''}`
}
