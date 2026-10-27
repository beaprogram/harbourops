import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { manager } from '../../test/fixtures'
import { json, mockApi, problem, renderApp } from '../../test/utils'

const signedOut = () => problem(401, 'unauthenticated', 'Sign in to continue.')
const demoDisabled = () => problem(404, 'not_found', 'Demo mode is not enabled.')

describe('LoginPage', () => {
  it('validates the form before contacting the server', async () => {
    const api = mockApi({ 'GET /api/auth/me': signedOut, 'GET /api/demo/accounts': demoDisabled })
    const { user } = renderApp('/login')

    await user.click(await screen.findByRole('button', { name: 'Sign in' }))

    expect(await screen.findByText('Enter a valid email address.')).toBeInTheDocument()
    expect(screen.getByText('Enter your password.')).toBeInTheDocument()
    expect(screen.getByLabelText('Email')).toHaveAttribute('aria-invalid', 'true')
    expect(api.requestsTo('POST', '/api/auth/login')).toHaveLength(0)
  })

  it("shows the server's message when sign-in fails", async () => {
    mockApi({
      'GET /api/auth/me': signedOut,
      'GET /api/demo/accounts': demoDisabled,
      'POST /api/auth/login': () => problem(401, 'invalid_credentials', 'Email or password is incorrect.'),
    })
    const { user } = renderApp('/login')

    await user.type(await screen.findByLabelText('Email'), 'manager@harbourview.example')
    await user.type(screen.getByLabelText('Password'), 'wrong-password')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Email or password is incorrect.')
  })

  it("signs in, refreshes the antiforgery token, and opens the manager's dashboard", async () => {
    const api = mockApi({
      'GET /api/auth/me': signedOut,
      'GET /api/demo/accounts': demoDisabled,
      'POST /api/auth/login': () => json(200, manager),
      'GET /api/dashboard': () =>
        json(200, { asOf: '2026-01-15T19:00:00Z', cards: [], workload: [], oldestOverdue: [], overdueListQuery: 'deadline=overdue' }),
    })
    const { user, router } = renderApp('/login')

    await user.type(await screen.findByLabelText('Email'), manager.email)
    await user.type(screen.getByLabelText('Password'), 'HarbourDemo!2026')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/dashboard')
    // One token for the anonymous sign-in request, then a fresh one for the signed-in user.
    expect(api.requestsTo('GET', '/api/antiforgery/token')).toHaveLength(2)
  })

  it('lists demo accounts only in demo mode and fills the form from them', async () => {
    mockApi({
      'GET /api/auth/me': signedOut,
      'GET /api/demo/accounts': () =>
        json(200, {
          password: 'Demo-Only-Pass1!',
          accounts: [
            { email: 'tech.sam@harbourview.example', displayName: 'Sam Okafor', role: 'Technician', organizationName: 'Harbourview' },
          ],
        }),
    })
    const { user } = renderApp('/login')

    await user.click(await screen.findByRole('button', { name: /Sam Okafor/ }))

    await waitFor(() => expect(screen.getByLabelText('Email')).toHaveValue('tech.sam@harbourview.example'))
    expect(screen.getByLabelText('Password')).toHaveValue('Demo-Only-Pass1!')
    expect(screen.getByRole('heading', { name: /Demo accounts/ })).toBeInTheDocument()
  })
})
