import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { catalog, votingApi } from '../api/endpoints'
import type { CompetitionSummary, Voting } from '../api/types'
import { lifecycleOf, useNow } from './competitionLifecycle'
import { FeaturedContext, pickFeaturedList, type Featured, type FeaturedSlide } from './featuredCompetitionContext'

/**
 * Fetches the competitions the site leads with, once per page load.
 *
 * A competition's voting window lives only behind /voting, so that call is made only for the competitions that
 * say they have one — on an ordinary day there is none, and this costs a single list request. Every call is
 * allowed to fail: the badge and the stage hero are extras, and without them the site is simply the site.
 */
export function FeaturedCompetitionProvider({ children }: { children: ReactNode }) {
  const [list, setList] = useState<CompetitionSummary[]>([])
  const [windows, setWindows] = useState<Record<string, Voting>>({})
  const [ready, setReady] = useState(false)
  const [published, setPublished] = useState<readonly string[]>([])
  const now = useNow()

  useEffect(() => {
    const controller = new AbortController()
    let live = true
    void (async () => {
      try {
        const all = await catalog.competitions(controller.signal)
        const chosen = pickFeaturedList(all)
        if (!live) return
        setList(chosen)
        setPublished(all.filter((c) => c.resultsPublished).map((c) => c.slug))
        const found = await Promise.all(
          chosen
            .filter((c) => c.voting !== 'NotConfigured')
            .map(async (c) => [c.slug, await votingApi.summary(c.slug, controller.signal).catch(() => null)] as const),
        )
        if (!live) return
        setWindows(Object.fromEntries(found.filter((e): e is [string, Voting] => e[1] !== null)))
      } catch {
        /* extras only — see above */
      } finally {
        if (live) setReady(true)
      }
    })()
    return () => {
      live = false
      controller.abort()
    }
  }, [])

  const slides = useMemo<FeaturedSlide[]>(
    () =>
      list.map((competition) => {
        const window = windows[competition.slug] ?? null
        return { competition, voting: window, lifecycle: lifecycleOf(competition, window, now) }
      }),
    [list, windows, now],
  )

  // The first is the one the navbar badge speaks for, and the one the boundary refresh below watches.
  const competition = slides[0]?.competition ?? null
  const voting = slides[0]?.voting ?? null
  const lifecycle = slides[0]?.lifecycle ?? null
  const setVoting = (next: Voting) => setWindows((w) => ({ ...w, [next.competition.slug]: next }))

  /*
   * Voting state remains the server's decision. When its current window reaches a boundary, ask the existing
   * read-only summary endpoint for the new state instead of guessing Open/Closed from dates in the browser.
   * A short, bounded-backoff retry covers the few milliseconds where a request can land on the other side of
   * the UI clock but before the server reports the transition. This runs only at a boundary, never every tick.
   */
  // The boundary is the window's own date, on the server's clock — not the countdown, which the stage drops the
  // moment an open window's end has passed (see lifecycleOf).
  const votingBoundaryAt =
    voting?.state === 'Open' ? voting.endsAt : voting?.state === 'Upcoming' ? voting.startsAt : null
  const votingBoundaryExpired = votingBoundaryAt !== null && Date.parse(votingBoundaryAt) <= now
  const competitionSlug = competition?.slug ?? null
  const votingState = voting?.state ?? null

  useEffect(() => {
    if (!votingBoundaryExpired || !competitionSlug || !votingState) return

    const controller = new AbortController()
    let retry: ReturnType<typeof setTimeout> | undefined
    let retryMs = 1_000

    const refresh = async () => {
      try {
        const next = await votingApi.summary(competitionSlug, controller.signal)
        if (controller.signal.aborted) return
        if (next.state === votingState) {
          retry = setTimeout(() => void refresh(), retryMs)
          retryMs = Math.min(retryMs * 2, 30_000)
          return
        }
        setVoting(next)
      } catch {
        if (!controller.signal.aborted) {
          retry = setTimeout(() => void refresh(), retryMs)
          retryMs = Math.min(retryMs * 2, 30_000)
        }
      }
    }

    void refresh()
    return () => {
      controller.abort()
      if (retry !== undefined) clearTimeout(retry)
    }
  }, [competitionSlug, votingBoundaryAt, votingBoundaryExpired, votingState])

  const value = useMemo<Featured>(
    () => ({
      competition,
      voting,
      lifecycle,
      slides,
      ready,
      published,
    }),
    [competition, voting, lifecycle, slides, ready, published],
  )
  return <FeaturedContext.Provider value={value}>{children}</FeaturedContext.Provider>
}
