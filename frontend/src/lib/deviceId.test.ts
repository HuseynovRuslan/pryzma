import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, refreshSession, setAccessToken } from '../api/client'
import { DEVICE_HEADER, deviceId, resetDeviceIdForTests } from './deviceId'

const fetchMock = vi.fn<typeof fetch>()

const sentDevice = (call: number) => new Headers(fetchMock.mock.calls[call][1]?.headers).get(DEVICE_HEADER)

beforeEach(() => {
  localStorage.clear()
  resetDeviceIdForTests()
  fetchMock.mockReset()
  fetchMock.mockImplementation(async () => new Response(null, { status: 204 }))
  vi.stubGlobal('fetch', fetchMock)
  setAccessToken(null)
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe("the browser's device id (D20)", () => {
  it('is made once, kept in this browser and has the shape the server accepts', () => {
    const first = deviceId()
    expect(first).toMatch(/^[A-Za-z0-9-]{8,64}$/)
    expect(localStorage.getItem('pryzma:device')).toBe(first)
    resetDeviceIdForTests()
    expect(deviceId()).toBe(first)
  })

  it('goes with every API call and with the session refresh, the same each time', async () => {
    await api('/api/x')
    await api('/api/y', { method: 'POST', body: {} })
    await refreshSession()
    const id = deviceId()
    expect([sentDevice(0), sentDevice(1), sentDevice(2)]).toEqual([id, id, id])
  })

  it('replaces a stored value of the wrong shape instead of sending it', () => {
    localStorage.setItem('pryzma:device', 'not a valid id!')
    const id = deviceId()
    expect(id).not.toBe('not a valid id!')
    expect(localStorage.getItem('pryzma:device')).toBe(id)
  })

  it('still works when the browser blocks storage', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked')
    })
    const id = deviceId()
    expect(id).toMatch(/^[A-Za-z0-9-]{8,64}$/)
    await api('/api/x')
    expect(sentDevice(0)).toBe(id)
  })
})
