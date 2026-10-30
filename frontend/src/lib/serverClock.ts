/**
 * The server's clock, as far as this page knows it.
 *
 * Whether voting is open is the server's answer, never the device's: a phone set an hour behind must not keep a
 * closed vote looking open, and one set ahead must not close it early. Every voting summary carries the server's
 * own time (`serverNow`); the difference to this device's clock is kept here, and everything that counts down or
 * waits for a boundary reads `serverNow()` instead of `Date.now()`.
 *
 * The network delay of the answer is left in: it is milliseconds against a device clock that can be wrong by hours,
 * and erring late by it only means asking the server a moment after the boundary rather than before.
 */
let offsetMs = 0

/** Records the server's time from an answer that has just arrived. Ignores a missing or unreadable value. */
export function noteServerTime(serverIso: string | null | undefined, receivedAt: number = Date.now()): void {
  if (!serverIso) return
  const server = Date.parse(serverIso)
  if (Number.isNaN(server)) return
  offsetMs = server - receivedAt
}

/** Now, by the server's clock (ms since the epoch). The device's clock until a summary has been read. */
export const serverNow = (): number => Date.now() + offsetMs

/** For tests: forget what the last answer said. */
export function resetServerClock(): void {
  offsetMs = 0
}

/**
 * The longest wait a browser timer takes: a longer delay fires at once. A boundary further away than this (about
 * 24.8 days) is waited for in steps, so a long window never turns into an immediate, repeated ask.
 */
export const MAX_TIMER_MS = 2_147_483_647

/** Milliseconds from now (by the server's clock) until `iso`, within what a timer can wait; 0 once it has passed. */
export function msUntil(iso: string): number {
  return Math.min(Math.max(0, Date.parse(iso) - serverNow()), MAX_TIMER_MS)
}
