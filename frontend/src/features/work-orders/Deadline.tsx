import { DueSoonBadge, OverdueBadge } from '../../components/Badges'
import { deadlineLabel, formatDateTime, type DeadlineFields } from '../../lib/format'

/** Urgency badges: always text ("Overdue", "Due soon"), never colour alone. */
export function DeadlineBadge({ item }: { item: DeadlineFields }) {
  if (item.isOverdue) return <OverdueBadge />
  if (item.isDueSoon) return <DueSoonBadge />
  return null
}

/** The exact due time in Halifax plus a relative label measured from the server's `asOf`. */
export function DeadlineValue({ item, asOf }: { item: DeadlineFields; asOf: string }) {
  const label = deadlineLabel(item, asOf)
  const tone = item.isOverdue ? 'text-red-700 font-semibold' : item.isDueSoon ? 'text-amber-800 font-semibold' : 'text-slate-600'
  return (
    <>
      <time dateTime={item.dueAt}>{formatDateTime(item.dueAt)}</time>
      {label && <span className={`block ${tone}`}>{label}</span>}
    </>
  )
}
