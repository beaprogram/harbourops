import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { activity, manager, technician, workOrder } from '../../test/fixtures'
import { json, mockApi, problem, renderApp } from '../../test/utils'

describe('WorkOrderDetailPage', () => {
  it('shows only the actions the server allows for this user', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, technician),
      'GET /api/work-orders/wo-1': () =>
        json(200, workOrder({ status: 'Assigned', allowedActions: ['Start'], resolutionNote: null, resolvedAt: null })),
      'GET /api/work-orders/wo-1/activity': () => json(200, activity.slice(0, 1)),
    })
    renderApp('/work-orders/wo-1')

    const actions = await screen.findByRole('region', { name: 'Actions' })
    expect(within(actions).getByRole('button', { name: 'Start work' })).toBeInTheDocument()
    expect(within(actions).queryByRole('button', { name: 'Close work order' })).not.toBeInTheDocument()
    expect(within(actions).queryByRole('button', { name: /Assign technician/ })).not.toBeInTheDocument()
  })

  it('explains a stale update (409) and recovers by loading the latest version', async () => {
    let detailLoads = 0
    const api = mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/wo-1': () =>
        json(200, ++detailLoads === 1 ? workOrder() : workOrder({ status: 'Closed', closedAt: '2026-01-15T19:00:00Z', allowedActions: [], version: 9 })),
      'GET /api/work-orders/wo-1/activity': () => json(200, activity),
      'POST /api/work-orders/wo-1/close': () =>
        problem(409, 'stale_version', 'Someone else updated this work order after you loaded it.'),
    })
    const { user } = renderApp('/work-orders/wo-1')

    await user.click(await screen.findByRole('button', { name: 'Close work order' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('This work order changed while you were viewing it')
    expect(api.requestsTo('POST', '/api/work-orders/wo-1/close')[0]?.body).toEqual({ version: 7 })

    await user.click(within(alert).getByRole('button', { name: 'Load latest version' }))

    expect(await screen.findByText('This work order is closed and can no longer be changed.')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(detailLoads).toBe(2)
  })

  it('requires a resolution note before resolving', async () => {
    const api = mockApi({
      'GET /api/auth/me': () => json(200, technician),
      'GET /api/work-orders/wo-1': () =>
        json(200, workOrder({ status: 'InProgress', allowedActions: ['Resolve'], resolutionNote: null, resolvedAt: null })),
      'GET /api/work-orders/wo-1/activity': () => json(200, activity.slice(0, 1)),
    })
    const { user } = renderApp('/work-orders/wo-1')

    await user.click(await screen.findByRole('button', { name: 'Mark resolved' }))

    expect(await screen.findByText('This field is required.')).toBeInTheDocument()
    expect(api.requestsTo('POST', '/api/work-orders/wo-1/resolve')).toHaveLength(0)
  })

  it('shows the activity history with Halifax times and notes', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/wo-1': () => json(200, workOrder()),
      'GET /api/work-orders/wo-1/activity': () => json(200, activity),
    })
    renderApp('/work-orders/wo-1')

    const timeline = await screen.findByRole('region', { name: 'Activity' })
    const entries = await within(timeline).findAllByRole('listitem')
    expect(entries).toHaveLength(2)
    expect(entries[1]).toHaveTextContent('By Sam Okafor (Technician)')
    expect(entries[1]).toHaveTextContent('Bled the radiator and replaced the valve.')
    expect(entries[1]).toHaveTextContent(/2:00\sp\.m\. AST/)
  })

  it('shows a not-found message without revealing whether the record exists', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/work-orders/wo-404': () => problem(404, 'not_found', 'Work order not found.'),
      'GET /api/work-orders/wo-404/activity': () => problem(404, 'not_found', 'Work order not found.'),
    })
    renderApp('/work-orders/wo-404')

    expect(await screen.findByRole('alert')).toHaveTextContent('Work order not found')
  })
})
