import { act, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, getAccessToken, refreshSession, retryAfterMs, setAccessToken, subscribeSession } from '../api/client'
import { AuthProvider, useAuth } from './AuthContext'
import { RequireAuth } from './RequireAuth'

const session = {
  accessToken: 'fresh-token',
  accessTokenExpiresAt: '',
  user: { id: 'u1', firstName: 'Nigar', lastName: 'Əliyeva', email: 'nigar@example.com', phone: null, roles: ['Participant'] },
}

function reply(status: number, body?: unknown, headers: Record<string, string> = {}) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  })
}

const fetchMock = vi.fn<typeof fetch>()

beforeEach(() => {
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
  setAccessToken(null)
  subscribeSession(() => {})
})

afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

function Probe() {
  const { status, user, retry } = useAuth()
  return (
    <>
      <output data-testid="status">{status}</output>
      <output data-testid="who">{user?.firstName ?? ''}</output>
      <button type="button" onClick={retry}>
        retry
      </button>
    </>
  )
}

const renderProvider = () =>
  render(
    <AuthProvider>
      <Probe />
    </AuthProvider>,
  )
const status = () => screen.getByTestId('status').textContent

/** Lets the fake clock run and every settled promise land. */
const elapse = (ms: number) =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(ms)
  })

describe('what a refresh answer means', () => {
  it('reads 429 as "try later": the token stays and Retry-After is honoured', async () => {
    setAccessToken('still-valid')
    fetchMock.mockResolvedValueOnce(reply(429, { status: 429, title: 'Çox sayda sorğu' }, { 'Retry-After': '7' }))
    expect(await refreshSession()).toEqual({ kind: 'busy', retryAfterMs: 7_000 })
    expect(getAccessToken()).toBe('still-valid')
  })

  it('reads a genuine 401 as signed out and forgets the token', async () => {
    setAccessToken('revoked')
    fetchMock.mockResolvedValueOnce(reply(401))
    expect(await refreshSession()).toEqual({ kind: 'signed-out' })
    expect(getAccessToken()).toBeNull()
  })

  it('reads network and server trouble as unreachable, not as signed out', async () => {
    setAccessToken('kept')
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))
    expect(await refreshSession()).toEqual({ kind: 'unreachable' })
    fetchMock.mockResolvedValueOnce(reply(503))
    expect(await refreshSession()).toEqual({ kind: 'unreachable' })
    expect(getAccessToken()).toBe('kept')
  })

  it('parses Retry-After as seconds or a date, within sane bounds', () => {
    const now = Date.parse('2026-09-19T08:00:00Z')
    expect(retryAfterMs('12', now)).toBe(12_000)
    expect(retryAfterMs('0', now)).toBe(1_000)
    expect(retryAfterMs('100000', now)).toBe(120_000)
    expect(retryAfterMs('Sat, 19 Sep 2026 08:00:30 GMT', now)).toBe(30_000)
    expect(retryAfterMs(null, now)).toBe(5_000)
    expect(retryAfterMs('soon', now)).toBe(5_000)
  })
})

describe('restoring the session on page load', () => {
  it('a throttled restore is not a sign-out: "unavailable", then signed in on the retry the server allows', async () => {
    vi.useFakeTimers()
    vi.spyOn(Math, 'random').mockReturnValue(0)
    fetchMock.mockResolvedValueOnce(reply(429, undefined, { 'Retry-After': '2' })).mockResolvedValueOnce(reply(200, session))
    renderProvider()
    await elapse(0)
    expect(status()).toBe('unavailable')

    await elapse(1_999)
    expect(fetchMock).toHaveBeenCalledTimes(1) // Retry-After is honoured, not hammered
    await elapse(1)
    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(status()).toBe('authenticated')
    expect(screen.getByTestId('who')).toHaveTextContent('Nigar')
  })

  it('a genuine 401 settles as signed out at once, without retries', async () => {
    fetchMock.mockResolvedValueOnce(reply(401))
    renderProvider()
    await waitFor(() => expect(status()).toBe('anonymous'))
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('retries a bounded number of times, then waits for the person — no refresh storm', async () => {
    vi.useFakeTimers()
    vi.spyOn(Math, 'random').mockReturnValue(0)
    fetchMock.mockImplementation(async () => reply(429, undefined, { 'Retry-After': '1' }))
    renderProvider()
    await elapse(10 * 60_000)
    expect(fetchMock).toHaveBeenCalledTimes(4)
    expect(status()).toBe('unavailable')

    fetchMock.mockImplementation(async () => reply(200, session))
    await act(async () => {
      screen.getByRole('button', { name: 'retry' }).click()
      await vi.advanceTimersByTimeAsync(0)
    })
    expect(fetchMock).toHaveBeenCalledTimes(5)
    expect(status()).toBe('authenticated')
  })

  it('a protected page waits with a notice instead of sending the person to the login page', async () => {
    vi.useFakeTimers()
    fetchMock.mockResolvedValue(reply(429, undefined, { 'Retry-After': '30' }))
    render(
      <AuthProvider>
        <MemoryRouter initialEntries={['/my/submissions']}>
          <Routes>
            <Route
              path="/my/submissions"
              element={
                <RequireAuth>
                  <p>İşlərim səhifəsi</p>
                </RequireAuth>
              }
            />
            <Route path="/login" element={<p>Giriş səhifəsi</p>} />
          </Routes>
        </MemoryRouter>
      </AuthProvider>,
    )
    await elapse(0)
    expect(screen.getByRole('heading', { name: 'Giriş vəziyyəti bu an yoxlanıla bilmədi' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'İndi yenidən yoxla' })).toBeInTheDocument()
    expect(screen.queryByText('Giriş səhifəsi')).not.toBeInTheDocument()
  })
})

describe('an API call made with an expired access token', () => {
  it('keeps the session when the renewal is throttled; only that request fails, and says so', async () => {
    const changes: unknown[] = []
    subscribeSession((s) => changes.push(s))
    setAccessToken('expired')
    fetchMock.mockResolvedValueOnce(reply(401)).mockResolvedValueOnce(reply(429, undefined, { 'Retry-After': '3' }))
    const error = await api('/api/me/submissions').catch((e: unknown) => e)
    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(429)
    expect((error as ApiError).message).toMatch(/hesabınızdan çıxmamısınız/)
    expect(changes).toEqual([]) // nobody was signed out
    expect(getAccessToken()).toBe('expired')
  })

  it('signs out when the renewal says the session is gone', async () => {
    const changes: unknown[] = []
    subscribeSession((s) => changes.push(s))
    setAccessToken('expired')
    fetchMock.mockResolvedValueOnce(reply(401)).mockResolvedValueOnce(reply(401))
    await expect(api('/api/me/submissions')).rejects.toMatchObject({ status: 401 })
    expect(changes).toEqual([null])
  })
})
