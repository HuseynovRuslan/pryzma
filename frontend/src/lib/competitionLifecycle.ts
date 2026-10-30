import { useEffect, useMemo, useState } from 'react'
import type { CompetitionDetail, CompetitionSummary, Voting } from '../api/types'
import { serverNow } from './serverClock'

/**
 * Where a competition stands, and how long is left of it — worked out once, in one place.
 *
 * The navbar, the hero and anything else that counts down all read this, so they cannot disagree: a header
 * saying "2 gün qalıb" beside a hero saying "01 gün 23 saat" is the kind of contradiction that comes from two
 * places rounding differently, and the only cure is for there to be one place.
 *
 * Nothing here is guessed from the design: every date comes from the API, and a stage with no date simply has
 * no countdown rather than an invented one.
 */

export type LifecycleStage =
  /** Entries are being taken. */
  | 'submissions'
  /** Entries have closed; the works are being checked. */
  | 'review'
  /** A voting window exists and has not opened yet. */
  | 'votingUpcoming'
  /** Voting is open. */
  | 'votingLive'
  /** Voting has closed. */
  | 'votingClosed'
  /** The result is public. */
  | 'results'

/** The four steps the design draws, in order. The stage maps onto one of them. */
export const LIFECYCLE_STEPS = ['Qəbul', 'Yoxlama', 'Səsvermə', 'Nəticələr'] as const

export type StepState = 'done' | 'current' | 'upcoming'

export interface LifecycleStep {
  label: string
  /** A shorter label for a phone's narrow rail, where the full one would run into its neighbour. */
  short?: string
  state: StepState
  /** "Tamamlandı" / "Cari mərhələ" / "Növbəti" — the line under the circle. */
  note: string
}

export interface Remaining {
  days: number
  hours: number
  minutes: number
  seconds: number
  /** Milliseconds left; 0 once the moment has passed. */
  ms: number
}

export interface Lifecycle {
  stage: LifecycleStage
  /** 0-based index into LIFECYCLE_STEPS. */
  step: number
  steps: LifecycleStep[]
  /** What the countdown is counting towards, when there is one. */
  countdown: { at: string; label: string; remaining: Remaining } | null
  /** True only inside the last 24 hours of the current countdown — the "Son 24 saat" badge. */
  isFinalDay: boolean
  /** One short line for the navbar badge; null when there is nothing worth saying. */
  badge: { text: string; tone: 'live' | 'soon' | 'urgent' } | null
}

const SECOND = 1000
const MINUTE = 60 * SECOND
const HOUR = 60 * MINUTE
const DAY = 24 * HOUR

/**
 * The clock, shared by everything on the page so two counters cannot drift apart — the server's clock as last
 * reported (lib/serverClock), so a device set to the wrong time counts down to the server's moment, not its own.
 */
export function useNow(intervalMs = SECOND): number {
  const [now, setNow] = useState(() => serverNow())
  useEffect(() => {
    const id = setInterval(() => setNow(serverNow()), intervalMs)
    return () => clearInterval(id)
  }, [intervalMs])
  return now
}

/**
 * Time left until an instant. Always floors, and never goes below zero — a countdown that has run out reads
 * 00/00/00 rather than counting upwards into negatives.
 */
export function remainingUntil(iso: string | null | undefined, now: number): Remaining | null {
  if (!iso) return null
  const at = new Date(iso).getTime()
  if (Number.isNaN(at)) return null
  const ms = Math.max(0, at - now)
  return {
    days: Math.floor(ms / DAY),
    hours: Math.floor((ms % DAY) / HOUR),
    minutes: Math.floor((ms % HOUR) / MINUTE),
    seconds: Math.floor((ms % MINUTE) / SECOND),
    ms,
  }
}

/** "2 gün qalıb" / "5 saat qalıb" / "12 dəqiqə qalıb" — the coarse form, for the navbar. */
export function shortRemaining(r: Remaining): string {
  if (r.ms <= 0) return 'bitdi'
  if (r.days > 0) return `${r.days} gün qalıb`
  if (r.hours > 0) return `${r.hours} saat qalıb`
  if (r.minutes > 0) return `${r.minutes} dəqiqə qalıb`
  return 'bir neçə saniyə qalıb'
}

type CompetitionLike = Pick<CompetitionSummary, 'endDate' | 'voting'> &
  Partial<Pick<CompetitionSummary, 'resultsPublished'>> &
  Partial<Pick<CompetitionDetail, 'isAcceptingSubmissions' | 'resultsPublishedAt' | 'startDate'>>

/**
 * The stage, from the competition and — where one exists — its voting window.
 *
 * `voting` is the answer from /voting: it carries the window itself, which no other endpoint does. When a
 * competition has no voting window at all that call 404s and `voting` is null, which is not a failure but a
 * fact: this competition goes from review straight to its result.
 */
function stageOf(competition: CompetitionLike, voting: Voting | null, now: number): LifecycleStage {
  // The detail page knows when; the list only knows whether. Either is enough to say it is over.
  if (competition.resultsPublishedAt || competition.resultsPublished || voting?.resultsPublished) return 'results'
  // An open window whose end, by the server's clock, has passed is over even before the server has been asked
  // again: an answer that is late or lost must not keep a finished vote looking open.
  if (voting?.state === 'Open' && voting.endsAt && Date.parse(voting.endsAt) <= now) return 'votingClosed'
  if (voting?.state === 'Open') return 'votingLive'
  if (voting?.state === 'Upcoming') return 'votingUpcoming'
  if (voting?.state === 'Closed') return 'votingClosed'
  // No window, or one not yet set: entries decide.
  const accepting =
    competition.isAcceptingSubmissions ??
    (competition.endDate ? new Date(competition.endDate).getTime() > now : true)
  return accepting ? 'submissions' : 'review'
}

const STEP_OF: Record<LifecycleStage, number> = {
  submissions: 0,
  review: 1,
  votingUpcoming: 1,
  votingLive: 2,
  votingClosed: 2,
  results: 3,
}

/**
 * After the vote and before the result, the rail shows the road to the result — the same steps the review stage
 * draws (components/voting/VotingReview): the vote is behind, the review is where the competition stands, the
 * result comes next. The check of the entries before the vote is long done and would only read as a second,
 * finished "Yoxlama" beside the one under way.
 */
const REVIEW_STEPS: readonly LifecycleStep[] = [
  { label: 'Qəbul', state: 'done', note: 'Tamamlandı' },
  { label: 'Səsvermə', state: 'done', note: 'Tamamlandı' },
  { label: 'Səslərin yoxlanması', short: 'Yoxlama', state: 'current', note: 'Hazırda bu mərhələdəyik' },
  { label: 'Yekun nəticələr', short: 'Nəticələr', state: 'upcoming', note: 'Elan olunacaq' },
]

function stepsFor(step: number, stage: LifecycleStage): LifecycleStep[] {
  if (stage === 'votingClosed') return REVIEW_STEPS.map((s) => ({ ...s }))
  return LIFECYCLE_STEPS.map((label, i) => {
    const state: StepState = i < step ? 'done' : i === step ? 'current' : 'upcoming'
    return {
      label,
      state,
      note: state === 'done' ? 'Tamamlandı' : state === 'current' ? 'Cari mərhələ' : 'Növbəti',
    }
  })
}

/** What the stage is counting towards, if anything. */
function countdownFor(
  stage: LifecycleStage,
  competition: CompetitionLike,
  voting: Voting | null,
  now: number,
): Lifecycle['countdown'] {
  const at =
    stage === 'submissions'
      ? competition.endDate
      : stage === 'votingLive'
        ? (voting?.endsAt ?? null)
        : stage === 'votingUpcoming' || stage === 'review'
          ? (voting?.startsAt ?? null)
          : null
  if (!at) return null
  const remaining = remainingUntil(at, now)
  if (!remaining) return null
  const label =
    stage === 'submissions'
      ? 'QƏBULUN BİTMƏSİNƏ'
      : stage === 'votingLive'
        ? 'SƏSVERMƏNİN BİTMƏSİNƏ QALAN VAXT'
        : 'SƏSVERMƏNİN BAŞLAMASINA'
  return { at, label, remaining }
}

function badgeFor(stage: LifecycleStage, countdown: Lifecycle['countdown'], finalDay: boolean): Lifecycle['badge'] {
  if (stage === 'votingLive') {
    if (finalDay) return { text: '● Son 24 saat', tone: 'urgent' }
    return { text: `● Səsvermə • ${countdown ? shortRemaining(countdown.remaining) : 'davam edir'}`, tone: 'live' }
  }
  if (stage === 'votingUpcoming') {
    const r = countdown?.remaining
    return { text: r && r.days === 0 ? 'Səsvermə bu gün' : r && r.days === 1 ? 'Səsvermə sabah' : 'Səsvermə tezliklə', tone: 'soon' }
  }
  return null
}

/**
 * The whole picture for one competition. Pass the voting summary when the competition has one; pass null when
 * /voting answered 404, which simply means there is no public vote here.
 */
export function lifecycleOf(competition: CompetitionLike, voting: Voting | null, now: number): Lifecycle {
  const stage = stageOf(competition, voting, now)
  const step = STEP_OF[stage]
  const countdown = countdownFor(stage, competition, voting, now)
  // The badge belongs to the voting window only: an entry deadline inside 24 hours is its own, older story.
  const isFinalDay = stage === 'votingLive' && countdown !== null && countdown.remaining.ms > 0 && countdown.remaining.ms <= DAY
  return { stage, step, steps: stepsFor(step, stage), countdown, isFinalDay, badge: badgeFor(stage, countdown, isFinalDay) }
}

/** The hook form: one ticking clock, one lifecycle, recomputed each second. */
export function useCompetitionLifecycle(competition: CompetitionLike | null, voting: Voting | null): Lifecycle | null {
  const now = useNow()
  return useMemo(() => (competition ? lifecycleOf(competition, voting, now) : null), [competition, voting, now])
}
