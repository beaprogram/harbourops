import { describe, expect, it } from 'vitest'
import { json, mockApi, problem } from '../test/utils'
import { ApiError, apiRequest } from './client'

describe('apiRequest', () => {
  it('sends the antiforgery token on state-changing requests but not on reads', async () => {
    const api = mockApi({
      'GET /api/things': () => json(200, []),
      'POST /api/things': () => json(200, { ok: true }),
    })

    await apiRequest('/api/things')
    await apiRequest('/api/things', { method: 'POST', body: { name: 'x' } })

    const [read] = api.requestsTo('GET', '/api/things')
    const [write] = api.requestsTo('POST', '/api/things')
    expect(read?.headers['X-XSRF-TOKEN']).toBeUndefined()
    expect(write?.headers['X-XSRF-TOKEN']).toMatch(/^token-/)
    expect(write?.body).toEqual({ name: 'x' })
  })

  it('refreshes a rejected antiforgery token and retries exactly once', async () => {
    let attempts = 0
    const api = mockApi({
      'POST /api/things': () =>
        ++attempts === 1 ? problem(400, 'antiforgery_failed', 'Token expired') : json(200, { ok: true }),
    })

    await expect(apiRequest('/api/things', { method: 'POST', body: {} })).resolves.toEqual({ ok: true })

    const tokensUsed = api.requestsTo('POST', '/api/things').map((r) => r.headers['X-XSRF-TOKEN'])
    expect(tokensUsed).toHaveLength(2)
    expect(tokensUsed[0]).not.toEqual(tokensUsed[1])
  })

  it('gives up after one retry so a persistent failure surfaces to the user', async () => {
    const api = mockApi({ 'POST /api/things': () => problem(400, 'antiforgery_failed', 'Token rejected') })

    await expect(apiRequest('/api/things', { method: 'POST', body: {} })).rejects.toBeInstanceOf(ApiError)
    expect(api.requestsTo('POST', '/api/things')).toHaveLength(2)
  })

  it('turns Problem Details into an ApiError with field errors', async () => {
    mockApi({
      'POST /api/work-orders': () =>
        problem(400, 'validation_failed', 'One or more fields need attention.', { title: ['Title is required.'] }),
    })

    const error = await apiRequest('/api/work-orders', { method: 'POST', body: {} }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ status: 400, code: 'validation_failed', fieldErrors: { title: ['Title is required.'] } })
  })

  it('flags 409 responses as stale so the UI can offer a reload', async () => {
    mockApi({ 'POST /api/work-orders/1/close': () => problem(409, 'stale_version', 'Someone else updated this.') })

    const error = (await apiRequest('/api/work-orders/1/close', { method: 'POST', body: {} }).catch((e: unknown) => e)) as ApiError

    expect(error.isStale).toBe(true)
  })
})
