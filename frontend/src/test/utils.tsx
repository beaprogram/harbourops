import { QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter } from 'react-router'
import { RouterProvider } from 'react-router/dom'
import { vi } from 'vitest'
import { createQueryClient } from '../app/queryClient'
import { routes } from '../app/router'

export interface RecordedRequest {
  method: string
  url: string
  headers: Record<string, string>
  body: unknown
}

type Handler = (request: RecordedRequest) => Response | Promise<Response>

export function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  })
}

export function problem(status: number, code: string, detail: string, errors?: Record<string, string[]>): Response {
  return json(status, { status, title: 'Problem', detail, code, errors })
}

/**
 * Replaces window.fetch with a router of "METHOD /path" → handler. Only the network is faked;
 * the real API client, antiforgery handling, TanStack Query, and React Router all run.
 * Unmatched requests fail loudly so tests cannot silently depend on missing data.
 */
export function mockApi(handlers: Record<string, Handler>) {
  const requests: RecordedRequest[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    const method = init?.method ?? 'GET'
    const request: RecordedRequest = {
      method,
      url,
      headers: (init?.headers ?? {}) as Record<string, string>,
      body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
    }
    requests.push(request)

    const handler = handlers[`${method} ${url}`] ?? handlers[`${method} ${url.split('?')[0]}`]
    if (handler) return handler(request)
    if (url === '/api/antiforgery/token') return json(200, { token: `token-${requests.length}`, headerName: 'X-XSRF-TOKEN' })
    throw new Error(`Unexpected request in test: ${method} ${url}`)
  })
  vi.stubGlobal('fetch', fetchMock)

  return {
    requests,
    requestsTo: (method: string, path: string) =>
      requests.filter((r) => r.method === method && r.url.split('?')[0] === path),
  }
}

/** Renders the real application routes at the given URL. */
export function renderApp(initialPath: string) {
  const queryClient = createQueryClient()
  queryClient.setDefaultOptions({ queries: { retry: false } })
  const router = createMemoryRouter(routes, { initialEntries: [initialPath] })
  const user = userEvent.setup()
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return { user, router, queryClient }
}
