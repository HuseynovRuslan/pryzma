import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError } from '../../api/client'
import { votingApi } from '../../api/endpoints'
import type { VotingLeaderboard, VotingLeaderboardEntry, VotingWork } from '../../api/types'
import { azOrdinal } from '../../lib/text'
import { workNumber } from './lib'

/** How often an open page asks for the ranking: often enough to feel live, rarely enough to cost little. */
export const POLL_MS = 10_000

/**
 * After the vote, the ranking is provisional and changes only when an admin sets votes aside: asking twice a minute
 * is enough for a decision to show without a reload.
 */
export const CLOSED_POLL_MS = 30_000

/** How long "+1 yeni səs" stays on a row after its count went up. */
export const BUMP_MS = 1_200

/** Rows the folded ranking shows. */
export const COMPACT = 3

/** "#36" — the short form of "İş #36", for a line that already says what it is about. */
export const shortNumber = (reference: string) => workNumber(reference).replace(/^İş\s+/u, '')

/** Places moved since the ranking last changed, by reference: 2 is two places up, -1 one place down. */
export type Movement = Record<string, number>

/**
 * How each work's rank changed between two successful reads. Only works present in both count: one that has just
 * appeared, and every work on the first read after a page load, has no movement at all — there is nothing to
 * compare it with, and a made-up arrow would be a claim the page cannot back.
 */
export function rankChanges(previous: VotingLeaderboard | null, next: VotingLeaderboard): Movement {
  if (!previous) return {}
  const before = new Map(previous.entries.map((e) => [e.reference, e.rank]))
  const moved: Movement = {}
  for (const e of next.entries) {
    const was = before.get(e.reference)
    if (was !== undefined && was !== e.rank) moved[e.reference] = was - e.rank
  }
  return moved
}

/** The votes each work gained between two reads; only works that gained any. */
export function countChanges(previous: VotingLeaderboard | null, next: VotingLeaderboard): Record<string, number> {
  if (!previous) return {}
  const before = new Map(previous.entries.map((e) => [e.reference, e.voteCount]))
  const gained: Record<string, number> = {}
  for (const e of next.entries) {
    const was = before.get(e.reference)
    if (was !== undefined && e.voteCount > was) gained[e.reference] = e.voteCount - was
  }
  return gained
}

/**
 * The line for the best climb of the latest reorder — "İş #110 · 4-cü → 3-cü · 1 pillə yüksəldi". Null when the
 * reorder only moved works down.
 */
export function climbLine(previous: VotingLeaderboard | null, next: VotingLeaderboard, moved: Movement): string | null {
  if (!previous) return null
  const best = next.entries.find((e) => (moved[e.reference] ?? 0) > 0)
  if (!best) return null
  const steps = moved[best.reference]
  return `${workNumber(best.reference)} · ${azOrdinal(best.rank + steps)} → ${azOrdinal(best.rank)} · ${steps} pillə yüksəldi`
}

/** A work's bar: its count as a share of the leader's. A comparison on the screen, not a chance of winning. */
export function barShare(votes: number, leaderVotes: number): number {
  if (leaderVotes <= 0 || votes <= 0) return 0
  return Math.min(1, votes / leaderVotes)
}

/**
 * The one line about the race, read from the counts as they stand — never a number below zero, never a claim
 * the counts do not make.
 *
 * About the viewer's own work when they have voted (it is the race they care about); otherwise about the closest
 * chase on the first screen. Ranks are shared by equal counts, so "2-ci yerə 3 səs qalıb" means exactly that: three
 * more votes and the work stands level with the second place.
 */
export function gapLine(entries: VotingLeaderboardEntry[], mine: string | null): string | null {
  if (entries.length === 0 || entries[0].voteCount === 0) return null
  const own = mine ? entries.find((e) => e.reference === mine) : undefined
  const focus = own ?? closestChase(entries.slice(0, COMPACT))
  if (!focus) return null

  const label = shortNumber(focus.reference)
  const level = entries.filter((e) => e.rank === focus.rank && e.reference !== focus.reference)
  if (focus.rank === 1) {
    if (level.length > 0) return `${label} · liderliyi ${together(level)} bölüşür`
    const next = entries.find((e) => e.voteCount < focus.voteCount)
    return next
      ? `${label} · liderdir, ${azOrdinal(next.rank)} yerdən ${focus.voteCount - next.voteCount} səs öndədir`
      : `${label} · liderdir`
  }

  const above = aboveOf(entries, focus)
  if (!above) return null
  const gap = above.voteCount - focus.voteCount
  const target = above.rank === 1 ? `Liderliyə ${gap} səs qalıb` : `${azOrdinal(above.rank)} yerə ${gap} səs qalıb`
  return level.length > 0 ? `${label} · ${together(level)} bərabərdir · ${target}` : `${label} · ${target}`
}

/** "#44 ilə", or "daha 2 işlə" when the tie is wider than one other work. */
const together = (others: VotingLeaderboardEntry[]) =>
  others.length === 1 ? `${shortNumber(others[0].reference)} ilə` : `daha ${others.length} işlə`

/** The nearest work with more votes — the one to catch. */
function aboveOf(entries: VotingLeaderboardEntry[], focus: VotingLeaderboardEntry) {
  const higher = entries.filter((e) => e.voteCount > focus.voteCount)
  return higher.length > 0 ? higher[higher.length - 1] : undefined
}

/** Among the shown works below the lead, the one closest to the place above it; the higher one when level. */
function closestChase(shown: VotingLeaderboardEntry[]) {
  let best: { entry: VotingLeaderboardEntry; gap: number } | undefined
  for (const e of shown) {
    if (e.rank === 1) continue
    const above = aboveOf(shown, e)
    if (!above) continue
    const gap = above.voteCount - e.voteCount
    if (!best || gap < best.gap) best = { entry: e, gap }
  }
  return best?.entry
}

/**
 * A card's count, kept in step with the ranking. Counts only grow, so the larger of the two is the newer: the
 * ranking's after a poll, the card's own just after this viewer voted and before the next poll.
 */
export function withLiveCount(work: VotingWork, counts: ReadonlyMap<string, number> | undefined): VotingWork {
  const live = counts?.get(work.reference)
  return live !== undefined && live > work.voteCount ? { ...work, voteCount: live } : work
}

export interface LiveRanking {
  /** The last ranking read successfully; null until the first. */
  board: VotingLeaderboard | null
  /** The latest reorder, kept until the next one. Empty after a page load until a reorder is actually seen. */
  movement: Movement
  /** The climb of the latest reorder, in words. */
  climb: string | null
  /** For screen readers: the top three, said again only when they change — never on a plain refresh. */
  said: string
  /** Votes gained in the latest read, shown briefly. */
  gained: Record<string, number>
  /** Counts by reference, for the cards under the ranking. */
  counts: ReadonlyMap<string, number>
  /** When the ranking was last read successfully (ms). */
  readAt: number | null
  /** Bumped on every successful read, so a row can tell a fresh change from an old one. */
  seq: number
  /** The latest read failed; the ranking on screen is the previous one. */
  stale: boolean
  /** Reads again now — after this viewer's own vote. */
  refresh: () => void
}

type State = Omit<LiveRanking, 'refresh'>

const START: State = {
  board: null,
  movement: {},
  climb: null,
  said: '',
  gained: {},
  counts: new Map(),
  readAt: null,
  seq: 0,
  stale: false,
}

const topOf = (board: VotingLeaderboard) =>
  board.entries
    .slice(0, COMPACT)
    .map((e) => `${e.rank}:${e.reference}`)
    .join(',')

/** "Reytinqdə ilk üçlük dəyişdi: 1-ci yer İş #36, 2-ci yer İş #44, 3-cü yer İş #110." */
export const topThreeLine = (board: VotingLeaderboard) =>
  `Reytinqdə ilk üçlük dəyişdi: ${board.entries
    .slice(0, COMPACT)
    .map((e) => `${azOrdinal(e.rank)} yer ${workNumber(e.reference)}`)
    .join(', ')}.`

function advance(current: State, next: VotingLeaderboard): State {
  const previous = current.board
  const live = next.state === 'Open'
  const moved = rankChanges(previous, next)
  const reordered = Object.keys(moved).length > 0
  return {
    board: next,
    // Once the window shuts the ranking is final: no arrows, no flashes, only the standings.
    movement: !live ? {} : reordered ? moved : current.movement,
    climb: !live ? null : reordered ? climbLine(previous, next, moved) : current.climb,
    said: live && previous && topOf(previous) !== topOf(next) ? topThreeLine(next) : current.said,
    gained: live ? countChanges(previous, next) : {},
    counts: new Map(next.entries.map((e) => [e.reference, e.voteCount])),
    readAt: Date.now(),
    seq: current.seq + 1,
    stale: false,
  }
}

/**
 * The live ranking, read now and then every POLL_MS while the vote is open: one request per cycle for the whole
 * list, never one per card, and never two at once — the next read is set only after the last one has answered.
 * A hidden tab stops asking and asks at once when it is shown again. Once the window has shut it asks every
 * CLOSED_POLL_MS, so a vote set aside in review leaves the provisional ranking without a reload; when the ranking does
 * not exist it stops for good. A failed read keeps the last ranking on screen and tries again next cycle.
 *
 * Movement is this page's own memory: the previous read is kept here and compared with the next. Nothing is stored
 * anywhere, so a reload starts with no arrows rather than invented ones.
 */
export function useLeaderboard(slug: string): LiveRanking {
  const [state, setState] = useState<State>(START)
  const reload = useRef<() => void>(() => undefined)

  useEffect(() => {
    let alive = true
    let done = false
    let timer: ReturnType<typeof setTimeout> | undefined
    let fade: ReturnType<typeof setTimeout> | undefined
    let inflight: AbortController | null = null
    // Asked for while a read was already on its way (a vote cast meanwhile): read once more when it answers.
    let again = false
    // Once the window has shut, the ranking is asked for less often — it moves only with an admin's decision.
    let closed = false

    const schedule = () => {
      clearTimeout(timer)
      timer = undefined
      if (!alive || done || document.visibilityState === 'hidden') return
      timer = setTimeout(read, closed ? CLOSED_POLL_MS : POLL_MS)
    }

    async function read() {
      if (!alive || done) return
      if (inflight) {
        again = true
        return
      }
      clearTimeout(timer)
      const controller = new AbortController()
      inflight = controller
      try {
        const next = await votingApi.leaderboard(slug, controller.signal)
        if (!alive) return
        closed = next.state !== 'Open'
        setState((current) => advance(current, next))
        clearTimeout(fade)
        fade = setTimeout(() => {
          if (alive) setState((current) => (Object.keys(current.gained).length ? { ...current, gained: {} } : current))
        }, BUMP_MS)
      } catch (err) {
        if (!alive) return
        // No ranking to show here at all (no vote, or not open yet): stop asking. Anything else is a bad moment.
        if (err instanceof ApiError && err.status === 404) done = true
        setState((current) => (current.stale ? current : { ...current, stale: true }))
      } finally {
        if (inflight === controller) inflight = null
      }
      if (again && alive && !done) {
        again = false
        return read()
      }
      schedule()
    }

    const onVisibility = () => {
      if (document.visibilityState === 'visible') void read()
    }

    reload.current = () => void read()
    document.addEventListener('visibilitychange', onVisibility)
    void read()
    return () => {
      alive = false
      clearTimeout(timer)
      clearTimeout(fade)
      inflight?.abort()
      reload.current = () => undefined
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [slug])

  const refresh = useCallback(() => reload.current(), [])
  // The same object until the next read, so a memoised ranking skips the renders of a page that ticks every second.
  return useMemo(() => ({ ...state, refresh }), [state, refresh])
}
