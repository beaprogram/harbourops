import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { PagedResponse, WorkOrderFilterOptions, WorkOrderListItem } from '../../api/types'
import { manager } from '../../test/fixtures'
import { json, mockApi, renderApp, type RecordedRequest } from '../../test/utils'

const options: WorkOrderFilterOptions = {
  properties: [{ id: '30000000-0000-4000-8000-000000000011', name: 'Quayside Lofts' }],
  technicians: [{ id: '20000000-0000-4000-8000-000000000012', name: 'Sam Okafor' }],
  canFilterByTechnician: true,
  exportRowLimit: 5000,
}

function item(overrides: Partial<WorkOrderListItem> = {}): WorkOrderListItem {
  return {
    id: 'wo-1',
    number: 7,
    title: 'Radiator banging',
    status: 'InProgress',
    priority: 'High',
    category: 'Heating',
    propertyName: 'Quayside Lofts',
    requesterName: 'Riley Chen',
    assignedTechnicianName: 'Sam Okafor',
    createdAt: '2026-01-15T14:00:00Z',
    updatedAt: '2026-01-15T14:00:00Z',
    dueAt: '2026-01-15T16:00:00Z',
    isOverdue: true,
    isDueSoon: false,
    ...overrides,
  }
}

function page(items: WorkOrderListItem[], totalCount = items.length, pageNumber = 1): PagedResponse<WorkOrderListItem> {
  return { items, page: pageNumber, pageSize: 20, totalCount, totalPages: Math.ceil(totalCount / 20), asOf: '2026-01-15T19:00:00Z' }
}

const listRequests = (requests: RecordedRequest[]) =>
  requests.filter((r) => r.method === 'GET' && r.url.startsWith('/api/work-orders?')).map((r) => new URL(r.url, 'http://x').searchParams)

describe('WorkOrderListPage', () => {
  it('reads search, filters, sort, and page from the URL and sends them to the API', async () => {
    const api = mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/filter-options': () => json(200, options),
      'GET /api/work-orders': () => json(200, page([item()], 21, 2)),
    })
    renderApp('/work-orders?q=radiator&status=New&status=Assigned&status=InProgress&deadline=overdue&sort=due&direction=asc&page=2')

    expect(await screen.findByText('Showing 21–21 of 21 work orders match')).toBeInTheDocument()
    expect(screen.getByLabelText('Search')).toHaveValue('radiator')
    expect(screen.getByLabelText('Status')).toHaveValue('active')
    expect(screen.getByLabelText('Deadline')).toHaveValue('overdue')
    expect(screen.getByLabelText('Sort by')).toHaveValue('due-asc')

    const sent = listRequests(api.requests).at(-1)!
    expect(sent.get('q')).toBe('radiator')
    expect(sent.getAll('status')).toEqual(['New', 'Assigned', 'InProgress'])
    expect(sent.get('deadline')).toBe('overdue')
    expect(sent.get('sort')).toBe('due')
    expect(sent.get('page')).toBe('2')
    expect(sent.get('pageSize')).toBe('20')
  })

  it('shows urgency as text with the exact due time', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/filter-options': () => json(200, options),
      'GET /api/work-orders': () =>
        json(200, page([item(), item({ id: 'wo-2', number: 8, isOverdue: false, isDueSoon: true, dueAt: '2026-01-15T22:00:00Z' })])),
    })
    renderApp('/work-orders')

    // Scope to the results: the Deadline filter also contains an "Overdue" option.
    const results = await screen.findByRole('region', { name: 'Work orders' })
    const [overdueCard, dueSoonCard] = within(results).getAllByRole('listitem')
    expect(within(overdueCard!).getByText('Overdue')).toBeInTheDocument()
    expect(within(overdueCard!).getByText('Overdue by 3 h')).toBeInTheDocument()
    expect(within(overdueCard!).getByText('Jan 15, 2026, 12:00 p.m. AST')).toBeInTheDocument()
    expect(within(dueSoonCard!).getByText('Due soon')).toBeInTheDocument()
    expect(within(dueSoonCard!).getByText('Due in 3 h')).toBeInTheDocument()
  })

  it('returns to page 1 and records the filter in the URL when a filter changes', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/filter-options': () => json(200, options),
      'GET /api/work-orders': () => json(200, page([item()], 45, 3)),
    })
    const { user, router } = renderApp('/work-orders?page=3')

    await user.selectOptions(await screen.findByLabelText('Technician'), 'unassigned')

    await waitFor(() => expect(router.state.location.search).toBe('?unassigned=true'))
  })

  it('debounces search typing into a single request', async () => {
    const api = mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/filter-options': () => json(200, options),
      'GET /api/work-orders': () => json(200, page([item()])),
    })
    const { user, router } = renderApp('/work-orders')

    await user.type(await screen.findByLabelText('Search'), 'radiator')

    await waitFor(() => expect(router.state.location.search).toBe('?q=radiator'))
    await waitFor(() => expect(listRequests(api.requests).some((p) => p.get('q') === 'radiator')).toBe(true))
    expect(listRequests(api.requests).filter((p) => p.has('q'))).toHaveLength(1)
  })

  it('clears search and filters but keeps the sort, then moves focus to the search box', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/filter-options': () => json(200, options),
      'GET /api/work-orders': () => json(200, page([item()])),
    })
    const { user, router } = renderApp('/work-orders?q=leak&priority=High&sort=priority&direction=desc')

    await user.click(await screen.findByRole('button', { name: 'Clear filters' }))

    await waitFor(() => expect(router.state.location.search).toBe('?sort=priority&direction=desc'))
    expect(screen.getByLabelText('Search')).toHaveValue('')
    expect(screen.getByLabelText('Search')).toHaveFocus()
  })

  it('offers a way out when filters match nothing', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/filter-options': () => json(200, options),
      'GET /api/work-orders': (request) =>
        json(200, request.url.includes('deadline=dueSoon') ? page([]) : page([item()])),
    })
    const { user, router } = renderApp('/work-orders?deadline=dueSoon')

    expect(await screen.findByText('No work orders match these filters')).toBeInTheDocument()
    expect(screen.getByText('No matching work orders')).toHaveAttribute('role', 'status')
    await user.click(screen.getAllByRole('button', { name: 'Clear filters' }).at(-1)!)

    await waitFor(() => expect(router.state.location.search).toBe(''))
    expect(await screen.findByText('Radiator banging')).toBeInTheDocument()
  })
})
