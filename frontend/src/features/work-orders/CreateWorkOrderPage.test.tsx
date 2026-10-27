import { screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { manager, workOrder } from '../../test/fixtures'
import { json, mockApi, noAttachments, noComments, problem, renderApp } from '../../test/utils'

const properties = [{ id: 'p-1', name: 'Quayside Lofts', addressLine: '18 Quayside Lane', community: 'Halifax' }]

describe('CreateWorkOrderPage', () => {
  it('shows client-side validation messages for every missing field', async () => {
    const api = mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/properties': () => json(200, properties),
    })
    const { user } = renderApp('/work-orders/new')

    await user.click(await screen.findByRole('button', { name: 'Submit request' }))

    expect(await screen.findByText('Choose a property.')).toBeInTheDocument()
    expect(screen.getByText('Choose a category.')).toBeInTheDocument()
    expect(screen.getByText('Title is required.')).toBeInTheDocument()
    expect(screen.getByText('Description is required.')).toBeInTheDocument()
    expect(api.requestsTo('POST', '/api/work-orders')).toHaveLength(0)
  })

  it('shows server validation errors next to the matching field', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/properties': () => json(200, properties),
      'POST /api/work-orders': () =>
        problem(400, 'validation_failed', 'One or more fields need attention.', {
          propertyId: ['Choose a property from your organization.'],
        }),
    })
    const { user } = renderApp('/work-orders/new')

    await user.selectOptions(await screen.findByLabelText('Property'), 'p-1')
    await user.selectOptions(screen.getByLabelText('Category'), 'Plumbing')
    await user.type(screen.getByLabelText('Title'), 'Leaking tap')
    await user.type(screen.getByLabelText('Description'), 'Drips constantly.')
    await user.click(screen.getByRole('button', { name: 'Submit request' }))

    expect(await screen.findByText('Choose a property from your organization.')).toBeInTheDocument()
    expect(screen.getByLabelText('Property')).toHaveAttribute('aria-invalid', 'true')
  })

  it('submits and opens the new work order', async () => {
    const api = mockApi({
      'GET /api/auth/me': () => json(200, manager),
      'GET /api/properties': () => json(200, properties),
      'POST /api/work-orders': () => json(201, workOrder({ id: 'wo-new', title: 'Leaking tap', status: 'New', allowedActions: [] })),
      'GET /api/work-orders/wo-new/activity': () => json(200, []),
      'GET /api/work-orders/wo-new/comments': noComments,
      'GET /api/work-orders/wo-new/attachments': noAttachments,
    })
    const { user, router } = renderApp('/work-orders/new')

    await user.selectOptions(await screen.findByLabelText('Property'), 'p-1')
    await user.selectOptions(screen.getByLabelText('Category'), 'Plumbing')
    await user.type(screen.getByLabelText('Title'), '  Leaking tap ')
    await user.type(screen.getByLabelText('Description'), 'Drips constantly.')
    await user.click(screen.getByRole('button', { name: 'Submit request' }))

    expect(await screen.findByText('Your request was submitted.')).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/work-orders/wo-new')
    expect(api.requestsTo('POST', '/api/work-orders')[0]?.body).toEqual({
      propertyId: 'p-1',
      category: 'Plumbing',
      title: 'Leaking tap',
      description: 'Drips constantly.',
    })
  })
})
