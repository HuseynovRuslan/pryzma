import type {
  VoteEligibility,
  Voting,
  VotingAuditEntry,
  VotingState,
  VotingWork,
  VotingWorks,
  VoteReceipt,
  VotingViewer,
} from '../../api/types'
import { shortCompetitionTitle } from '../../lib/format'
import { serverNow } from '../../lib/serverClock'
import { azLocative } from '../../lib/text'

/** What to call a work when its title field is not public: its reference number, never a person. */
export const workName = (w: Pick<VotingWork, 'title' | 'reference'>) => w.title ?? `İş ${w.reference}`

/**
 * What a work's button says, and the line under it. One entry per reason the server can give, so the browser
 * never works out from a date or a count whether somebody may vote — it only renders the answer it was given.
 *
 * `tone` decides how the button looks and whether it does anything: `action` presses, `done` is the receipt for a
 * vote already cast, `off` explains why there is nothing to press.
 */
export const voteCopy: Record<VoteEligibility, { label: string; note: string | null; tone: 'action' | 'done' | 'off' }> = {
  Allowed: { label: 'Səs ver', note: null, tone: 'action' },
  // The button carries the whole message; a note under it would only say the same thing again on every card.
  NotSignedIn: { label: 'Səs vermək üçün daxil olun', note: null, tone: 'action' },
  VotingNotOpen: { label: 'Səsvermə başa çatıb', note: 'Bu müsabiqədə səsvermə yekunlaşıb.', tone: 'off' },
  AlreadyVotedForThisWork: { label: 'Səsiniz qeydə alındı ✓', note: 'Dəstəyiniz üçün təşəkkür edirik!', tone: 'done' },
  // The server's name for it is older than the rule: it means "voted for another work of this competition".
  AlreadyVotedInNomination: {
    label: 'Səsinizi artıq vermisiniz',
    note: 'Müsabiqədə yalnız bir işə səs verə bilərsiniz.',
    tone: 'off',
  },
  OwnWork: { label: 'Bu sizin işinizdir', note: 'Öz işinizə səs verə bilməzsiniz.', tone: 'off' },
  EmailNotConfirmed: {
    label: 'E-maili təsdiqləyin',
    note: 'Səs vermək üçün e-mailinizə göndərilən linki açın.',
    tone: 'off',
  },
}

/**
 * Whether the running total is public while voting is open. The organiser decided on 4 October 2026 that it is:
 * each work's count is shown live, on the ballot, on the home page and on the work's own page. The author stays
 * private either way (D2) — a count belongs to a work's code, never to a name. The API has always carried the
 * number; this is the one place the page decides whether to print it, so changing it back is one line.
 */
export const LIVE_COUNTS = true

/** Whether a work's count is on the page in this state of the window. Once the window shuts, it always is. */
export const countsVisible = (state: VotingState) => LIVE_COUNTS || state !== 'Open'

/**
 * A competition whose works are all in one list. A person votes once per competition whatever the categories, so
 * this only decides whether there is anything to filter or label: with one category there is not.
 */
export const isSinglePool = (voting: Pick<Voting, 'nominations'> | null | undefined) =>
  (voting?.nominations.length ?? 0) <= 1

/** The one rule, said the same way everywhere it is said. */
export const ONE_VOTE = 'Müsabiqədə yalnız bir işə səs verə bilərsiniz.'

/**
 * What comes after the vote, for a page that shows it: `review` from the end of the window until an admin publishes
 * the official result, then `results`. Null while the vote is upcoming or open.
 *
 * The window's end is the server's (`endsAt`), compared on the server's clock (lib/serverClock): once it has passed,
 * the vote is treated as over even if the answer saying so has not arrived yet. That only ever closes, never opens —
 * and it never claims a result: `results` needs the server's own word that the result is public.
 */
export function afterVote(
  voting: Pick<Voting, 'state' | 'endsAt' | 'resultsPublished'>,
  now: number = serverNow(),
): 'review' | 'results' | null {
  const ended =
    voting.state === 'Closed' || (voting.state === 'Open' && voting.endsAt !== null && Date.parse(voting.endsAt) <= now)
  if (!ended) return null
  return voting.resultsPublished === true && voting.state === 'Closed' ? 'results' : 'review'
}

/** A work's button and note. */
export const voteCopyFor = (eligibility: VoteEligibility) => voteCopy[eligibility]

/** The line under the button: why it says what it says. */
export const voteNote = (eligibility: VoteEligibility): string | null => voteCopyFor(eligibility).note

/**
 * The work's number for a card: "İş #36" from SUB-2026-00036. The reference stays the work's real identifier —
 * this is only its shorter spoken form, read from the reference rather than invented.
 */
export function workNumber(reference: string): string {
  const digits = reference.match(/(\d+)$/)?.[1]
  return digits ? `İş #${Number(digits)}` : reference
}

/**
 * The work's number as the jury's catalog and printed documents show it: "İş №90" from SUB-2026-00090 — the long
 * reference never goes on paper.
 */
export const catalogNumber = (reference: string) => workNumber(reference).replace('İş #', 'İş №')

export const votesLabel = (n: number) => `${n} səs`

/**
 * From how many voters the page says how many have voted. Below it, "4 nəfər səs verib" reads as an empty room
 * rather than a live one, so the page says nothing and lets the countdown speak.
 */
export const PULSE_FROM = 20

/** "indicə", "4 dəqiqə əvvəl" — only within the last hour; an older last vote is not news worth printing. */
export function lastVoteAgo(lastVoteAt: string | null, now: number): string | null {
  if (!lastVoteAt) return null
  const minutes = Math.floor((now - Date.parse(lastVoteAt)) / 60_000)
  if (minutes < 0 || minutes >= 60) return null
  return minutes < 1 ? 'indicə' : `${minutes} dəqiqə əvvəl`
}

/**
 * What a voter sends a friend: that they voted, and where to vote — never for which work. The ballot stays
 * secret even in the voter's own message, so sharing costs nothing to share.
 */
export function shareMessage(platformName: string, competitionTitle: string): string {
  const at = azLocative(platformName.toLocaleLowerCase('az')).slice(-2)
  // A title written without quotes ends in "müsabiqəsi" itself; the sentence already says it once.
  const name = shortCompetitionTitle(competitionTitle).replace(/\s+müsabiqəsi$/u, '')
  return `${platformName}-${at} «${name}» müsabiqəsində səs verdim. Sən də bəyəndiyin işə səs ver.`
}

const SEED_KEY = 'pryzma.ballot-seed'
let memorySeed: number | undefined

/**
 * This visitor's shuffle of the ballot. The server orders the works by it, so each visitor has their own order —
 * no work owns the first screen, and the order is not the running count (which would only feed the leader). It is
 * kept in the browser so the order holds across pages, reloads and the trip to a work and back; where storage is
 * unavailable it lasts for this page's life, which still keeps paging consistent.
 */
export function ballotSeed(): number {
  if (memorySeed !== undefined) return memorySeed
  try {
    const kept = Number(window.localStorage.getItem(SEED_KEY))
    if (Number.isInteger(kept) && kept > 0) return (memorySeed = kept)
  } catch {
    // storage blocked: fall through to a seed for this page only
  }
  memorySeed = 1 + Math.floor(Math.random() * 2_147_483_646)
  try {
    window.localStorage.setItem(SEED_KEY, String(memorySeed))
  } catch {
    // keep it in memory only
  }
  return memorySeed
}

/**
 * The page after a vote, worked out from the server's own receipt rather than guessed: the work it names takes
 * the count the server returned, and every other work — in any category, since a person votes once per
 * competition — becomes unpressable. A reload, a filter or another work's page asks the server and is told the
 * same thing; this is only the page agreeing with it in the meantime.
 */
export function applyVote<T extends { items: VotingWork[]; viewer: VotingViewer | null }>(page: T, receipt: VoteReceipt): T {
  return {
    ...page,
    items: page.items.map((w) => votedWork(w, receipt)),
    viewer: votedViewer(page.viewer, receipt),
  }
}

export function votedWork(w: VotingWork, receipt: VoteReceipt): VotingWork {
  if (w.reference === receipt.reference)
    return { ...w, voteCount: receipt.voteCount, eligibility: 'AlreadyVotedForThisWork' }
  if (w.eligibility === 'Allowed') return { ...w, eligibility: 'AlreadyVotedInNomination' }
  return w
}

export function votedViewer(viewer: VotingViewer | null, receipt: VoteReceipt): VotingViewer | null {
  if (!viewer) return viewer
  return {
    ...viewer,
    mayVote: false,
    reason: 'AlreadyVotedInNomination',
    votedNominations: viewer.votedNominations.includes(receipt.nominationSlug)
      ? viewer.votedNominations
      : [...viewer.votedNominations, receipt.nominationSlug],
    votedWorks: viewer.votedWorks.includes(receipt.reference)
      ? viewer.votedWorks
      : [...viewer.votedWorks, receipt.reference],
  }
}

/** A page of works with one more page appended; used by "Daha çox göstər". */
export const appendPage = (page: VotingWorks, more: VotingWork[]): VotingWorks => ({
  ...page,
  items: [...page.items, ...more],
})

/**
 * One public audit line, in the owner's words: how many votes, set aside under which rule. Built from the rule the
 * server names, never from anybody's own text — so nothing private can reach it.
 */
export function auditLine(entry: Pick<VotingAuditEntry, 'votes' | 'reason'>): string {
  const why =
    entry.reason === 'DuplicatePhone'
      ? 'təkrarlanan telefon məlumatlarına görə etibarsız sayıldı'
      : entry.reason === 'DisposableEmail'
        ? 'müvəqqəti e-poçt qaydasına əsasən etibarsız sayıldı'
        : 'koordinasiyalı hesab klasteri qaydasına əsasən etibarsız sayıldı'
  return `${entry.votes} səs ${why}.`
}

/** Each rule a vote may be set aside under, in the owner's public words: its name and what it means, briefly. */
export const auditRules: Record<VotingAuditEntry['reason'], { name: string; meaning: string }> = {
  DuplicatePhone: {
    name: 'Telefon təkrarı',
    meaning: 'Eyni telefon nömrəsi ilə bir neçə səsverən hesab açılıbsa, həmin hesabların səsləri.',
  },
  CoordinatedAccounts: {
    name: 'R2 qaydası',
    meaning: 'Səsvermə müddətində koordinasiyalı şəkildə açılmış hesab qruplarının səsləri.',
  },
  DisposableEmail: {
    name: 'Müvəqqəti e-poçt',
    meaning: 'Səsvermə müddətində yaradılmış və müvəqqəti e-poçt xidmətindən istifadə etmiş hesabların səsləri.',
  },
}
