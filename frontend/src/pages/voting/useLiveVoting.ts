import { useCallback, useEffect, useReducer, useRef, useState } from 'react'
import { ApiError } from '../../api/client'
import { votingApi } from '../../api/endpoints'
import type { Voting } from '../../api/types'
import type { AsyncState } from '../../lib/useApi'
import { msUntil } from '../../lib/serverClock'

/** The longest wait between two asks while the server has not yet caught up with a boundary. */
const MAX_BACKOFF_MS = 30_000

/** A tab shown again asks once; asking more often than this on quick switches would add nothing. */
const VISIBLE_REFRESH_MIN_MS = 15_000

/**
 * In review, how often a visible page asks whether the result has been published. The result is announced once,
 * by a person, at no set time: a few minutes late is fine, and a request every few minutes from an open tab costs
 * nothing. A hidden tab does not ask; it asks when shown again.
 */
export const REVIEW_REFRESH_MS = 5 * 60_000

/**
 * A competition's voting summary that follows the vote as it moves, without a reload and without polling.
 *
 * - Read once on mount.
 * - At the window's next boundary (its opening or its end), by the server's clock, the page re-renders at once
 *   — so an ended window stops looking open even if the network is down — and asks the server again. Should the
 *   answer still show the old state (a request landing a moment early), it asks again with a growing pause.
 * - When the tab is shown again, it asks once: a phone left on the page overnight wakes up to today's state.
 * - In review, a visible page asks every few minutes whether the result has been published (REVIEW_REFRESH_MS).
 * - `refresh()` asks on demand — after a refused vote, or when the live ranking reports another state.
 *
 * A failed refresh keeps the last answer on screen; it never turns into an error page, and it never becomes a
 * claim the server did not make (see `afterVote`).
 */
export function useLiveVoting(slug: string, enabled = true) {
  const [state, setState] = useState<AsyncState<Voting>>({ status: 'loading', data: undefined, error: undefined })
  const [attempt, setAttempt] = useState(0)
  const [, rerender] = useReducer((n: number) => n + 1, 0)
  const lastAsked = useRef(0)

  useEffect(() => {
    if (!enabled) return
    const controller = new AbortController()
    setState({ status: 'loading', data: undefined, error: undefined })
    lastAsked.current = Date.now()
    votingApi
      .summary(slug, controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setState({ status: 'success', data, error: undefined })
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return
        const error = err instanceof ApiError ? err : new ApiError(0, 'Məlumatı yükləmək alınmadı.')
        setState({ status: 'error', data: undefined, error })
      })
    return () => controller.abort()
  }, [slug, enabled, attempt])

  /** Asks again in the background; resolves to the new answer, or null when the request failed. */
  const refresh = useCallback(async (): Promise<Voting | null> => {
    lastAsked.current = Date.now()
    try {
      const data = await votingApi.summary(slug)
      setState((current) => (current.status === 'error' ? current : { status: 'success', data, error: undefined }))
      return data
    } catch {
      return null
    }
  }, [slug])

  const data = state.data
  const boundary =
    data?.state === 'Open' ? data.endsAt : data?.state === 'Upcoming' ? data.startsAt : null
  const fromState = data?.state ?? null

  useEffect(() => {
    if (!enabled || !boundary || !fromState) return
    let cancelled = false
    let timer: ReturnType<typeof setTimeout> | undefined
    let backoff = 1_000

    const fire = async () => {
      if (cancelled) return
      // A boundary beyond one timer's reach: wait the next step, without asking anybody anything.
      const left = msUntil(boundary)
      if (left > 0) {
        timer = setTimeout(() => void fire(), left + 250)
        return
      }
      rerender()
      const next = await refresh()
      if (cancelled || (next && next.state !== fromState)) return
      timer = setTimeout(() => void fire(), backoff)
      backoff = Math.min(backoff * 2, MAX_BACKOFF_MS)
    }

    // A quarter of a second past the moment, so the first ask lands on the far side of it.
    timer = setTimeout(() => void fire(), msUntil(boundary) + 250)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [enabled, boundary, fromState, refresh])

  const inReview = data?.state === 'Closed' && data.resultsPublished !== true
  useEffect(() => {
    if (!enabled || !inReview) return
    const id = setInterval(() => {
      if (document.visibilityState === 'visible') void refresh()
    }, REVIEW_REFRESH_MS)
    return () => clearInterval(id)
  }, [enabled, inReview, refresh])

  useEffect(() => {
    if (!enabled) return
    const onVisibility = () => {
      if (document.visibilityState !== 'visible') return
      rerender()
      if (Date.now() - lastAsked.current >= VISIBLE_REFRESH_MIN_MS) void refresh()
    }
    document.addEventListener('visibilitychange', onVisibility)
    return () => document.removeEventListener('visibilitychange', onVisibility)
  }, [enabled, refresh])

  const retry = useCallback(() => setAttempt((n) => n + 1), [])
  return { ...state, retry, refresh }
}
