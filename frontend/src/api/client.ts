/**
 * The only place the frontend talks HTTP.
 *
 * Authentication: the session is an HttpOnly cookie that JavaScript cannot read. The browser
 * sends it automatically on same-origin requests; nothing is stored in localStorage.
 *
 * CSRF protection: state-changing requests must carry an antiforgery token in the X-XSRF-TOKEN
 * header. The token is fetched from the API, kept only in memory, and refreshed after sign-in and
 * sign-out because the server binds it to the current user.
 */

const ANTIFORGERY_HEADER = 'X-XSRF-TOKEN'

/** An error response from the API, parsed from RFC 9457 Problem Details. */
export class ApiError extends Error {
  readonly status: number
  readonly code: string | undefined
  readonly title: string
  readonly fieldErrors: Record<string, string[]>

  constructor(status: number, title: string, detail: string | undefined, code?: string, fieldErrors?: Record<string, string[]>) {
    super(detail ?? title)
    this.name = 'ApiError'
    this.status = status
    this.title = title
    this.code = code
    this.fieldErrors = fieldErrors ?? {}
  }

  /** Someone else changed the record after we loaded it (optimistic concurrency). */
  get isStale(): boolean {
    return this.status === 409
  }
}

let antiforgeryToken: string | null = null
let pendingTokenRequest: Promise<string> | null = null

/** Fetches a fresh antiforgery token. Call after sign-in and sign-out. */
export function refreshAntiforgeryToken(): Promise<string> {
  pendingTokenRequest ??= fetch('/api/antiforgery/token', {
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
  })
    .then(async (response) => {
      if (!response.ok) throw await toApiError(response)
      const body = (await response.json()) as { token: string }
      antiforgeryToken = body.token
      return body.token
    })
    .finally(() => {
      pendingTokenRequest = null
    })
  return pendingTokenRequest
}

/** Test helper: forget the in-memory token. */
export function resetAntiforgeryToken(): void {
  antiforgeryToken = null
  pendingTokenRequest = null
}

interface RequestOptions {
  method?: 'GET' | 'POST'
  body?: unknown
  /** Internal: whether a rejected antiforgery token may be refreshed and the request retried once. */
  retryWithFreshToken?: boolean
}

export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const method = options.method ?? 'GET'
  const isStateChanging = method !== 'GET'
  const headers: Record<string, string> = { Accept: 'application/json' }

  if (isStateChanging) {
    headers[ANTIFORGERY_HEADER] = antiforgeryToken ?? (await refreshAntiforgeryToken())
  }
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json'
  }

  const response = await fetch(path, {
    method,
    headers,
    credentials: 'same-origin',
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  })

  if (!response.ok) {
    const error = await toApiError(response)
    // An expired or identity-mismatched token is rejected before the server does anything,
    // so it is safe to refresh it and retry exactly once.
    if (isStateChanging && error.code === 'antiforgery_failed' && options.retryWithFreshToken !== false) {
      await refreshAntiforgeryToken()
      return apiRequest<T>(path, { ...options, retryWithFreshToken: false })
    }
    throw error
  }

  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

interface ProblemDetails {
  title?: string
  detail?: string
  code?: string
  errors?: Record<string, string[]>
}

async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails = {}
  try {
    problem = (await response.json()) as ProblemDetails
  } catch {
    // Not JSON (e.g. a proxy error page). Fall back to the status text.
  }
  return new ApiError(
    response.status,
    problem.title ?? (response.statusText || 'Request failed'),
    problem.detail,
    problem.code,
    problem.errors,
  )
}
