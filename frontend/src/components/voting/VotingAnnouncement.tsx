import { Link, useLocation } from 'react-router-dom'
import { useFeaturedCompetition } from '../../lib/featuredCompetitionContext'
import { LAST_TEN_MS } from '../../lib/finalMusic/schedule'
import { shortCompetitionTitle } from '../../lib/format'
import './announce.css'

/** "09:41": minutes and seconds left, for the strip's last ten minutes. */
function clock(ms: number): string {
  const s = Math.floor(ms / 1000)
  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`
}

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
  // The last ten minutes of the soonest close: the strip turns to the final's dark and counts the minutes down.
  const left = Math.min(...live.map((s) => s.lifecycle.countdown?.remaining.ms ?? Infinity))
  const final = left > 0 && left <= LAST_TEN_MS

  return (
    <aside className={`vannounce${final ? ' vannounce--final' : ''}`} aria-label="Səsvermə elanı">
      <div className="container vannounce__inner">
        <p className="vannounce__title">
          <span className="vannounce__dot" aria-hidden="true" />
          {final ? (
            <>
              Son dəqiqələr
              <span className="vannounce__clock tabular" role="timer" aria-live="off">
                {clock(left)}
              </span>
            </>
          ) : (
            'Səsvermə başladı!'
          )}
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
