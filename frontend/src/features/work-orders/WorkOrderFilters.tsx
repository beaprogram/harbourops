import { useEffect, useEffectEvent, useId, useState, type ReactNode, type RefObject } from 'react'
import type { WorkOrderFilterOptions, WorkOrderStatus } from '../../api/types'
import { Button } from '../../components/Button'
import { inputClass } from '../../components/FormField'
import { statusLabels } from '../../lib/format'
import {
  ACTIVE_STATUSES,
  ALL_STATUSES,
  SEARCH_MAX_LENGTH,
  clearFilters,
  hasActiveFilters,
  type SortDirection,
  type SortField,
  type WorkOrderListFilters,
} from './listFilters'

const SEARCH_DEBOUNCE_MS = 300

const SORT_OPTIONS: { value: `${SortField}-${SortDirection}`; label: string }[] = [
  { value: 'created-desc', label: 'Newest first' },
  { value: 'created-asc', label: 'Oldest first' },
  { value: 'updated-desc', label: 'Recently updated' },
  { value: 'updated-asc', label: 'Least recently updated' },
  { value: 'due-asc', label: 'Due soonest' },
  { value: 'due-desc', label: 'Due latest' },
  { value: 'priority-desc', label: 'Highest priority first' },
  { value: 'priority-asc', label: 'Lowest priority first' },
]

interface WorkOrderFiltersProps {
  filters: WorkOrderListFilters
  options: WorkOrderFilterOptions | undefined
  /** Discrete changes add a history entry; debounced search typing replaces the current one. */
  onChange: (next: WorkOrderListFilters, options?: { replace?: boolean }) => void
  searchInputRef: RefObject<HTMLInputElement | null>
}

export function WorkOrderFilters({ filters, options, onChange, searchInputRef }: WorkOrderFiltersProps) {
  const panelId = useId()
  const [expanded, setExpanded] = useState(false)
  const active = hasActiveFilters(filters)
  const activeCount = countActive(filters)

  // Any filter change returns to page 1, so the user never lands on an empty page past the end.
  const set = (patch: Partial<WorkOrderListFilters>, replace = false) =>
    onChange({ ...filters, ...patch, page: 1 }, { replace })

  const statusValue = statusKey(filters.statuses)
  const technicianValue = filters.unassigned ? 'unassigned' : (filters.technicianId ?? '')

  return (
    <section aria-label="Search and filters" className="rounded-xl border border-slate-200 bg-white p-4 shadow-xs">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
        <div className="flex-1">
          <SearchField
            value={filters.q}
            inputRef={searchInputRef}
            onCommit={(q) => set({ q }, true)}
          />
        </div>
        <div className="flex gap-2">
          <Button
            variant="secondary"
            className="md:hidden"
            aria-expanded={expanded}
            aria-controls={panelId}
            onClick={() => setExpanded((value) => !value)}
          >
            {expanded ? 'Hide filters' : 'Filters'}
            {activeCount > 0 && <span className="rounded-full bg-navy-800 px-1.5 text-xs text-white">{activeCount}</span>}
          </Button>
          {active && (
            <Button
              variant="ghost"
              onClick={() => {
                onChange(clearFilters(filters))
                // The button disappears once filters are cleared, so put focus somewhere useful.
                searchInputRef.current?.focus()
              }}
            >
              Clear filters
            </Button>
          )}
        </div>
      </div>

      <div id={panelId} className={`${expanded ? 'grid' : 'hidden'} mt-4 gap-3 sm:grid-cols-2 md:grid md:grid-cols-3 lg:grid-cols-6`}>
        <Select label="Status" value={statusValue} onChange={(value) => set({ statuses: statusesFor(value, filters.statuses) })}>
          <option value="">Any status</option>
          <option value="active">Active</option>
          {ALL_STATUSES.map((status) => (
            <option key={status} value={status}>
              {statusLabels[status]}
            </option>
          ))}
          {statusValue === 'custom' && <option value="custom">Custom selection</option>}
        </Select>

        <Select
          label="Priority"
          value={filters.priorities.length === 1 ? filters.priorities[0]! : ''}
          onChange={(value) => set({ priorities: value === 'High' || value === 'Normal' ? [value] : [] })}
        >
          <option value="">Any priority</option>
          <option value="High">High</option>
          <option value="Normal">Normal</option>
        </Select>

        <Select label="Property" value={filters.propertyId ?? ''} onChange={(value) => set({ propertyId: value || null })}>
          <option value="">Any property</option>
          {options?.properties.map((property) => (
            <option key={property.id} value={property.id}>
              {property.name}
            </option>
          ))}
          {filters.propertyId && !options?.properties.some((p) => p.id === filters.propertyId) && (
            <option value={filters.propertyId}>Selected property</option>
          )}
        </Select>

        {options?.canFilterByTechnician && (
          <Select
            label="Technician"
            value={technicianValue}
            onChange={(value) =>
              set(
                value === 'unassigned'
                  ? { unassigned: true, technicianId: null }
                  : { unassigned: false, technicianId: value || null },
              )
            }
          >
            <option value="">Any technician</option>
            <option value="unassigned">Unassigned</option>
            {options.technicians.map((technician) => (
              <option key={technician.id} value={technician.id}>
                {technician.name}
              </option>
            ))}
            {filters.technicianId && !options.technicians.some((t) => t.id === filters.technicianId) && (
              <option value={filters.technicianId}>Selected technician</option>
            )}
          </Select>
        )}

        <Select
          label="Deadline"
          value={filters.deadline ?? ''}
          onChange={(value) => set({ deadline: value === 'overdue' || value === 'dueSoon' ? value : null })}
        >
          <option value="">Any deadline</option>
          <option value="overdue">Overdue</option>
          <option value="dueSoon">Due within 24 hours</option>
        </Select>

        <Select
          label="Sort by"
          value={`${filters.sort}-${filters.direction}`}
          onChange={(value) => {
            const [sort, direction] = value.split('-') as [SortField, SortDirection]
            set({ sort, direction })
          }}
        >
          {SORT_OPTIONS.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </Select>
      </div>
    </section>
  )
}

/**
 * Search input that updates the URL 300 ms after typing stops. If the URL changes for another
 * reason (Clear filters, Back button), the box follows it.
 */
function SearchField({
  value,
  onCommit,
  inputRef,
}: {
  value: string
  onCommit: (value: string) => void
  inputRef: RefObject<HTMLInputElement | null>
}) {
  const id = useId()
  const [text, setText] = useState(value)
  const [syncedValue, setSyncedValue] = useState(value)
  if (value !== syncedValue) {
    // Adjusting state while rendering is React's recommended way to follow an outside change.
    setSyncedValue(value)
    setText(value)
  }

  const commit = useEffectEvent((next: string) => onCommit(next))
  useEffect(() => {
    if (text === value) return
    const timer = window.setTimeout(() => commit(text), SEARCH_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [text, value])

  return (
    <div className="space-y-1.5">
      <label htmlFor={id} className="block text-sm font-medium text-navy-900">
        Search
      </label>
      <input
        id={id}
        ref={inputRef}
        type="search"
        value={text}
        maxLength={SEARCH_MAX_LENGTH}
        placeholder="Title, description, or WO number"
        className={inputClass}
        onChange={(event) => setText(event.target.value)}
      />
    </div>
  )
}

function Select({
  label,
  value,
  onChange,
  children,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  children: ReactNode
}) {
  const id = useId()
  return (
    <div className="space-y-1.5">
      <label htmlFor={id} className="block text-sm font-medium text-navy-900">
        {label}
      </label>
      <select id={id} className={inputClass} value={value} onChange={(event) => onChange(event.target.value)}>
        {children}
      </select>
    </div>
  )
}

function statusKey(statuses: WorkOrderStatus[]): string {
  if (statuses.length === 0) return ''
  if (statuses.length === ACTIVE_STATUSES.length && ACTIVE_STATUSES.every((s) => statuses.includes(s))) return 'active'
  if (statuses.length === 1) return statuses[0]!
  return 'custom'
}

function statusesFor(key: string, current: WorkOrderStatus[]): WorkOrderStatus[] {
  if (key === '') return []
  if (key === 'active') return [...ACTIVE_STATUSES]
  if (key === 'custom') return current
  return ALL_STATUSES.filter((s) => s === key)
}

function countActive(filters: WorkOrderListFilters): number {
  return [
    filters.statuses.length > 0,
    filters.priorities.length > 0,
    filters.propertyId !== null,
    filters.technicianId !== null || filters.unassigned,
    filters.deadline !== null,
  ].filter(Boolean).length
}
