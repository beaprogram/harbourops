import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { Dashboard } from '../../api/types'
import { manager, technician } from '../../test/fixtures'
import { json, mockApi, problem, renderApp } from '../../test/utils'

const dashboard: Dashboard = {
  asOf: '2026-01-15T19:00:00Z',
  cards: [
    { key: 'active', title: 'Active requests', description: 'New, assigned, or in progress.', count: 9, listQuery: 'status=New&status=Assigned&status=InProgress' },
    { key: 'unassigned', title: 'Unassigned', description: 'No technician yet.', count: 2, listQuery: 'status=New&status=Assigned&status=InProgress&unassigned=true' },
    { key: 'overdue', title: 'Overdue', description: 'Past due.', count: 1, listQuery: 'deadline=overdue&sort=due&direction=asc' },
    { key: 'dueSoon', title: 'Due within 24 hours', description: 'Due soon.', count: 0, listQuery: 'deadline=dueSoon&sort=due&direction=asc' },
  ],
  workload: [
    {
      id: 'u-sam', displayName: 'Sam Okafor', isActive: true, notStartedCount: 1, inProgressCount: 2, activeCount: 3, overdueCount: 1,
      activeListQuery: 'technicianId=u-sam&status=Assigned&status=InProgress', overdueListQuery: 'technicianId=u-sam&deadline=overdue',
    },
    {
      id: 'u-jordan', displayName: 'Jordan Leblanc', isActive: true, notStartedCount: 0, inProgressCount: 0, activeCount: 0, overdueCount: 0,
      activeListQuery: 'technicianId=u-jordan', overdueListQuery: 'technicianId=u-jordan&deadline=overdue',
    },
  ],
  oldestOverdue: [
    { id: 'wo-5', number: 5, title: 'Bathroom exhaust fan rattling', propertyName: 'Quayside Lofts', priority: 'Normal', status: 'InProgress', assignedTechnicianName: 'Sam Okafor', dueAt: '2026-01-14T19:00:00Z' },
  ],
  overdueListQuery: 'deadline=overdue&sort=due&direction=asc',
}

describe('DashboardPage', () => {
  it('links every number to the matching filtered list', async () => {
    mockApi({ 'GET /api/auth/me': () => json(200, manager), 'GET /api/dashboard': () => json(200, dashboard) })
    renderApp('/dashboard')

    const overdueCard = await screen.findByRole('link', { name: /Overdue\s*1/ })
    expect(overdueCard).toHaveAttribute('href', '/work-orders?deadline=overdue&sort=due&direction=asc')
    expect(screen.getByRole('link', { name: /Active requests\s*9/ })).toHaveAttribute(
      'href',
      '/work-orders?status=New&status=Assigned&status=InProgress',
    )

    const workload = screen.getByRole('region', { name: 'Technician workload' })
    const samRow = within(workload).getByRole('row', { name: /Sam Okafor/ })
    expect(within(samRow).getByRole('link', { name: /3 active jobs for Sam Okafor/ })).toHaveAttribute(
      'href',
      '/work-orders?technicianId=u-sam&status=Assigned&status=InProgress',
    )
    // Technicians with nothing on their plate are shown as available, not hidden.
    expect(within(within(workload).getByRole('row', { name: /Jordan Leblanc/ })).getByText('Available')).toBeInTheDocument()

    const overdue = screen.getByRole('region', { name: 'Oldest overdue' })
    expect(within(overdue).getByText('Overdue by 1 day')).toBeInTheDocument()
  })

  it('shows a helpful error with a retry when the dashboard fails to load', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/dashboard': () => problem(500, 'server_error', 'Unexpected error.'),
    })
    renderApp('/dashboard')

    expect(await screen.findByRole('alert')).toHaveTextContent('The dashboard could not be loaded')
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })

  it('is not shown to other roles, and their browser never requests it', async () => {
    const api = mockApi({ 'GET /api/auth/me': () => json(200, technician) })
    renderApp('/dashboard')

    expect(await screen.findByRole('heading', { name: 'Not available' })).toBeInTheDocument()
    expect(api.requestsTo('GET', '/api/dashboard')).toHaveLength(0)
  })
})
