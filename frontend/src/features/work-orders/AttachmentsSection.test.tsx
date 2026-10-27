import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AttachmentItem, AttachmentList } from '../../api/types'
import { activity, manager, workOrder } from '../../test/fixtures'
import { json, mockApi, noComments, problem, renderApp } from '../../test/utils'

const LIST = 'GET /api/work-orders/wo-1/attachments'
const UPLOAD = 'POST /api/work-orders/wo-1/attachments'
const MAX = 5 * 1024 * 1024

function photo(id: string, name: string): AttachmentItem {
  return {
    id,
    fileName: name,
    mediaType: 'image/jpeg',
    sizeBytes: 120_000,
    width: 1600,
    height: 1200,
    previewWidth: 480,
    previewHeight: 360,
    uploadedBy: { id: 'u-req', displayName: 'Alex MacIsaac' },
    uploaderRole: 'Requester',
    uploadedAt: '2026-01-15T15:00:00Z',
    previewUrl: `/api/work-orders/wo-1/attachments/${id}/preview`,
    contentUrl: `/api/work-orders/wo-1/attachments/${id}/content`,
  }
}

function list(items: AttachmentItem[], overrides: Partial<AttachmentList> = {}): AttachmentList {
  return {
    items,
    limits: { maxPerWorkOrder: 5, maxFileSizeBytes: MAX, maxDimension: 10000, maxPixels: 40000000, acceptedTypes: ['image/jpeg', 'image/png'] },
    canUpload: true,
    cannotUploadReason: null,
    ...overrides,
  }
}

const baseHandlers = {
  'GET /api/auth/me': () => json(200, manager),
  'GET /api/work-orders/wo-1': () => json(200, workOrder({ status: 'InProgress', allowedActions: [] })),
  'GET /api/work-orders/wo-1/activity': () => json(200, activity),
  'GET /api/work-orders/wo-1/comments': noComments,
}

const jpeg = (name = 'leak.jpg', size = 2048) => new File([new Uint8Array(size)], name, { type: 'image/jpeg' })

describe('Photos on the work-order page', () => {
  beforeEach(() => {
    // jsdom has no object URLs; count them so the test can check they are released.
    URL.createObjectURL = vi.fn(() => `blob:preview-${Math.random()}`)
    URL.revokeObjectURL = vi.fn()
  })

  it('shows small previews only, and loads the full image when a photo is opened', async () => {
    mockApi({ ...baseHandlers, [LIST]: () => json(200, list([photo('a1', 'before.jpg'), photo('a2', 'after.jpg')])) })
    const { user } = renderApp('/work-orders/wo-1')

    const gallery = await screen.findByRole('region', { name: /Photos/ })
    const thumbnails = await within(gallery).findAllByRole('img')
    expect(thumbnails.map((img) => img.getAttribute('src'))).toEqual([
      '/api/work-orders/wo-1/attachments/a1/preview',
      '/api/work-orders/wo-1/attachments/a2/preview',
    ])
    expect(within(gallery).getByRole('heading', { name: 'Photos (2 of 5)' })).toBeInTheDocument()

    const second = within(gallery).getByRole('button', { name: /Photo 2 of 2, after\.jpg/ })
    await user.click(second)

    const dialog = await screen.findByRole('dialog', { name: 'Photo 2 of 2' })
    expect(within(dialog).getByRole('img')).toHaveAttribute('src', '/api/work-orders/wo-1/attachments/a2/content')
    expect(within(dialog).getByRole('button', { name: 'Close' })).toHaveFocus()
    expect(within(dialog).getByRole('link', { name: 'Download' })).toHaveAttribute(
      'href',
      '/api/work-orders/wo-1/attachments/a2/content?download=true',
    )

    await user.click(within(dialog).getByRole('button', { name: 'Close' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(within(gallery).getByRole('button', { name: /Photo 2 of 2/ })).toHaveFocus() // focus goes back
  })

  it('previews the chosen photo, uploads it once, and confirms', async () => {
    let finish!: () => void
    let uploaded = false
    const api = mockApi({
      ...baseHandlers,
      [LIST]: () => json(200, uploaded ? list([photo('a1', 'leak.jpg')]) : list([])),
      [UPLOAD]: () =>
        new Promise<Response>((resolve) => {
          finish = () => {
            uploaded = true
            resolve(json(201, photo('a1', 'leak.jpg')))
          }
        }),
    })
    const { user } = renderApp('/work-orders/wo-1')

    const input = await screen.findByLabelText('Add a photo')
    expect(screen.getByText(/JPEG or PNG, up to 5 MiB\. 5 photos remaining\./)).toBeInTheDocument()
    await user.upload(input, jpeg())
    expect(await screen.findByRole('img', { name: 'Preview of leak.jpg' })).toBeInTheDocument()

    const submit = screen.getByRole('button', { name: 'Upload photo' })
    await user.click(submit)
    await user.click(submit)
    expect(screen.getByRole('button', { name: 'Uploading…' })).toBeDisabled()
    finish()

    expect(await screen.findByText('Photo uploaded.')).toBeInTheDocument()
    const posts = api.requestsTo('POST', '/api/work-orders/wo-1/attachments')
    expect(posts).toHaveLength(1)
    expect(posts[0]!.headers['X-XSRF-TOKEN']).toMatch(/^token-/)
    expect((posts[0]!.rawBody as FormData).get('file')).toBeInstanceOf(File)
    expect(await screen.findByRole('button', { name: /Photo 1 of 1, leak\.jpg/ })).toBeInTheDocument()
    expect(screen.queryByRole('img', { name: 'Preview of leak.jpg' })).not.toBeInTheDocument()
    expect(URL.revokeObjectURL).toHaveBeenCalled() // the temporary preview URL is released
  })

  it('rejects oversized or non-image files before uploading', async () => {
    const api = mockApi({ ...baseHandlers, [LIST]: () => json(200, list([])) })
    renderApp('/work-orders/wo-1')
    const input = await screen.findByLabelText('Add a photo')

    fireEvent.change(input, { target: { files: [jpeg('huge.jpg', MAX + 1)] } })
    expect(await screen.findByText(/Photos must be 5 MiB or smaller\./)).toBeInTheDocument()
    expect(input).toHaveAttribute('aria-invalid', 'true')

    fireEvent.change(input, { target: { files: [new File(['<html></html>'], 'notes.html', { type: 'text/html' })] } })
    expect(await screen.findByText('Choose a JPEG or PNG photo.')).toBeInTheDocument()
    expect(api.requestsTo('POST', '/api/work-orders/wo-1/attachments')).toHaveLength(0)
  })

  it("keeps the chosen photo and explains the server's reason when an upload fails", async () => {
    mockApi({
      ...baseHandlers,
      [LIST]: () => json(200, list([])),
      [UPLOAD]: () => problem(415, 'unsupported_image', 'Only JPEG and PNG photos are supported.'),
    })
    const { user } = renderApp('/work-orders/wo-1')

    await user.upload(await screen.findByLabelText('Add a photo'), jpeg('renamed.jpg'))
    await user.click(screen.getByRole('button', { name: 'Upload photo' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Only JPEG and PNG photos are supported.')
    expect(screen.getByRole('img', { name: 'Preview of renamed.jpg' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Upload photo' })).toBeEnabled()
  })

  it('explains why no more photos can be added', async () => {
    mockApi({
      ...baseHandlers,
      [LIST]: () => json(200, list([photo('a1', 'a.jpg')], { canUpload: false, cannotUploadReason: 'This work order is closed, so photos can\'t be added.' })),
    })
    renderApp('/work-orders/wo-1')

    expect(await screen.findByText("This work order is closed, so photos can't be added.")).toBeInTheDocument()
    expect(screen.queryByLabelText('Add a photo')).not.toBeInTheDocument()
  })
})
