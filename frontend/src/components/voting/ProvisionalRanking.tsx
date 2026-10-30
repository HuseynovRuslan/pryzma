import { Link } from 'react-router-dom'
import type { VotingLeaderboard } from '../../api/types'
import { azOrdinal } from '../../lib/text'
import { votesLabel } from '../../pages/voting/lib'
import './provisional.css'

/** How many works the home page lists without a click. */
export const HOME_TOP = 10

/**
 * "İlkin reytinq" on the home page, after the vote and before the official result (the owner's decision of
 * 7 October 2026): the top ten by valid votes, in plain sight — no fold, no carousel, no other page. Each row is
 * the rank, the work's public picture, its reference and title, and its count, so a phone reads rank and count at
 * a glance. It is the vote's provisional order: its title says so and its lede that it may still change while the
 * audit runs; that it is not the final result is said once, by the review status right above it.
 *
 * The board comes from the page's own live ranking (useLeaderboard), which keeps reading while the vote is closed,
 * so a vote set aside moves these rows without a reload.
 */
export function ProvisionalRanking({
  board,
  slug,
  top = HOME_TOP,
}: {
  board: VotingLeaderboard | null
  slug: string
  top?: number
}) {
  const entries = board?.entries.slice(0, top) ?? []
  return (
    <section className="prank" aria-labelledby="prank-title">
      <header className="prank__head">
        <h2 id="prank-title" className="prank__title">
          İlkin reytinq
        </h2>
        <p className="prank__lede">Bu sıralama audit davam etdiyi üçün dəyişə bilər.</p>
      </header>

      {board === null ? (
        <p className="prank__wait" role="status">
          Reytinq yüklənir…
        </p>
      ) : (
        <ol className="prank__list" role="list">
          {entries.map((e) => (
            <li key={e.reference} className={`prank__row${e.rank === 1 ? ' is-lead' : ''}`}>
              <Link to={`/competitions/${slug}/voting/${e.reference}`} className="prank__link">
                <span className="prank__rank tabular" aria-label={`${azOrdinal(e.rank)} yer`}>
                  {e.rank}
                </span>
                <span className="prank__thumb" aria-hidden="true">
                  {e.preview && <img src={e.preview.thumbUrl} alt="" width={56} height={56} loading="lazy" decoding="async" />}
                </span>
                <span className="prank__work">
                  <span className="prank__ref tabular">{e.reference}</span>
                  {e.title && <span className="prank__name">{e.title}</span>}
                </span>
                <span className="prank__votes tabular">{votesLabel(e.voteCount)}</span>
              </Link>
            </li>
          ))}
        </ol>
      )}

      <div className="prank__foot">
        <Link to={`/competitions/${slug}/voting#works`} className="btn">
          Bütün işlərə bax
        </Link>
      </div>
    </section>
  )
}
