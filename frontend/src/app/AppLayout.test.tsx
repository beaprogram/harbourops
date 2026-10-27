import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { PagedResponse, WorkOrderListItem } from '../api/types'
import { activity, manager, workOrder } from '../test/fixtures'
import { json, mockApi, noAttachments, noComments, renderApp } from '../test/utils'

const page: PagedResponse<WorkOrderListItem> = {
  items: [
    {
      id: 'wo-1', number: 1042, title: 'No heat from bedroom radiator', status: 'Resolved', priority: 'High',
      category: 'Heating', propertyName: 'Tidewater Court', requesterName: 'Alex MacIsaac',
      assignedTechnicianName: 'Sam Okafor', createdAt: '2026-01-15T14:00:00Z', updatedAt: '2026-01-15T18:00:00Z',
      dueAt: '2026-01-16T14:00:00Z', isOverdue: false, isDueSoon: false,
    },
  ],
  page: 1, pageSize: 20, totalCount: 1, totalPages: 1, asOf: '2026-01-15T19:00:00Z',
}

describe('Moving between pages', () => {
  it('moves focus to the new page and gives it its own title, but leaves focus alone when filters change', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders': () => json(200, page),
      'GET /api/work-orders/filter-options': () =>
        json(200, { properties: [], technicians: [], canFilterByTechnician: true, exportRowLimit: 5000 }),
      'GET /api/work-orders/wo-1': () => json(200, workOrder()),
      'GET /api/work-orders/wo-1/activity': () => json(200, activity),
      'GET /api/work-orders/wo-1/comments': noComments,
      'GET /api/work-orders/wo-1/attachments': noAttachments,
    })
    const { user } = renderApp('/work-orders')

    await waitFor(() => expect(document.title).toBe('All work orders · HarbourOps'))
    const priority = screen.getByLabelText('Priority')
    await user.selectOptions(priority, 'High')
    expect(priority).toHaveFocus() // a filter change is not a page change

    await user.click(await screen.findByRole('link', { name: 'No heat from bedroom radiator' }))

    await waitFor(() => expect(document.title).toBe('WO-1042: No heat from bedroom radiator · HarbourOps'))
    // Focus lands on the new page (its heading, or <main> while it was still loading), not on <body>.
    const focused = document.activeElement
    expect(focused === document.getElementById('main') || focused?.tagName === 'H1').toBe(true)
  })
})
