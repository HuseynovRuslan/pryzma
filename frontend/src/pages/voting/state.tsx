import { Link } from 'react-router-dom'
import type { Voting } from '../../api/types'
import { formatDate } from '../../lib/format'
import { ONE_VOTE } from './lib'

/**
 * What the ballot's pages show when the API answers 404 — no window set on this competition, or one that has
 * not opened yet. The message is the server's own, so the two cases read differently without the browser
 * deciding which is which from dates it should not be trusted with.
 */
export function VotingLocked({ slug, message }: { slug: string; message?: string }) {
  return (
    <div className="container page-state">
      <h1>Səsvermə açıq deyil</h1>
      <p className="lede">{message ?? 'Bu müsabiqədə səsvermə yoxdur.'}</p>
      <div className="voting__actions">
        <Link to={`/competitions/${slug}`} className="btn btn--primary">
          Müsabiqəyə qayıt
        </Link>
        <Link to="/competitions" className="btn btn--ghost">
          Müsabiqələrə bax
        </Link>
      </div>
    </div>
  )
}

/**
 * The window, in one line above the works: when it closes while it is open, and that it is over once it is.
 * Shown only when there is something to say — an open vote with no end date would say nothing at all.
 */
export function VotingClosedNote({ voting }: { voting: Pick<Voting, 'state' | 'endsAt'> }) {
  if (voting.state === 'Closed')
    return (
      <p className="voting__window voting__window--closed" role="status">
        Səsvermə başa çatıb. Nəticələr müsabiqənin nəticələr səhifəsində elan olunacaq.
      </p>
    )
  if (voting.state !== 'Open' || !voting.endsAt) return null
  return (
    <p className="voting__window">
      Səsvermə <strong>{formatDate(voting.endsAt)}</strong> tarixinədək davam edir. {ONE_VOTE}
    </p>
  )
}
