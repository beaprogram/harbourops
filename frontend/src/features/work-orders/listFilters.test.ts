import { describe, expect, it } from 'vitest'
import { DEFAULT_FILTERS, clearFilters, hasActiveFilters, parseListFilters, toSearchParams } from './listFilters'

const parse = (query: string) => parseListFilters(new URLSearchParams(query))

describe('list filters in the URL', () => {
  it('round-trips every filter through the URL', () => {
    const query =
      'q=leak&status=New&status=Assigned&priority=High&propertyId=30000000-0000-4000-8000-000000000011' +
      '&deadline=dueSoon&sort=priority&direction=asc&page=3'

    expect(toSearchParams(parse(query)).toString()).toBe(query)
  })

  it('drops malformed or unknown values instead of sending them to the API', () => {
    const filters = parse('status=Deleted&status=New&priority=Urgent&propertyId=not-a-guid&deadline=late&sort=title&page=-4')

    expect(filters).toEqual({ ...DEFAULT_FILTERS, statuses: ['New'] })
  })

  it('omits defaults so plain URLs stay plain', () => {
    expect(toSearchParams(DEFAULT_FILTERS).toString()).toBe('')
    expect(parse('sort=due').direction).toBe('asc')
  })

  it('prefers a technician over "unassigned" because the API rejects both together', () => {
    const filters = parse('unassigned=true&technicianId=20000000-0000-4000-8000-000000000012')

    expect(filters.unassigned).toBe(false)
    expect(filters.technicianId).toBe('20000000-0000-4000-8000-000000000012')
  })

  it('treats sorting and paging as view settings, not filters', () => {
    const sortedPage = parse('sort=updated&direction=desc&page=2')

    expect(hasActiveFilters(sortedPage)).toBe(false)
    expect(clearFilters(parse('q=x&sort=updated&direction=asc'))).toEqual({ ...DEFAULT_FILTERS, sort: 'updated', direction: 'asc' })
  })
})
