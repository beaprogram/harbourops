import { Link } from 'react-router'
import type { DashboardCard } from '../../api/types'
import { PriorityBadge, StatusBadge } from '../../components/Badges'
import { Button } from '../../components/Button'
import { Alert, EmptyState, LoadingState } from '../../components/Feedback'
import { formatDateTime, formatDuration, workOrderReference } from '../../lib/format'
import { useDashboard } from './dashboard'
import { usePageTitle } from '../../lib/usePageTitle'

const listHref = (query: string) => (query ? `/work-orders?${query}` : '/work-orders')

const cardTone: Record<DashboardCard['key'], { box: string; label: string }> = {
  active: { box: 'border-slate-200 bg-white', label: '' },
  unassigned: { box: 'border-navy-200 bg-navy-50', label: 'Needs a technician' },
  overdue: { box: 'border-red-300 bg-red-50', label: 'Needs attention now' },
  dueSoon: { box: 'border-amber-300 bg-amber-50', label: 'Coming up' },
}

/** Manager-only. The route is guarded in the UI, and the API also returns 403 to everyone else. */
export function DashboardPage() {
  const dashboard = useDashboard()
  usePageTitle('Dashboard')

  if (dashboard.isPending) return <LoadingState label="Loading dashboard…" />
  if (dashboard.isError) {
    return (
      <Alert
        tone="error"
        title="The dashboard could not be loaded"
        action={
          <Button variant="secondary" onClick={() => void dashboard.refetch()}>
            Try again
          </Button>
        }
      >
        {dashboard.error.message}
      </Alert>
    )
  }

  const data = dashboard.data
  const maxActive = Math.max(1, ...data.workload.map((w) => w.activeCount))

  return (
    <div className="space-y-8">
      <div className="flex flex-col gap-1 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">Dashboard</h1>
          <p className="mt-1 text-slate-600">What needs attention now. Every number opens the matching work orders.</p>
        </div>
        <p className="text-sm text-slate-600">
          As of <time dateTime={data.asOf}>{formatDateTime(data.asOf)}</time>
        </p>
      </div>

      <section aria-labelledby="summary-heading">
        <h2 id="summary-heading" className="sr-only">
          Summary
        </h2>
        <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {data.cards.map((card) => (
            <li key={card.key}>
              <Link
                to={listHref(card.listQuery)}
                className={`block h-full rounded-xl border p-5 shadow-xs transition-colors hover:border-teal-600 ${cardTone[card.key].box}`}
              >
                <span className="block text-sm font-semibold text-navy-900">{card.title}</span>
                <span className="mt-2 block text-4xl font-bold text-navy-900 tabular-nums">{card.count}</span>
                <span className="mt-2 block text-sm text-slate-700">{card.description}</span>
                {card.count > 0 && cardTone[card.key].label && (
                  <span className="mt-2 block text-xs font-semibold text-slate-800 uppercase">{cardTone[card.key].label}</span>
                )}
                <span className="mt-3 block text-sm font-medium text-teal-800 underline">View list</span>
              </Link>
            </li>
          ))}
        </ul>
      </section>

      <div className="grid gap-6 lg:grid-cols-5">
        <section aria-labelledby="workload-heading" className="min-w-0 rounded-xl border border-slate-200 bg-white p-5 shadow-xs lg:col-span-3">
          <h2 id="workload-heading" className="text-lg font-semibold text-navy-900">
            Technician workload
          </h2>
          <p className="mt-1 text-sm text-slate-600">Active work assigned to each technician (assigned or in progress).</p>
          {data.workload.length === 0 ? (
            <div className="mt-4">
              <EmptyState title="No technicians yet">Technicians in your organization will appear here.</EmptyState>
            </div>
          ) : (
            // The wide table scrolls inside this wrapper instead of widening the page. `relative` matters: without
            // it, the absolutely positioned sr-only labels inside the table escape the scroll area at narrow widths.
            <div className="relative mt-4 overflow-x-auto" tabIndex={0} role="region" aria-label="Technician workload table">
              <table className="w-full min-w-[34rem] text-left text-sm">
                <caption className="sr-only">Active work per technician, busiest first</caption>
                <thead className="border-b border-slate-200 text-slate-600">
                  <tr>
                    <th scope="col" className="py-2 pr-3 font-medium">Technician</th>
                    <th scope="col" className="px-3 py-2 text-right font-medium">Not started</th>
                    <th scope="col" className="px-3 py-2 text-right font-medium">In progress</th>
                    <th scope="col" className="px-3 py-2 text-right font-medium">Active total</th>
                    <th scope="col" className="py-2 pl-3 text-right font-medium">Overdue</th>
                  </tr>
                </thead>
                <tbody>
                  {data.workload.map((row) => (
                    <tr key={row.id} className="border-b border-slate-100 last:border-0">
                      <th scope="row" className="py-3 pr-3 font-medium text-navy-900">
                        {row.displayName}
                        {!row.isActive && <span className="ml-1 text-xs font-normal text-slate-600">(deactivated)</span>}
                        {/* A compact visual cue; the exact number is always in the "Active total" column. */}
                        <span aria-hidden="true" className="mt-1 block h-1.5 rounded-full bg-slate-100">
                          <span
                            className="block h-1.5 rounded-full bg-teal-600"
                            style={{ width: `${(row.activeCount / maxActive) * 100}%` }}
                          />
                        </span>
                      </th>
                      <td className="px-3 py-3 text-right tabular-nums">{row.notStartedCount}</td>
                      <td className="px-3 py-3 text-right tabular-nums">{row.inProgressCount}</td>
                      <td className="px-3 py-3 text-right font-semibold tabular-nums">
                        {row.activeCount > 0 ? (
                          <Link to={listHref(row.activeListQuery)} className="text-teal-800 underline">
                            {row.activeCount}{' '}
                            <span className="sr-only">active jobs for {row.displayName}</span>
                          </Link>
                        ) : (
                          <>
                            0 <span className="block text-xs font-normal text-slate-600">Available</span>
                          </>
                        )}
                      </td>
                      <td className="py-3 pl-3 text-right tabular-nums">
                        {row.overdueCount > 0 ? (
                          <Link to={listHref(row.overdueListQuery)} className="font-semibold text-red-700 underline">
                            {row.overdueCount} overdue{' '}
                            <span className="sr-only">for {row.displayName}</span>
                          </Link>
                        ) : (
                          <span className="text-slate-600">None</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>

        <section aria-labelledby="overdue-heading" className="min-w-0 rounded-xl border border-slate-200 bg-white p-5 shadow-xs lg:col-span-2">
          <div className="flex items-baseline justify-between gap-3">
            <h2 id="overdue-heading" className="text-lg font-semibold text-navy-900">
              Oldest overdue
            </h2>
            {data.oldestOverdue.length > 0 && (
              <Link to={listHref(data.overdueListQuery)} className="text-sm font-medium text-teal-800 underline">
                View all overdue
              </Link>
            )}
          </div>
          {data.oldestOverdue.length === 0 ? (
            <p className="mt-4 rounded-md bg-teal-50 p-4 text-sm text-teal-900">Nothing is overdue. Nice work.</p>
          ) : (
            <ol className="mt-4 space-y-3">
              {data.oldestOverdue.map((item) => (
                <li key={item.id} className="rounded-lg border border-slate-200 p-3">
                  <p className="text-xs font-semibold text-slate-500">{workOrderReference(item.number)}</p>
                  <Link to={`/work-orders/${item.id}`} className="font-semibold text-navy-900 underline-offset-2 hover:underline">
                    {item.title}
                  </Link>
                  <p className="mt-1 text-sm text-slate-600">
                    {item.propertyName} · {item.assignedTechnicianName ?? 'Unassigned'}
                  </p>
                  <div className="mt-2 flex flex-wrap items-center gap-2">
                    <span className="text-sm font-semibold text-red-700">
                      Overdue by {formatDuration(new Date(data.asOf).getTime() - new Date(item.dueAt).getTime())}
                    </span>
                    <PriorityBadge priority={item.priority} />
                    <StatusBadge status={item.status} />
                  </div>
                </li>
              ))}
            </ol>
          )}
        </section>
      </div>
    </div>
  )
}
