/**
 * The only place the frontend talks HTTP.
 *
 * Authentication: the session is an HttpOnly cookie that the browser never exposes to JavaScript. The browser
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
/** Who this tab believes is signed in (undefined until known). See the session check in apiRequest. */
let expectedUserId: string | null | undefined

/** Records who the interface is showing as signed in (null when signed out). Called by the query client. */
export function setExpectedUser(id: string | null): void {
  expectedUserId = id
}

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
  expectedUserId = undefined
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
  // FormData (file uploads) is sent as-is: the browser sets the multipart Content-Type and boundary.
  const isForm = options.body instanceof FormData
  if (options.body !== undefined && !isForm) {
    headers['Content-Type'] = 'application/json'
  }

  const response = await fetch(path, {
    method,
    headers,
    credentials: 'same-origin',
    body: options.body === undefined ? undefined : isForm ? (options.body as FormData) : JSON.stringify(options.body),
  })

  if (!response.ok) {
    const error = await toApiError(response)
    // An expired or identity-mismatched token is rejected before the server does anything,
    // so it is safe to refresh it and retry exactly once.
    if (isStateChanging && error.code === 'antiforgery_failed' && options.retryWithFreshToken !== false) {
      // A mismatched token often means the person changed in another tab (sign-in, sign-out, or a demo persona
      // switch: the cookie is shared by every tab). Retrying would silently act as that other person, so check first.
      if (await signedInUserChanged()) {
        window.dispatchEvent(new Event(SESSION_CHANGED_EVENT))
        throw new ApiError(
          409,
          'Signed in as someone else',
          'Another tab changed who is signed in, so this was not sent. The page now shows who you are; check it and try again.',
          'session_changed',
        )
      }
      await refreshAntiforgeryToken()
      return apiRequest<T>(path, { ...options, retryWithFreshToken: false })
    }
    throw error
  }

  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

/**
 * Downloads a file (GET only) and returns it with the server's file name, so the caller can show
 * progress and errors instead of navigating the browser away. Errors become ApiError as usual.
 */
export async function apiDownload(path: string): Promise<{ blob: Blob; fileName: string | null }> {
  const response = await fetch(path, { credentials: 'same-origin' })
  if (!response.ok) throw await toApiError(response)
  const disposition = response.headers.get('Content-Disposition') ?? ''
  const fileName = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1] ?? /filename="?([^";]+)"?/i.exec(disposition)?.[1] ?? null
  return { blob: await response.blob(), fileName: fileName ? decodeURIComponent(fileName) : null }
}

/** Fired on window when another tab changed who is signed in. The query client refreshes everything. */
export const SESSION_CHANGED_EVENT = 'harbourops:session-changed'

async function signedInUserChanged(): Promise<boolean> {
  if (expectedUserId === undefined) return false
  const response = await fetch('/api/auth/me', { credentials: 'same-origin', headers: { Accept: 'application/json' } })
  const currentId = response.ok ? ((await response.json()) as { id: string }).id : null
  return currentId !== expectedUserId
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
