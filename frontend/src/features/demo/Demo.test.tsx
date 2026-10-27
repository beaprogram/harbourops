import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { CurrentUser, DemoSession } from '../../api/types'
import { json, mockApi, problem, renderApp } from '../../test/utils'

const expiresAt = new Date(Date.now() + 47 * 60_000).toISOString()

const personas: NonNullable<DemoSession['session']>['personas'] = [
  { key: 'manager', displayName: 'Morgan Ellis', role: 'Manager', organizationName: 'Harbourview Property Group', description: 'Assign and close work.' },
  { key: 'technician', displayName: 'Sam Okafor', role: 'Technician', organizationName: 'Harbourview Property Group', description: 'Start and resolve jobs.' },
  { key: 'requester', displayName: 'Riley Chen', role: 'Requester', organizationName: 'Harbourview Property Group', description: 'Report problems.' },
  { key: 'otherManager', displayName: 'Dana Fraser', role: 'Manager', organizationName: 'Cove Commons Housing Co-op', description: 'A different organization.' },
]

const usage = { workOrders: 0, maxWorkOrders: 20, comments: 0, maxComments: 60, attachments: 0, maxAttachments: 12, storedBytes: 0, maxStoredBytes: 26214400, exports: 0, maxExports: 20 }

function persona(key: 'manager' | 'otherManager'): CurrentUser {
  const p = personas.find((x) => x.key === key)!
  return {
    id: `u-${key}`,
    email: `${key}@sandbox.harbourops.example`,
    displayName: p.displayName,
    role: p.role,
    organization: { id: key === 'manager' ? 'org-a' : 'org-b', name: p.organizationName },
    demo: { expiresAt },
  }
}

function activeSession(current: 'manager' | 'otherManager' | null): DemoSession {
  return { status: 'active', lifetimeMinutes: 60, session: { expiresAt, currentPersona: current, personas, usage } }
}

const emptyDashboard = () =>
  json(200, { asOf: '2026-01-15T19:00:00Z', cards: [], workload: [], oldestOverdue: [], overdueListQuery: 'deadline=overdue' })
const signedOut = () => problem(401, 'unauthenticated', 'Sign in to continue.')
const noSharedAccounts = () => problem(404, 'not_found', 'Demo mode is not enabled.')

describe('Demo sandboxes', () => {
  it('offers "Try the demo", starts a private session with a fresh token, and shows the demo banner', async () => {
    const api = mockApi({
      'GET /api/auth/me': signedOut,
      'GET /api/demo/accounts': noSharedAccounts,
      'GET /api/demo/session': () => json(200, { status: 'none', lifetimeMinutes: 60, session: null }),
      'POST /api/demo/sessions': () => json(200, { user: persona('manager'), session: activeSession('manager') }),
      'GET /api/dashboard': emptyDashboard,
    })
    const { user, router } = renderApp('/login')

    expect(await screen.findByText(/This public demo has no sign-in accounts/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Try the demo' }))

    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/dashboard')
    const banner = screen.getByRole('region', { name: 'Demo session' })
    expect(banner).toHaveTextContent('Portfolio demo · fictional data · temporary session.')
    expect(banner).toHaveTextContent('Switching person applies to every tab in this browser.')
    expect(banner).toHaveTextContent(/Ends at .* \(in 4\d min\)/)
    // The start request carried an antiforgery token, and a new token was fetched for the new identity.
    const [start] = api.requestsTo('POST', '/api/demo/sessions')
    expect(start?.headers['X-XSRF-TOKEN']).toMatch(/^token-/)
    expect(api.requestsTo('GET', '/api/antiforgery/token')).toHaveLength(2)
  })

  it('switches person by key only, refreshes the token, and announces the change', async () => {
    let current: 'manager' | 'otherManager' = 'manager'
    const api = mockApi({
      'GET /api/auth/me': () => json(200, persona(current)),
      'GET /api/demo/session': () => json(200, activeSession(current)),
      'POST /api/demo/persona': () => {
        current = 'otherManager'
        return json(200, { user: persona('otherManager'), session: activeSession('otherManager') })
      },
      'GET /api/dashboard': emptyDashboard,
    })
    const { user } = renderApp('/dashboard')

    const banner = await screen.findByRole('region', { name: 'Demo session' })
    const select = await within(banner).findByLabelText('Act as')
    expect(within(banner).getByRole('button', { name: 'Switch' })).toBeDisabled() // already Morgan
    await user.selectOptions(select, 'otherManager')
    expect(within(banner).getByText('A different organization.')).toBeInTheDocument()
    await user.click(within(banner).getByRole('button', { name: 'Switch' }))

    expect(await within(banner).findByRole('status')).toHaveTextContent(
      'You are now Dana Fraser, Manager at Cove Commons Housing Co-op.',
    )
    expect(await screen.findByText('Dana Fraser')).toBeInTheDocument() // the header follows
    const [switched] = api.requestsTo('POST', '/api/demo/persona')
    expect(switched?.body).toEqual({ persona: 'otherManager' })
    expect(api.requestsTo('GET', '/api/antiforgery/token').length).toBeGreaterThanOrEqual(2)
  })

  it('asks before resetting, keeps keyboard focus on the choice, and resets only on confirmation', async () => {
    const api = mockApi({
      'GET /api/auth/me': () => json(200, persona('manager')),
      'GET /api/demo/session': () => json(200, activeSession('manager')),
      'POST /api/demo/reset': () => json(200, { user: persona('manager'), session: activeSession('manager') }),
      'GET /api/dashboard': emptyDashboard,
    })
    const { user } = renderApp('/dashboard')
    const banner = await screen.findByRole('region', { name: 'Demo session' })

    await user.click(within(banner).getByRole('button', { name: 'Reset my demo' }))
    expect(within(banner).getByRole('button', { name: 'Reset now' })).toHaveFocus()
    expect(banner).toHaveTextContent('Reset replaces all of your demo data with a fresh copy.')
    await user.click(within(banner).getByRole('button', { name: 'Cancel' }))
    expect(within(banner).getByRole('button', { name: 'Reset my demo' })).toHaveFocus()
    expect(api.requestsTo('POST', '/api/demo/reset')).toHaveLength(0)

    await user.click(within(banner).getByRole('button', { name: 'Reset my demo' }))
    await user.click(within(banner).getByRole('button', { name: 'Reset now' }))
    expect(await within(banner).findByRole('status')).toHaveTextContent('Your demo has fresh data.')
    expect(api.requestsTo('POST', '/api/demo/reset')).toHaveLength(1)
  })

  it('ending the demo leads to an accessible ended screen with a way to start again', async () => {
    let ended = false
    mockApi({
      'GET /api/auth/me': () => (ended ? signedOut() : json(200, persona('manager'))),
      'GET /api/demo/session': () =>
        json(200, ended ? { status: 'none', lifetimeMinutes: 60, session: null } : activeSession('manager')),
      'POST /api/demo/end': () => {
        ended = true
        return new Response(null, { status: 204 })
      },
      'POST /api/demo/sessions': () => {
        ended = false
        return json(200, { user: persona('manager'), session: activeSession('manager') })
      },
      'GET /api/dashboard': emptyDashboard,
    })
    const { user, router } = renderApp('/dashboard')
    const banner = await screen.findByRole('region', { name: 'Demo session' })

    await user.click(within(banner).getByRole('button', { name: 'End demo' }))

    const heading = await screen.findByRole('heading', { level: 1, name: 'Your demo session has ended' })
    await waitFor(() => expect(heading).toHaveFocus())
    expect(router.state.location.pathname).toBe('/demo/ended')
    expect(document.title).toBe('Demo ended · HarbourOps')
    await user.click(screen.getByRole('button', { name: 'Start a new demo' }))
    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeInTheDocument()
  })

  it('sends a visitor whose demo ended elsewhere to the ended screen, not the sign-in page', async () => {
    mockApi({
      'GET /api/auth/me': () => problem(401, 'demo_expired', 'This demo session has ended.'),
      'GET /api/demo/session': () => json(200, { status: 'ended', lifetimeMinutes: 60, session: null }),
    })
    const { router } = renderApp('/work-orders')

    expect(await screen.findByRole('heading', { name: 'Your demo session has ended' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/demo/ended')
  })
})
