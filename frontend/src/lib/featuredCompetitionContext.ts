import { createContext, useContext } from 'react'
import type { CompetitionSummary, Voting } from '../api/types'
import type { Lifecycle } from './competitionLifecycle'

/** One competition as the front of the page shows it: itself, its window, and where it stands today. */
export interface FeaturedSlide {
  competition: CompetitionSummary
  voting: Voting | null
  lifecycle: Lifecycle
}

/**
 * The competitions the site leads with, shared between the navbar badge and the page below it.
 *
 * `competition` / `voting` / `lifecycle` are the first of `slides` — the one the badge speaks for. The rest
 * are what the home page offers beside it, so a reader can look at the others without leaving the page.
 *
 * The context and its hook live apart from the provider so that the provider's file exports a component and
 * nothing else — which is what keeps fast refresh working during development.
 */
export interface Featured {
  competition: CompetitionSummary | null
  voting: Voting | null
  lifecycle: Lifecycle | null
  /** Every competition worth leading with, the first one first. Empty until the call settles. */
  slides: FeaturedSlide[]
  /** False until the calls have settled, so nothing flashes a stage it is about to change. */
  ready: boolean
  /**
   * The slugs of the competitions whose results are public, in the API's order — read off the same list, so the
   * home page knows whether there are winners to show without asking anything more on an ordinary day.
   */
  published: readonly string[]
}

export const FeaturedContext = createContext<Featured>({
  competition: null,
  voting: null,
  lifecycle: null,
  slides: [],
  ready: false,
  published: [],
})

export const useFeaturedCompetition = () => useContext(FeaturedContext)

/**
 * How many competitions the front of the page will carry. A reader who has to click past six of them is
 * reading a list, not a lead; the rest are a click away under "Bütün müsabiqələr".
 */
export const MAX_FEATURED = 4

/**
 * The competitions worth leading with, most urgent first: voting before an open call, because a vote is the
 * thing with a deadline the reader can act on today.
 *
 * Then the two waiting stages. Entries have closed and no voting window is set yet — the works are being
 * checked — and then a vote that has closed while the result is still to come. Both are the competition the site
 * is in the middle of; leaving them out made the front page look as if nothing were happening the morning after
 * a deadline, which is exactly when people come to look.
 *
 * A competition whose result is out has finished and is not led with, in any band. Nor is one that has not
 * opened yet.
 *
 * Order within a band is the order the API gave, which is the admin's own.
 */
export function pickFeaturedList(list: CompetitionSummary[]): CompetitionSummary[] {
  const bands: ((c: CompetitionSummary) => boolean)[] = [
    (c) => c.voting === 'Open',
    (c) => c.voting === 'Upcoming',
    (c) => c.phase === 'Open',
    (c) => c.phase === 'Closed' && c.voting === 'NotConfigured',
    (c) => c.voting === 'Closed',
  ]
  const live = list.filter((c) => !c.resultsPublished)
  const chosen: CompetitionSummary[] = []
  for (const inBand of bands)
    for (const c of live)
      if (inBand(c) && !chosen.includes(c)) chosen.push(c)
  return chosen.slice(0, MAX_FEATURED)
}

/** The one the navbar badge speaks for: the first of the same ordering, so the two cannot disagree. */
export function pickFeatured(list: CompetitionSummary[]): CompetitionSummary | null {
  return pickFeaturedList(list)[0] ?? null
}
