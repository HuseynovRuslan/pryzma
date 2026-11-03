import type { FeaturedSlide } from '../featuredCompetitionContext'
import { campaigns } from '../../content/campaigns'

/**
 * When the final-hours music plays, and on which pages — worked out from the same voting window and the same
 * server clock as the countdown, so the music and the numbers on the screen cannot disagree about the time left.
 */

const SECOND = 1000
const MINUTE = 60 * SECOND
const HOUR = 60 * MINUTE

/** The music starts three hours before the vote closes. */
export const MUSIC_WINDOW_MS = 3 * HOUR
/** The last ten minutes: the page itself looks more urgent. */
export const LAST_TEN_MS = 10 * MINUTE
/** The last minute: the second, building track takes over and ends on zero. */
export const LAST_MINUTE_MS = 60 * SECOND

export type FinalPhase =
  /** More than three hours left, or no open vote: silence, and nothing on screen. */
  | 'idle'
  /** Inside the last three hours. */
  | 'hours'
  /** Inside the last ten minutes. */
  | 'lastTen'
  /** Inside the last sixty seconds. */
  | 'lastMinute'
  /** The window has run out. */
  | 'over'

export function finalPhaseOf(endsAt: number | null, now: number): FinalPhase {
  if (endsAt === null || Number.isNaN(endsAt)) return 'idle'
  const left = endsAt - now
  if (left <= 0) return 'over'
  if (left <= LAST_MINUTE_MS) return 'lastMinute'
  if (left <= LAST_TEN_MS) return 'lastTen'
  if (left <= MUSIC_WINDOW_MS) return 'hours'
  return 'idle'
}

/** True while the music may sound at all. */
export const isMusicPhase = (p: FinalPhase) => p === 'hours' || p === 'lastTen' || p === 'lastMinute'

export interface FinalTarget {
  slug: string
  /** The close of the vote, ms since the epoch (server time). */
  endsAt: number
}

/** The open votes, soonest close first. A window the server has not dated has no final hours. */
function liveWindows(slides: readonly FeaturedSlide[]): FinalTarget[] {
  const out: FinalTarget[] = []
  for (const s of slides) {
    if (s.voting?.state !== 'Open' || !s.voting.endsAt) continue
    const endsAt = Date.parse(s.voting.endsAt)
    if (!Number.isNaN(endsAt)) out.push({ slug: s.competition.slug, endsAt })
  }
  return out.sort((a, b) => a.endsAt - b.endsAt)
}

/**
 * The vote whose final the page belongs to, or null on a page that is not about an open vote.
 *
 * The competition pages — the front page, the list of competitions, an open competition's own page and its ballot —
 * carry the music; everything else (signing in, the account, the information pages, results, a competition whose
 * vote is not open) stays silent.
 */
export function finalTargetFor(slides: readonly FeaturedSlide[], pathname: string): FinalTarget | null {
  const live = liveWindows(slides)
  if (live.length === 0) return null
  const path = pathname.replace(/\/+$/, '') || '/'
  if (path === '/' || path === '/competitions') return live[0]

  const campaign = campaigns.find((c) => c.path === path)
  const slug = campaign
    ? campaign.competitionSlug
    : /^\/competitions\/([^/]+)(?:\/voting(?:\/[^/]+)?)?$/.exec(path)?.[1]
  if (!slug) return null
  return live.find((w) => w.slug === decodeURIComponent(slug)) ?? null
}

/** The soonest open vote, whatever the page — for what every page shows, such as the strip under the header. */
export function soonestFinal(slides: readonly FeaturedSlide[]): FinalTarget | null {
  return liveWindows(slides)[0] ?? null
}
