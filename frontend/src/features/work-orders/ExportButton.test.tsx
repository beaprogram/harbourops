import { screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { CurrentUser, PagedResponse, WorkOrderFilterOptions, WorkOrderListItem } from '../../api/types'
import { manager } from '../../test/fixtures'
import { json, mockApi, problem, renderApp } from '../../test/utils'

const options: WorkOrderFilterOptions = { properties: [], technicians: [], canFilterByTechnician: true, exportRowLimit: 5000 }

const listOf = (totalCount: number): PagedResponse<WorkOrderListItem> => ({
  items: [], page: 1, pageSize: 20, totalCount, totalPages: Math.ceil(totalCount / 20), asOf: '2026-01-15T19:00:00Z',
})

describe('Exporting the work-order list', () => {
  const downloads: string[] = []

  beforeEach(() => {
    downloads.length = 0
    // jsdom has no object URLs or real downloads; record what would have been saved instead.
    URL.createObjectURL = vi.fn(() => 'blob:harbourops-test')
    URL.revokeObjectURL = vi.fn()
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      downloads.push(this.download)
    })
  })

  afterEach(() => {
    // @ts-expect-error -- remove the test-only stubs again
    delete URL.createObjectURL
    // @ts-expect-error -- remove the test-only stubs again
    delete URL.revokeObjectURL
  })

  it('exports every row matching the current filters, not just the current page', async () => {
    let finish!: () => void
    const api = mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/filter-options': () => json(200, options),
      'GET /api/work-orders': () => json(200, listOf(45)),
      'GET /api/work-orders/export': () =>
        new Promise<Response>((resolve) => {
          finish = () =>
            resolve(new Response('Reference,Title\r\n', {
              status: 200,
              headers: {
                'Content-Type': 'text/csv; charset=utf-8',
                'Content-Disposition': "attachment; filename=harbourops-work-orders-20260115-1500.csv; filename*=UTF-8''harbourops-work-orders-20260115-1500.csv",
              },
            }))
        }),
    })
    const { user } = renderApp('/work-orders?q=leak&deadline=overdue&page=2')

    expect(await screen.findByText('Exports all 45 matching work orders (limit 5,000).')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Export CSV' }))

    expect(screen.getByRole('button', { name: 'Preparing CSV…' })).toBeDisabled()
    finish()
    await waitFor(() => expect(downloads).toEqual(['harbourops-work-orders-20260115-1500.csv']))

    const exportUrl = new URL(api.requestsTo('GET', '/api/work-orders/export')[0]!.url, 'http://x')
    expect(exportUrl.searchParams.get('q')).toBe('leak')
    expect(exportUrl.searchParams.get('deadline')).toBe('overdue')
    expect(exportUrl.searchParams.has('page')).toBe(false)
  })

  it('explains the limit and disables export when too many work orders match', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/filter-options': () => json(200, options),
      'GET /api/work-orders': () => json(200, listOf(6200)),
    })
    renderApp('/work-orders')

    expect(await screen.findByText('6,200 work orders match. Narrow the filters to 5,000 or fewer to export.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Export CSV' })).toBeDisabled()
  })

  it("shows the server's reason when an export is refused", async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/filter-options': () => json(200, options),
      'GET /api/work-orders': () => json(200, listOf(12)),
      'GET /api/work-orders/export': () =>
        problem(422, 'export_too_large', 'This export would contain 5,010 work orders, more than the 5,000 limit.'),
    })
    const { user } = renderApp('/work-orders')

    await user.click(await screen.findByRole('button', { name: 'Export CSV' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('more than the 5,000 limit')
    expect(downloads).toEqual([])
  })

  it('is not offered to other roles', async () => {
    const requester: CurrentUser = { ...manager, role: 'Requester' }
    mockApi({
      'GET /api/auth/me': () => json(200, requester),
      'GET /api/work-orders/filter-options': () => json(200, { ...options, exportRowLimit: null }),
      'GET /api/work-orders': () => json(200, listOf(3)),
    })
    renderApp('/work-orders')

    expect(await screen.findByRole('heading', { name: 'My requests' })).toBeInTheDocument()
    await screen.findByText(/^3 work orders/)
    expect(screen.queryByRole('button', { name: 'Export CSV' })).not.toBeInTheDocument()
  })
})
