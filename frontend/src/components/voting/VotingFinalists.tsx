import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { votingApi } from '../../api/endpoints'
import type { CompetitionSummary, Voting, VotingWork, VotingWorks } from '../../api/types'
import { withLiveCount } from '../../pages/voting/leaderboard'
import { applyVote, ballotSeed, countsVisible, isSinglePool } from '../../pages/voting/lib'
import { VoteDialog, VotingCard } from '../../pages/voting/shared'
import '../../pages/voting/Voting.css'
import './stage.css'

/** Two full rows of the grid; the rest are a click away on the ballot itself. */
const SHOWN = 6

/**
 * The works on the ballot, on the home page, in the grid the design asks for.
 *
 * It borrows the ballot's own card, dialog and receipt handling rather than growing a second way to cast a
 * vote — there is one vote endpoint and one set of rules, and a second implementation of the button is a
 * second place for them to drift.
 */
export function VotingFinalists({
  competition,
  voting,
  id = 'finalists',
  liveCounts,
  onVoted,
}: {
  competition: CompetitionSummary
  voting: Voting
  id?: string
  /** The live ranking's counts, so a card says what the ranking above it says. */
  liveCounts?: ReadonlyMap<string, number>
  /** A vote was cast here: the ranking reads again. */
  onVoted?: () => void
}) {
  const [page, setPage] = useState<VotingWorks | null>(null)
  const [failed, setFailed] = useState(false)
  const [chosen, setChosen] = useState<VotingWork | null>(null)
  const [casting, setCasting] = useState(false)
  const [cast, setCast] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const slug = competition.slug
  const live = voting.state === 'Open'

  useEffect(() => {
    const controller = new AbortController()
    votingApi
      .works(slug, { pageSize: SHOWN, seed: ballotSeed() }, controller.signal)
      .then(setPage)
      .catch(() => {
        if (!controller.signal.aborted) setFailed(true)
      })
    return () => controller.abort()
  }, [slug])

  const confirm = async (captchaToken?: string) => {
    if (!chosen) return
    setCasting(true)
    setError(null)
    try {
      const receipt = await votingApi.vote(slug, chosen.reference, captchaToken)
      setPage((current) => (current ? applyVote(current, receipt) : current))
      onVoted?.()
      // The dialog stays, as the receipt and the invitation to pass the vote on.
      setCast(true)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Səs göndərilmədi. Bir az sonra yenidən cəhd edin.')
      // Whatever the refusal was, what this viewer may do has changed: ask again rather than keep a stale button.
      if (err instanceof ApiError && [401, 403, 409, 422].includes(err.status))
        votingApi.works(slug, { pageSize: SHOWN, seed: ballotSeed() }).then(setPage).catch(() => undefined)
    } finally {
      setCasting(false)
    }
  }

  // Nothing to show is not an error worth a box on the home page; the stage hero already says where things are.
  if (failed || (page && page.items.length === 0)) return null

  return (
    <section className="vgrid" id={id} aria-labelledby="vgrid-title">
      <header className="vgrid__head">
        <div>
          {/* Every work entered and not rejected is on the ballot — not a shortlist, so not "finalists". */}
          <p className="vgrid__eyebrow">Müsabiqə işləri</p>
          <h2 id="vgrid-title" className="vgrid__title">
            Səsvermədə iştirak edən işlər
          </h2>
        </div>
        {live && (
          <span className="vgrid__badge">
            <span className="stage__pulse" aria-hidden="true" />
            Səsvermə açıqdır
          </span>
        )}
      </header>

      {!page ? (
        <ul role="list" className="vgrid__grid" aria-hidden="true">
          {[0, 1, 2].map((i) => (
            <li key={i} className="vcard vcard--skeleton">
              <div className="skeleton" style={{ height: 200 }} />
              <div className="vcard__body">
                <div className="skeleton" style={{ height: 14, width: '40%' }} />
                <div className="skeleton" style={{ height: 24, width: '80%' }} />
                <div className="skeleton" style={{ height: 40 }} />
              </div>
            </li>
          ))}
        </ul>
      ) : (
        <>
          <ul role="list" className="vgrid__grid">
            {page.items.map((w) => (
              <VotingCard
                key={w.reference}
                work={withLiveCount(w, liveCounts)}
                slug={slug}
                // The same rule as the ballot — and the note in the hero says which it is.
                showCount={countsVisible(voting.state)}
                singlePool={isSinglePool(voting)}
                busy={casting && chosen?.reference === w.reference}
                onVote={() => {
                  setError(null)
                  setChosen(w)
                }}
              />
            ))}
          </ul>
          {page.total > page.items.length && (
            <p className="vgrid__more">
              <Link to={`/competitions/${slug}/voting`} className="btn">
                Bütün işlərə bax
                <span className="tabular"> ({page.total})</span>
              </Link>
            </p>
          )}
        </>
      )}

      {chosen && (
        <VoteDialog
          work={chosen}
          busy={casting}
          error={error}
          cast={cast}
          competition={{ slug, title: competition.title }}
          onConfirm={confirm}
          onClose={() => {
            setChosen(null)
            setError(null)
            setCast(false)
          }}
        />
      )}
    </section>
  )
}
