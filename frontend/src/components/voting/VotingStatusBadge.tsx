import { Link } from 'react-router-dom'
import { useFeaturedCompetition } from '../../lib/featuredCompetitionContext'
import './badge.css'

/**
 * The live state of the vote, in the navbar.
 *
 * It reads the same lifecycle the hero does, so the two can never disagree about how long is left — the whole
 * reason that calculation lives in one place. It renders nothing at all when there is no vote to announce,
 * which is most days.
 */
export function VotingStatusBadge() {
  const { competition, lifecycle } = useFeaturedCompetition()
  if (!competition || !lifecycle?.badge) return null
  const { text, tone } = lifecycle.badge
  return (
    <Link
      to={`/competitions/${competition.slug}/voting`}
      className={`vbadge vbadge--${tone}`}
      title={`${competition.title} — səsvermə`}
    >
      {text}
    </Link>
  )
}
