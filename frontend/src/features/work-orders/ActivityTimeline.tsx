import type { ActivityEvent } from '../../api/types'
import { StatusBadge } from '../../components/Badges'
import { Alert, LoadingState } from '../../components/Feedback'
import { activityLabels, formatDateTime, roleLabels } from '../../lib/format'

interface ActivityTimelineProps {
  events: ActivityEvent[] | undefined
  isPending: boolean
  error: Error | null
}

export function ActivityTimeline({ events, isPending, error }: ActivityTimelineProps) {
  return (
    <section aria-labelledby="activity-heading" className="rounded-xl border border-slate-200 bg-white p-6 shadow-xs">
      <h2 id="activity-heading" className="text-lg font-semibold text-navy-900">
        Activity
      </h2>
      {isPending ? (
        <LoadingState label="Loading activity…" />
      ) : error ? (
        <div className="mt-4">
          <Alert tone="error" title="Activity could not be loaded">
            {error.message}
          </Alert>
        </div>
      ) : (
        <ol className="mt-5 space-y-6 border-l-2 border-navy-100 pl-6">
          {events?.map((event) => (
            <li key={event.id} className="relative">
              <span aria-hidden="true" className="absolute top-1.5 -left-[31px] size-3 rounded-full border-2 border-white bg-teal-600 ring-2 ring-navy-100" />
              <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                <p className="font-semibold text-navy-900">{activityLabels[event.type]}</p>
                <time dateTime={event.occurredAt} className="text-sm text-slate-600">
                  {formatDateTime(event.occurredAt)}
                </time>
              </div>
              <p className="mt-1 text-sm text-slate-800">{event.summary}</p>
              <p className="text-sm text-slate-600">
                By {event.actor.displayName} ({roleLabels[event.actorRole]})
              </p>
              {event.fromStatus && event.toStatus && (
                <p className="mt-2 flex items-center gap-2 text-sm">
                  <StatusBadge status={event.fromStatus} />
                  <span aria-label="changed to">→</span>
                  <StatusBadge status={event.toStatus} />
                </p>
              )}
              {event.note && (
                <blockquote className="mt-2 rounded-md border-l-4 border-teal-600 bg-slate-50 px-3 py-2 text-sm whitespace-pre-line text-slate-800">
                  {event.note}
                </blockquote>
              )}
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}
