import { placeName } from '../../lib/text'
import type { AdminPlaceCandidate } from '../api'

/** "1-ci yer", "1-ci və 2-ci yer", "1-ci, 2-ci və 3-cü yer". */
export function placesName(places: number[]): string {
  const names = places.map((p) => placeName(p).replace(/ yer$/, ''))
  const joined = names.length > 1 ? `${names.slice(0, -1).join(', ')} və ${names[names.length - 1]}` : names[0]
  return `${joined} yer`
}

/**
 * The places the vote decides on its own, and what it leaves to the admin.
 *
 * A place is filled only when exactly one work holds its rank. Works level on votes are never put in an order
 * here — not by reference, not by date (D3): their places stay empty, and a note says which works are level and
 * for which places, so the admin orders them. Works without a vote are not placed at all.
 */
export function placesFromVotes(candidates: AdminPlaceCandidate[], count = 3): { places: (number | null)[]; notes: string[] } {
  const places: (number | null)[] = Array.from({ length: count }, () => null)
  const notes: string[] = []
  const groups: AdminPlaceCandidate[][] = []
  for (const c of candidates.filter((x) => x.voteCount > 0)) {
    const last = groups[groups.length - 1]
    if (last && last[0].voteRank === c.voteRank) last.push(c)
    else groups.push([c])
  }
  if (groups.length === 0) return { places, notes: ['Hələ heç bir işə səs verilməyib — yerləri özünüz seçin.'] }

  let position = 1
  for (const group of groups) {
    if (position > count) break
    if (group.length === 1) {
      places[position - 1] = group[0].submissionId
    } else {
      const open = Array.from({ length: Math.min(group.length, count - position + 1) }, (_, i) => position + i)
      const votes = `${group[0].voteCount} səs`
      notes.push(
        group.length <= open.length
          ? `${placesName(open)}: ${group.map((w) => w.reference).join(' və ')} bərabər səs toplayıb (${votes}) — sıranı siz təyin edin.`
          : `${placesName(open)}: ${group.length} iş bərabər səs toplayıb (${votes}) — ${open.length === 1 ? 'bu yeri hansının tutacağını' : 'bu yerləri hansılarının tutacağını'} siz seçin.`,
      )
    }
    position += group.length
  }
  return { places, notes }
}

/** Why the chosen places cannot be saved as they are, or null. The server says the same; this only says it sooner. */
export function placesProblem(places: (number | null)[]): string | null {
  for (let i = 1; i < places.length; i++)
    if (places[i] !== null && places[i - 1] === null) return `${placeName(i)} boş qalarkən ${placeName(i + 1)} seçilə bilməz.`
  const chosen = places.filter((p): p is number => p !== null)
  if (new Set(chosen).size !== chosen.length) return 'Bir iş yalnız bir yer tuta bilər.'
  return null
}
