import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { refreshSession, setAccessToken, subscribeSession, type RefreshOutcome } from '../api/client'
import { authApi } from '../api/endpoints'
import type { AuthResponse, LoginPayload, RegisterPayload, User } from '../api/types'

/**
 * 'unavailable': the session could not be checked just now — the refresh was throttled (429) or the server was out
 * of reach. That is not a sign-out: nothing is cleared, the check is repeated a few times on its own (honouring
 * Retry-After), and `retry` asks again at once.
 */
type Status = 'loading' | 'authenticated' | 'anonymous' | 'unavailable'

interface AuthState {
  status: Status
  user: User | null
  login: (payload: LoginPayload) => Promise<User>
  register: (payload: RegisterPayload) => Promise<User>
  logout: () => Promise<void>
  setUser: (user: User) => void
  /** A session obtained elsewhere (the QRLog sign-in) becomes the current one. */
  applySession: (session: AuthResponse) => void
  /** Checks the session again after it was 'unavailable'. */
  retry: () => void
}

const AuthContext = createContext<AuthState | null>(null)

/** Automatic checks before waiting for the person to ask: the first one and three more. */
const RESTORE_ATTEMPTS = 4

/** The wait before the next automatic check: what the server asked for (plus a little jitter), else a backoff. */
function restoreDelayMs(outcome: RefreshOutcome, attempt: number): number {
  if (outcome.kind === 'busy') return outcome.retryAfterMs + Math.floor(Math.random() * 500)
  return Math.min(15_000, 2_000 * 2 ** (attempt - 1))
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatusState] = useState<Status>('loading')
  const statusRef = useRef<Status>('loading')
  const [user, setUserState] = useState<User | null>(null)
  const [restoreRun, setRestoreRun] = useState(0)

  const setStatus = useCallback((next: Status) => {
    statusRef.current = next
    setStatusState(next)
  }, [])

  const apply = useCallback(
    (session: AuthResponse | null) => {
      setAccessToken(session?.accessToken ?? null)
      setUserState(session?.user ?? null)
      setStatus(session ? 'authenticated' : 'anonymous')
    },
    [setStatus],
  )

  // Restore the session from the refresh cookie (on start and on retry). A clear answer settles it; a throttled or
  // unreachable server leaves it 'unavailable' and asks again a bounded number of times — it never signs anyone out.
  useEffect(() => {
    subscribeSession(apply)
    let cancelled = false
    let timer: number | undefined
    const attempt = (n: number) => {
      refreshSession().then((outcome) => {
        if (cancelled) return
        // Signed in or out meanwhile (the login form, a QRLog sign-in): that answer stands.
        if (statusRef.current !== 'loading' && statusRef.current !== 'unavailable') return
        if (outcome.kind === 'session') return apply(outcome.session)
        if (outcome.kind === 'signed-out') return apply(null)
        setStatus('unavailable')
        if (n < RESTORE_ATTEMPTS) timer = window.setTimeout(() => attempt(n + 1), restoreDelayMs(outcome, n))
      })
    }
    attempt(1)
    return () => {
      cancelled = true
      window.clearTimeout(timer)
    }
  }, [apply, setStatus, restoreRun])

  // Renew the short-lived access token shortly before it expires while the tab is open.
  useEffect(() => {
    if (status !== 'authenticated') return
    const id = window.setInterval(() => {
      refreshSession().then((outcome) => {
        if (outcome.kind === 'session') apply(outcome.session)
        else if (outcome.kind === 'signed-out') apply(null)
        // Throttled or unreachable: keep the session; the next renewal, or the next request's 401, asks again.
      })
    }, 1000 * 60 * 12)
    return () => window.clearInterval(id)
  }, [status, apply])

  const login = useCallback(
    async (payload: LoginPayload) => {
      const session = await authApi.login(payload)
      apply(session)
      return session.user
    },
    [apply],
  )

  const register = useCallback(
    async (payload: RegisterPayload) => {
      const session = await authApi.register(payload)
      apply(session)
      return session.user
    },
    [apply],
  )

  const logout = useCallback(async () => {
    try {
      await authApi.logout()
    } finally {
      apply(null)
    }
  }, [apply])

  const setUser = useCallback((next: User) => setUserState(next), [])
  const applySession = useCallback((session: AuthResponse) => apply(session), [apply])
  const retry = useCallback(() => {
    if (statusRef.current === 'unavailable') setRestoreRun((n) => n + 1)
  }, [])

  const value = useMemo(
    () => ({ status, user, login, register, logout, setUser, applySession, retry }),
    [status, user, login, register, logout, setUser, applySession, retry],
  )
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside <AuthProvider>')
  return ctx
}
