import type { CompetitionSummary } from '../api/types'

export type CompetitionCardTone = 'open' | 'soon' | 'voting' | 'results' | 'closed'

/** Public status copied only from fields already supplied by the competition summary. */
export function competitionCardStatus(c: CompetitionSummary): { label: string; tone: CompetitionCardTone } {
  if (c.resultsPublished) return { label: 'Nəticələr', tone: 'results' }
  if (c.voting === 'Open') return { label: 'Səsvermə açıqdır', tone: 'voting' }
  if (c.voting === 'Upcoming') return { label: 'Səsvermə tezliklə', tone: 'soon' }
  if (c.voting === 'Closed') return { label: 'Səsvermə başa çatıb', tone: 'closed' }
  if (c.phase === 'Open') return { label: 'Qəbul açıqdır', tone: 'open' }
  if (c.phase === 'Upcoming') return { label: 'Tezliklə', tone: 'soon' }
  return { label: 'Qəbul bağlanıb', tone: 'closed' }
}
