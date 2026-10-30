import { Link, useLocation } from 'react-router-dom'
import { useFeaturedCompetition } from '../../lib/featuredCompetitionContext'
import { shortCompetitionTitle } from '../../lib/format'
import './announce.css'

/**
 * "Səsvermə başladı": a strip under the header of every public page while a vote is open, with one link per
 * competition to its own ballot. It reads the list the page already fetched (FeaturedCompetitionProvider), so it
 * costs no request, appears when an admin starts the vote and goes when the window closes — the server's state,
 * never a date worked out here.
 */
export function VotingAnnouncement() {
  const { slides } = useFeaturedCompetition()
  const { pathname } = useLocation()
  const live = slides.filter((s) => s.lifecycle.stage === 'votingLive')
  if (live.length === 0) return null

  return (
    <aside className="vannounce" aria-label="Səsvermə elanı">
      <div className="container vannounce__inner">
        <p className="vannounce__title">
          <span className="vannounce__dot" aria-hidden="true" />
          Səsvermə başladı!
        </p>
        <ul className="vannounce__links" role="list">
          {live.map(({ competition: c }) => {
            const to = `/competitions/${c.slug}/voting`
            return (
              <li key={c.slug}>
                <Link to={to} className="vannounce__link" aria-current={pathname === to ? 'page' : undefined}>
                  {shortCompetitionTitle(c.title)} — səs ver
                </Link>
              </li>
            )
          })}
        </ul>
      </div>
    </aside>
  )
}
