/**
 * The final show's running order (D13), as plain data: which screen comes after which, built only from the frozen,
 * locked numbers the server sends (FinalShowDto). Nothing here decides a place — the places come from the server —
 * it only decides the order of the reveals, so the same data always gives the same show, run after run.
 */
import type { FinalShow, FinalShowWork } from './api'

export type Slide =
  | { kind: 'opening' }
  | { kind: 'finalists' }
  | { kind: 'finalist'; work: FinalShowWork; index: number; count: number }
  | { kind: 'juryIntro' }
  | { kind: 'jury'; work: FinalShowWork; revealed: FinalShowWork[] }
  | { kind: 'publicIntro' }
  | { kind: 'public'; work: FinalShowWork; revealed: FinalShowWork[] }
  | { kind: 'combineIntro' }
  | { kind: 'place'; work: FinalShowWork; revealed: FinalShowWork[] }
  | { kind: 'blocked' }
  | { kind: 'top3Intro' }
  | { kind: 'placeCard'; place: number }
  | { kind: 'winnerPause' }
  | { kind: 'prize'; work: FinalShowWork; place: number }
  | { kind: 'table' }

const byReference = (a: FinalShowWork, b: FinalShowWork) => (a.reference < b.reference ? -1 : a.reference > b.reference ? 1 : 0)

/** Worst first, so the reveal climbs to the top: by the given rank from the bottom, reference order inside a level. */
const climbing = (rank: (w: FinalShowWork) => number) => (works: FinalShowWork[]) =>
  [...works].sort((a, b) => rank(b) - rank(a) || byReference(a, b))

/** The order the top three are revealed in: third, second, then — after a pause — the winner. */
export const TOP_THREE_ORDER = [3, 2, 1] as const

export function buildDeck(show: FinalShow): Slide[] {
  const works = [...show.works].sort(byReference)
  const deck: Slide[] = [{ kind: 'opening' }, { kind: 'finalists' }]

  // B — the ten, one by one, in reference order: nothing about any number yet.
  works.forEach((work, index) => deck.push({ kind: 'finalist', work, index, count: works.length }))

  // C — the jury's ranking, tenth to first.
  deck.push({ kind: 'juryIntro' })
  const jury = climbing((w) => w.juryRank)(works)
  jury.forEach((work, i) => deck.push({ kind: 'jury', work, revealed: jury.slice(0, i + 1) }))

  // D — the public's ranking, tenth to first.
  deck.push({ kind: 'publicIntro' })
  const pub = climbing((w) => w.publicRank)(works)
  pub.forEach((work, i) => deck.push({ kind: 'public', work, revealed: pub.slice(0, i + 1) }))

  // E — the two combined, places ten to four; the top three stay closed.
  deck.push({ kind: 'combineIntro' })
  const below = climbing((w) => w.place ?? w.rank)(works.filter((w) => w.place !== null && w.place > 3))
  below.forEach((work, i) => deck.push({ kind: 'place', work, revealed: below.slice(0, i + 1) }))

  // A tie at a prize the jury has not decided: the show stops here, as the publish does.
  if (!show.prizesDecided) {
    deck.push({ kind: 'blocked' })
    return deck
  }

  // F — the top three, each announced and then shown with how it was reached; the winner after a pause.
  deck.push({ kind: 'top3Intro' })
  for (const place of TOP_THREE_ORDER) {
    const work = works.find((w) => w.place === place)
    if (!work) continue
    deck.push(place === 1 ? { kind: 'winnerPause' } : { kind: 'placeCard', place })
    deck.push({ kind: 'prize', work, place })
  }
  deck.push({ kind: 'table' })
  return deck
}

/** The final table: by place (level works together), best first. */
export const finalOrder = (works: FinalShowWork[]) =>
  [...works].sort((a, b) => (a.place ?? a.rank) - (b.place ?? b.rank) || byReference(a, b))

/** A jury total as spoken: 52,5. */
export const points = (n: number) => String(n).replace('.', ',')

/** A juror's mark as typed: empty, or 0–10 in steps of 0.5 (a comma is a decimal point, as written in Azerbaijani). */
export function parseMark(text: string): number | null | 'invalid' {
  const t = text.trim().replace(',', '.')
  if (t === '') return null
  const n = Number(t)
  return Number.isFinite(n) && n >= 0 && n <= 10 && Number.isInteger(n * 2) ? n : 'invalid'
}
