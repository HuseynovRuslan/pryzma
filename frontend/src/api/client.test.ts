import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, apiUpload, setAccessToken } from './client'

const fetchMock = vi.fn<typeof fetch>()

beforeEach(() => {
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
  setAccessToken(null)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('what a successful answer carries', () => {
  it('takes a 202 without a body — "the link is on its way" — as success, not as a broken connection', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }))
    await expect(api('/api/auth/registration/start', { method: 'POST', body: { email: 'a@example.com' } })).resolves.toBeUndefined()
  })

  it('takes a 204 and an empty 200 the same way', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }))
    await expect(api('/api/contact', { method: 'POST', body: {} })).resolves.toBeUndefined()
    fetchMock.mockResolvedValueOnce(new Response('', { status: 200 }))
    await expect(api('/api/x', { method: 'POST', body: {} })).resolves.toBeUndefined()
  })

  it('reads the JSON when there is some', async () => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ proof: 'p-1' }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    await expect(api('/api/auth/registration/verify', { method: 'POST', body: { token: 't' } })).resolves.toEqual({ proof: 'p-1' })
  })

  it('turns a success it cannot read into a message, never a crash', async () => {
    fetchMock.mockResolvedValue(new Response('<!doctype html><title>x</title>', { status: 200 }))
    const err = await api('/api/x').catch((e: unknown) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).message).toMatch(/Gözlənilməz xəta/)
  })
})

describe('an upload whose answer cannot be read', () => {
  it('ends with a message instead of leaving the upload waiting forever', async () => {
    class FakeXhr {
      status = 201
      responseText = '<html>'
      upload = { onprogress: null as unknown }
      onload: (() => void) | null = null
      onerror: (() => void) | null = null
      onabort: (() => void) | null = null
      open() {}
      setRequestHeader() {}
      getResponseHeader() {
        return 'text/html'
      }
      send() {
        queueMicrotask(() => this.onload?.())
      }
      abort() {}
    }
    vi.stubGlobal('XMLHttpRequest', FakeXhr)
    const { promise } = apiUpload('/api/submissions/1/files', new FormData(), () => {})
    await expect(promise).rejects.toBeInstanceOf(ApiError)
  })
})
