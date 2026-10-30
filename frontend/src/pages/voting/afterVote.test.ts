import { afterEach, describe, expect, it } from 'vitest'
import { MAX_TIMER_MS, msUntil, noteServerTime, resetServerClock, serverNow } from '../../lib/serverClock'
import { afterVote } from './lib'

const HOUR = 3_600_000
const iso = (ms: number) => new Date(ms).toISOString()

describe('after the vote', () => {
  afterEach(() => resetServerClock())

  it('is nothing while the vote is upcoming or open', () => {
    const now = Date.parse('2026-10-07T07:39:59.000Z')
    expect(afterVote({ state: 'Upcoming', endsAt: iso(now + HOUR) }, now)).toBeNull()
    expect(afterVote({ state: 'Open', endsAt: '2026-10-07T07:40:00Z' }, now)).toBeNull()
  })

  it('is the review stage from the exact end until the result is published', () => {
    const end = Date.parse('2026-10-07T07:40:00Z')
    expect(afterVote({ state: 'Open', endsAt: iso(end) }, end - 1)).toBeNull()
    // At the end itself, before the server has been asked again.
    expect(afterVote({ state: 'Open', endsAt: iso(end) }, end)).toBe('review')
    expect(afterVote({ state: 'Closed', endsAt: iso(end) }, end + HOUR)).toBe('review')
    expect(afterVote({ state: 'Closed', endsAt: iso(end), resultsPublished: false }, end + HOUR)).toBe('review')
  })

  it("is the result only on the server's word", () => {
    const end = Date.parse('2026-10-07T07:40:00Z')
    expect(afterVote({ state: 'Closed', endsAt: iso(end), resultsPublished: true }, end + HOUR)).toBe('results')
    // A window the server still calls open is never "results", whatever else the answer says.
    expect(afterVote({ state: 'Open', endsAt: iso(end), resultsPublished: true }, end + HOUR)).toBe('review')
  })

  it("reads the server's clock, not the device's", () => {
    const device = Date.now()
    // The server is an hour ahead of this device: by the server's clock the vote ended half an hour ago.
    noteServerTime(iso(device + HOUR), device)
    expect(serverNow() - Date.now()).toBeGreaterThan(HOUR - 1_000)
    expect(afterVote({ state: 'Open', endsAt: iso(device + HOUR / 2) })).toBe('review')

    // And one an hour behind: by the server's clock the end is still to come.
    noteServerTime(iso(device - HOUR), device)
    expect(afterVote({ state: 'Open', endsAt: iso(device - HOUR / 2) })).toBeNull()
  })

  it('ignores a missing or unreadable server time', () => {
    noteServerTime(undefined)
    noteServerTime('not a date')
    expect(Math.abs(serverNow() - Date.now())).toBeLessThan(50)
  })

  it('never asks a timer to wait longer than it can', () => {
    // A window that ends in 40 days would fire a plain timer at once, and again, and again.
    expect(msUntil(iso(Date.now() + 40 * 24 * HOUR))).toBe(MAX_TIMER_MS)
    expect(msUntil(iso(Date.now() - HOUR))).toBe(0)
  })
})
