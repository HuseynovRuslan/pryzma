import type { AuthResponse } from './types'

/** Field-level messages from ValidationProblemDetails, keyed by camelCase field name ('' = form level). */
export type FieldErrors = Record<string, string[]>

export class ApiError extends Error {
  readonly status: number
  readonly fieldErrors: FieldErrors

  constructor(status: number, message: string, fieldErrors: FieldErrors = {}) {
    super(message)
    this.status = status
    this.fieldErrors = fieldErrors
  }
}

const NETWORK_ERROR = 'Serverlə əlaqə qurulmadı. İnternet bağlantısını yoxlayıb yenidən cəhd edin.'
const GENERIC_ERROR = 'Gözlənilməz xəta baş verdi. Bir az sonra yenidən cəhd edin.'
const SESSION_RENEWAL_DELAYED = 'Sessiya bu an yenilənmədi. Bir neçə saniyədən sonra yenidən cəhd edin — hesabınızdan çıxmamısınız.'

// The access token lives only in memory. The refresh token is an HttpOnly cookie the script never sees.
let accessToken: string | null = null
let refreshInFlight: Promise<RefreshOutcome> | null = null
let onSessionChange: (session: AuthResponse | null) => void = () => {}

export function setAccessToken(token: string | null) {
  accessToken = token
}

export function getAccessToken() {
  return accessToken
}

export function subscribeSession(listener: (session: AuthResponse | null) => void) {
  onSessionChange = listener
}

/**
 * What a refresh says about the session. Only 'signed-out' — a 401: no cookie, or one that is invalid, expired or
 * revoked — means the session is gone. 'busy' (429, throttled) and 'unreachable' (network or server trouble) say
 * nothing about the credentials: the caller keeps what it has and asks again later.
 */
export type RefreshOutcome =
  | { kind: 'session'; session: AuthResponse }
  | { kind: 'signed-out' }
  | { kind: 'busy'; retryAfterMs: number }
  | { kind: 'unreachable' }

/** Retry-After (seconds or an HTTP date) in milliseconds, kept within 1 s … 2 min; 5 s when absent or unreadable. */
export function retryAfterMs(header: string | null, now = Date.now()): number {
  const clamp = (ms: number) => Math.min(120_000, Math.max(1_000, ms))
  if (!header?.trim()) return 5_000
  const seconds = Number(header)
  if (Number.isFinite(seconds)) return clamp(seconds * 1000)
  const date = Date.parse(header)
  return Number.isNaN(date) ? 5_000 : clamp(date - now)
}

/**
 * Exchanges the refresh cookie for a new access token. Concurrent callers share one request, so
 * StrictMode double effects or parallel 401s never race each other into a token-reuse lockout.
 */
export function refreshSession(): Promise<RefreshOutcome> {
  refreshInFlight ??= (async (): Promise<RefreshOutcome> => {
    try {
      const res = await fetch('/api/auth/refresh', { method: 'POST', credentials: 'same-origin' })
      if (res.ok) {
        const session = (await res.json()) as AuthResponse
        setAccessToken(session.accessToken)
        return { kind: 'session', session }
      }
      if (res.status === 401) {
        setAccessToken(null)
        return { kind: 'signed-out' }
      }
      if (res.status === 429) return { kind: 'busy', retryAfterMs: retryAfterMs(res.headers.get('Retry-After')) }
      return { kind: 'unreachable' }
    } catch {
      return { kind: 'unreachable' }
    } finally {
      refreshInFlight = null
    }
  })()
  return refreshInFlight
}

/**
 * After a 401 from the API: renews the session once. True when the request may be repeated with the new token,
 * false when the session is gone (the app signs out). Throttled or unreachable: the session is kept and only this
 * request fails — with a message that says so.
 */
async function renewAfter401(): Promise<boolean> {
  const outcome = await refreshSession()
  if (outcome.kind === 'session') {
    onSessionChange(outcome.session)
    return true
  }
  if (outcome.kind === 'signed-out') {
    onSessionChange(null)
    return false
  }
  throw new ApiError(outcome.kind === 'busy' ? 429 : 0, SESSION_RENEWAL_DELAYED)
}

interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  body?: unknown
  signal?: AbortSignal
}

export async function api<T>(path: string, options: RequestOptions = {}, retried = false): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`

  let res: Response
  try {
    res = await fetch(path, {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      credentials: 'same-origin',
      signal: options.signal,
    })
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') throw err
    throw new ApiError(0, NETWORK_ERROR)
  }

  if (res.status === 401 && !retried && accessToken && !path.startsWith('/api/auth/')) {
    if (await renewAfter401()) return api<T>(path, options, true)
  }

  if (!res.ok) throw await toApiError(res)
  return readBody<T>(res)
}

/**
 * A success's body as JSON, or undefined when it has none: a 204, and the 202 "on its way" answers (a registration
 * link, a confirmation mail) that carry no body. Reading those as JSON threw, and from 6 to 23 October 2026 the
 * registration form said "Bağlantı alınmadı" for every link it had just sent.
 */
async function readBody<T>(res: Response): Promise<T> {
  const text = await res.text()
  if (!text) return undefined as T
  try {
    return JSON.parse(text) as T
  } catch {
    throw new ApiError(res.status, GENERIC_ERROR)
  }
}

/** Authenticated binary GET (e.g. an uploaded image for a thumbnail); <img src> cannot send a bearer token. */
export async function apiBlob(path: string, signal?: AbortSignal, retried = false): Promise<Blob> {
  const res = await fetch(path, {
    headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
    credentials: 'same-origin',
    signal,
  })
  if (res.status === 401 && !retried && accessToken) {
    if (await renewAfter401()) return apiBlob(path, signal, true)
  }
  if (!res.ok) throw await toApiError(res)
  return res.blob()
}

export interface UploadHandle<T> {
  promise: Promise<T>
  abort: () => void
}

/**
 * Multipart upload with progress reporting (fetch cannot report upload progress).
 * Like {@link api}, it refreshes the session once on 401.
 */
export function apiUpload<T>(path: string, form: FormData, onProgress: (fraction: number) => void): UploadHandle<T> {
  let xhr: XMLHttpRequest | null = null
  let aborted = false

  const send = (retried: boolean): Promise<T> =>
    new Promise<T>((resolve, reject) => {
      const request = new XMLHttpRequest()
      xhr = request
      request.open('POST', path)
      if (accessToken) request.setRequestHeader('Authorization', `Bearer ${accessToken}`)
      request.setRequestHeader('Accept', 'application/json')
      request.upload.onprogress = (e) => {
        if (e.lengthComputable) onProgress(e.loaded / e.total)
      }
      request.onerror = () => reject(new ApiError(0, NETWORK_ERROR))
      request.onabort = () => reject(new DOMException('Upload aborted', 'AbortError'))
      request.onload = async () => {
        const res = new Response(request.responseText || null, {
          status: request.status,
          headers: { 'Content-Type': request.getResponseHeader('Content-Type') ?? 'application/json' },
        })
        if (res.status === 401 && !retried && accessToken) {
          let renewed: boolean
          try {
            renewed = await renewAfter401()
          } catch (err) {
            reject(err)
            return
          }
          if (renewed && !aborted) {
            send(true).then(resolve, reject)
            return
          }
        }
        if (!res.ok) {
          reject(await toApiError(res))
          return
        }
        // A body that cannot be read must end the upload with a message, not leave its spinner turning.
        readBody<T>(res).then(resolve, reject)
      }
      request.send(form)
    })

  return {
    promise: send(false),
    abort: () => {
      aborted = true
      xhr?.abort()
    },
  }
}

async function toApiError(res: Response): Promise<ApiError> {
  let title = ''
  let fieldErrors: FieldErrors = {}
  try {
    const body = await res.json()
    title = typeof body?.title === 'string' ? body.title : ''
    if (body?.errors && typeof body.errors === 'object') fieldErrors = normaliseKeys(body.errors)
  } catch {
    /* non-JSON error body */
  }

  if (!title || /^(One or more|Unauthorized|Not Found|Bad Request|Payload Too Large|Request Entity)/i.test(title)) {
    title =
      res.status === 401
        ? 'Davam etmək üçün hesabınıza daxil olun.'
        : res.status === 403
          ? 'Bu əməliyyat üçün icazəniz yoxdur.'
          : res.status === 404
            ? 'Axtardığınız məlumat tapılmadı.'
            : res.status === 413
              ? 'Fayl çox böyükdür.'
              : GENERIC_ERROR
  }
  return new ApiError(res.status, title, fieldErrors)
}

function normaliseKeys(errors: Record<string, string[]>): FieldErrors {
  const out: FieldErrors = {}
  for (const [key, value] of Object.entries(errors)) {
    const k = key.length ? key[0].toLowerCase() + key.slice(1) : ''
    out[k] = [...(out[k] ?? []), ...value]
  }
  return out
}
