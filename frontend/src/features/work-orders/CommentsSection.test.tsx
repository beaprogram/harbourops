import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { CommentItem, CommentPage } from '../../api/types'
import { activity, manager, workOrder } from '../../test/fixtures'
import { json, mockApi, noAttachments, problem, renderApp } from '../../test/utils'

const DETAIL = 'GET /api/work-orders/wo-1'
const ACTIVITY = 'GET /api/work-orders/wo-1/activity'
const COMMENTS = 'GET /api/work-orders/wo-1/comments'
const POST_COMMENT = 'POST /api/work-orders/wo-1/comments'
const ATTACHMENTS = 'GET /api/work-orders/wo-1/attachments'

function comment(id: string, body: string, createdAt = '2026-01-15T15:00:00Z'): CommentItem {
  return { id, body, author: { id: 'u-req', displayName: 'Alex MacIsaac' }, authorRole: 'Requester', createdAt }
}

const page = (items: CommentItem[], hasOlder = false, totalCount = items.length): CommentPage => ({ items, hasOlder, totalCount })

const openWorkOrder = () => json(200, workOrder({ status: 'InProgress', allowedActions: [] }))

describe('Comments on the work-order page', () => {
  it('shows author, role, Halifax time, and the text exactly as written', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      [DETAIL]: openWorkOrder,
      [ACTIVITY]: () => json(200, activity),
      [ATTACHMENTS]: noAttachments,
      [COMMENTS]: () => json(200, page([comment('c-1', 'Still dripping.\n<b>Please</b> check tonight.')])),
    })
    renderApp('/work-orders/wo-1')

    const section = await screen.findByRole('region', { name: /Comments/ })
    const entry = await within(section).findByRole('listitem')
    expect(entry).toHaveTextContent('Alex MacIsaac')
    expect(entry).toHaveTextContent('Requester')
    expect(entry).toHaveTextContent('Jan 15, 2026, 11:00 a.m. AST')
    // Markup in a comment is shown as text, never rendered as HTML.
    expect(within(entry).getByText(/<b>Please<\/b> check tonight\./)).toBeInTheDocument()
    expect(entry.querySelector('b')).toBeNull()
  })

  it('posts once even if the button is pressed twice, then shows the comment and clears the box', async () => {
    let release!: () => void
    const api = mockApi({
      'GET /api/auth/me': () => json(200, manager),
      [DETAIL]: openWorkOrder,
      [ACTIVITY]: () => json(200, activity),
      [ATTACHMENTS]: noAttachments,
      [COMMENTS]: () => json(200, page([])),
      [POST_COMMENT]: () =>
        new Promise<Response>((resolve) => {
          release = () => resolve(json(201, { ...comment('c-new', 'Technician arriving at 3 p.m.'), author: { id: 'u-manager', displayName: 'Morgan Ellis' }, authorRole: 'Manager' }))
        }),
    })
    const { user } = renderApp('/work-orders/wo-1')

    const box = await screen.findByLabelText('Add a comment')
    await user.type(box, 'Technician arriving at 3 p.m.')
    const submit = screen.getByRole('button', { name: 'Post comment' })
    await user.click(submit)
    await user.click(submit)

    expect(screen.getByRole('button', { name: 'Posting…' })).toBeDisabled()
    release()

    expect(await screen.findByText('Technician arriving at 3 p.m.')).toBeInTheDocument()
    expect(api.requestsTo('POST', '/api/work-orders/wo-1/comments')).toHaveLength(1)
    await waitFor(() => expect(box).toHaveValue(''))
    expect(screen.getByText('Comment posted.')).toBeInTheDocument()
    expect(box).toHaveFocus()
    expect(screen.getByRole('heading', { name: 'Comments (1)' })).toBeInTheDocument()
  })

  it('keeps the typed text when posting fails', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      [DETAIL]: openWorkOrder,
      [ACTIVITY]: () => json(200, activity),
      [ATTACHMENTS]: noAttachments,
      [COMMENTS]: () => json(200, page([])),
      [POST_COMMENT]: () => problem(503, 'unavailable', 'The service is temporarily unavailable.'),
    })
    const { user } = renderApp('/work-orders/wo-1')

    const box = await screen.findByLabelText('Add a comment')
    await user.type(box, 'Please call before visiting.')
    await user.click(screen.getByRole('button', { name: 'Post comment' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Your comment was not posted')
    expect(box).toHaveValue('Please call before visiting.')
  })

  it('validates empty comments on the client without sending them', async () => {
    const api = mockApi({
      'GET /api/auth/me': () => json(200, manager),
      [DETAIL]: openWorkOrder,
      [ACTIVITY]: () => json(200, activity),
      [ATTACHMENTS]: noAttachments,
      [COMMENTS]: () => json(200, page([])),
    })
    const { user } = renderApp('/work-orders/wo-1')

    await user.type(await screen.findByLabelText('Add a comment'), '   ')
    await user.click(screen.getByRole('button', { name: 'Post comment' }))

    expect(await screen.findByText('Write a comment before posting.')).toBeInTheDocument()
    expect(api.requestsTo('POST', '/api/work-orders/wo-1/comments')).toHaveLength(0)
  })

  it('loads earlier comments above the ones already shown', async () => {
    const api = mockApi({
      'GET /api/auth/me': () => json(200, manager),
      [DETAIL]: openWorkOrder,
      [ACTIVITY]: () => json(200, activity),
      [ATTACHMENTS]: noAttachments,
      [COMMENTS]: (request) =>
        request.url.includes('before=c-2')
          ? json(200, page([comment('c-1', 'First message', '2026-01-15T14:30:00Z')], false, 3))
          : json(200, page([comment('c-2', 'Second message'), comment('c-3', 'Third message')], true, 3)),
    })
    const { user } = renderApp('/work-orders/wo-1')

    await user.click(await screen.findByRole('button', { name: 'Show earlier comments' }))

    await screen.findByText('First message')
    const section = screen.getByRole('region', { name: /Comments/ })
    expect(within(section).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      expect.stringContaining('First message'),
      expect.stringContaining('Second message'),
      expect.stringContaining('Third message'),
    ])
    expect(api.requestsTo('GET', '/api/work-orders/wo-1/comments').at(-1)?.url).toContain('before=c-2')
    expect(screen.queryByRole('button', { name: 'Show earlier comments' })).not.toBeInTheDocument()
  })

  it('does not offer a comment box on closed work orders', async () => {
    mockApi({
      'GET /api/auth/me': () => json(200, manager),
      [DETAIL]: () => json(200, workOrder({ status: 'Closed', closedAt: '2026-01-15T19:00:00Z', allowedActions: [] })),
      [ACTIVITY]: () => json(200, activity),
      [ATTACHMENTS]: noAttachments,
      [COMMENTS]: () => json(200, page([comment('c-1', 'Thanks for the quick fix!')])),
    })
    renderApp('/work-orders/wo-1')

    expect(await screen.findByText("This work order is closed, so new comments can't be added.")).toBeInTheDocument()
    expect(await screen.findByText('Thanks for the quick fix!')).toBeInTheDocument()
    expect(screen.queryByLabelText('Add a comment')).not.toBeInTheDocument()
  })
})
