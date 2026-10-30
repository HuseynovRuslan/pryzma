import { useEffect, useState } from 'react'
import { votingApi } from '../../api/endpoints'
import type { Voting } from '../../api/types'
import { useNow } from '../../lib/competitionLifecycle'
import { lastVoteAgo, PULSE_FROM } from '../../pages/voting/lib'

/** How often the line asks again while it is on screen: often enough to move, rarely enough to cost nothing. */
const REFRESH_MS = 60_000

/**
 * That the vote is alive, in numbers that name nobody: how many people have voted, and how recently the last vote
 * came in. Who voted, and for what, is never on the page.
 *
 * Quiet until PULSE_FROM people have voted — a small number reads as an empty room — and gone once the window
 * shuts. While the page is visible it asks the server again every minute, so the line moves while someone reads.
 */
export function VotePulse({ voting, slug, className = '' }: { voting: Voting; slug: string; className?: string }) {
  const [live, setLive] = useState(voting)
  const now = useNow(30_000)

  useEffect(() => setLive(voting), [voting])

  useEffect(() => {
    if (voting.state !== 'Open') return
    const id = setInterval(() => {
      if (document.visibilityState !== 'visible') return
      votingApi
        .summary(slug)
        .then(setLive)
        .catch(() => undefined)
    }, REFRESH_MS)
    return () => clearInterval(id)
  }, [slug, voting.state])

  if (live.state !== 'Open' || live.voterCount < PULSE_FROM) return null
  const ago = lastVoteAgo(live.lastVoteAt, now)
  return (
    <p className={`vpulse ${className}`.trim()}>
      <span className={`vpulse__dot${ago ? ' is-fresh' : ''}`} aria-hidden="true" />
      <span className="vpulse__text">
        <strong className="vpulse__n tabular">{live.voterCount}</strong> nəfər artıq səs verib.
        {ago && <span className="vpulse__ago"> Sonuncu səs {ago} verildi.</span>}
      </span>
    </p>
  )
}
