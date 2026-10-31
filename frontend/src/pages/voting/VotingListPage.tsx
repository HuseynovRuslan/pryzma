import { useEffect, useRef, useState } from 'react'
import { Link, Navigate, useParams, useSearchParams } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { votingApi } from '../../api/endpoints'
import type { Voting, VotingWork, VotingWorks } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { Breadcrumbs } from '../../components/Breadcrumbs'
import { PageError, PageLoading } from '../../components/PageState'
import { accentVars } from '../../lib/accent'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { usePlatform } from '../../platform/context'
import { LiveLeaderboard } from '../../components/voting/LiveLeaderboard'
import { VotePulse } from '../../components/voting/VotePulse'
import { AuditNotice, AuditUpdates, ReviewStatus } from '../../components/voting/VotingReview'
import { GalleryFilter } from '../results/GalleryFilter'
import { useLeaderboard, withLiveCount } from './leaderboard'
import { afterVote, appendPage, applyVote, ballotSeed, countsVisible, isSinglePool, voteRefusal } from './lib'
import { ShareVote } from './ShareVote'
import { VoteDialog, VotingCard } from './shared'
import { VotingClosedNote, VotingLocked } from './state'
import { useLiveVoting } from './useLiveVoting'
import { useVotingAudit } from './useVotingAudit'
import '../../components/voting/stage.css'
import './Voting.css'

/** Works per request. The rest arrive on demand, so a competition with hundreds stays light on a phone. */
const PAGE = 24

/** Long enough that typing a word does not cost a request per letter, short enough to feel immediate. */
const SEARCH_DELAY_MS = 300

/**
 * Every answer on this page is shaped to whoever is asking, so nothing is asked for until the session has been
 * restored. A page loaded straight from an address starts anonymous for the moment the refresh takes; fetching
 * then would show a signed-in person a ballot that tells them to sign in, and nothing would correct it.
 */
export default function VotingListPage() {
  const { status } = useAuth()
  if (status === 'loading') return <PageLoading label="Səsvermə yüklənir" />
  return <VotingList key={status} />
}

/**
 * The ballot. The competition's own summary (state, window, nominations) is asked for once; the works are asked
 * for again whenever the nomination or the search changes, which keeps the header still while the grid moves.
 *
 * Nothing here decides who may vote: every button renders the reason the server gave it.
 */
function VotingList() {
  const { slug = '' } = useParams()
  const [params, setParams] = useSearchParams()
  const nomination = params.get('nomination')
  const q = params.get('q') ?? ''

  // Follows the window as it moves: the page turns into the review stage at the end without a reload.
  const summary = useLiveVoting(slug)
  const works = useApi(
    (signal) => votingApi.works(slug, { nomination, q, pageSize: PAGE, seed: ballotSeed() }, signal),
    [slug, nomination, q],
  )

  useTitle(summary.data ? `Səsvermə — ${summary.data.competition.title}` : 'Səsvermə')

  if (summary.status === 'loading') return <PageLoading label="Səsvermə yüklənir" />
  if (summary.status === 'error')
    return summary.error.status === 404 ? (
      <VotingLocked slug={slug} message={summary.error.message} />
    ) : (
      <PageError error={summary.error} onRetry={summary.retry} />
    )

  return (
    <Ballot
      voting={summary.data}
      slug={slug}
      nomination={nomination}
      q={q}
      onSearch={(value) => {
        const next = new URLSearchParams(params)
        next.delete('page')
        if (value) next.set('q', value)
        else next.delete('q')
        setParams(next, { replace: true, preventScrollReset: true })
      }}
      works={works}
      refreshVoting={summary.refresh}
    />
  )
}

type WorksState = ReturnType<typeof useApi<VotingWorks>>

function Ballot({
  voting,
  slug,
  nomination,
  q,
  onSearch,
  works,
  refreshVoting,
}: {
  voting: Voting
  slug: string
  nomination: string | null
  q: string
  onSearch: (value: string) => void
  works: WorksState
  /** Asks the server for the vote's state again — after a refused vote, or when the ranking reports another. */
  refreshVoting: () => Promise<Voting | null>
}) {
  const { platformName } = usePlatform()
  const c = voting.competition
  // The page owns the works it has been handed, so a cast vote can be shown at once without asking again.
  const [page, setPage] = useState<VotingWorks | null>(null)
  const [more, setMore] = useState<'idle' | 'loading' | 'failed'>('idle')
  const [chosen, setChosen] = useState<VotingWork | null>(null)
  const [casting, setCasting] = useState(false)
  const [cast, setCast] = useState(false)
  const [voteError, setVoteError] = useState<string | null>(null)
  // The ranking above the works, and the cards' counts with it: one request every ten seconds for the whole page.
  const live = useLeaderboard(slug)
  // After the window: the review stage until the result is published, then the result itself.
  const after = afterVote(voting)
  // The review stage's real totals and audit lines, read only while it is shown.
  const audit = useVotingAudit(slug, after === 'review')

  useEffect(() => {
    if (works.status === 'success') setPage(works.data)
    else setPage(null)
  }, [works.status, works.data])

  // The ranking is read every few seconds anyway; when it reports a state the summary does not, the summary asks.
  const boardState = live.board?.state
  useEffect(() => {
    if (boardState && boardState !== voting.state) void refreshVoting()
  }, [boardState, voting.state, refreshVoting])

  // When the server confirms the end, the cards are asked for again so each button says what the server says.
  const lastState = useRef(voting.state)
  const retryWorks = works.retry
  useEffect(() => {
    if (lastState.current === 'Open' && voting.state === 'Closed') retryWorks()
    lastState.current = voting.state
  }, [voting.state, retryWorks])

  const active = voting.nominations.find((n) => n.slug === nomination) ?? null
  // The page's own copy of who is looking — it learns about a vote cast here before the summary does.
  const viewer = page?.viewer ?? voting.viewer

  const loadMore = async () => {
    if (!page) return
    setMore('loading')
    try {
      const next = await votingApi.works(slug, {
        nomination,
        q,
        page: Math.floor(page.items.length / PAGE) + 1,
        pageSize: PAGE,
        seed: ballotSeed(),
      })
      setPage((current) => (current ? appendPage(current, next.items) : next))
      setMore('idle')
    } catch {
      setMore('failed')
    }
  }

  const confirm = async (captchaToken?: string) => {
    if (!chosen) return
    setCasting(true)
    setVoteError(null)
    try {
      const receipt = await votingApi.vote(slug, chosen.reference, captchaToken)
      setPage((current) => (current ? applyVote(current, receipt) : current))
      // The ranking reads again at once, so the vote is in it before the dialog is even closed.
      live.refresh()
      // The dialog stays, as the receipt and the invitation to pass the vote on.
      setCast(true)
    } catch (err) {
      // The server's own words: already voted, window shut, own work, account too new. The page then asks again,
      // because whatever it was, what this viewer may do has changed.
      setVoteError(voteRefusal(err))
      if (err instanceof ApiError && [401, 403, 409, 422].includes(err.status)) works.retry()
      // 422 is the window: the page asks again, and turns into the review stage once the server says so.
      if (err instanceof ApiError && err.status === 422) void refreshVoting()
    } finally {
      setCasting(false)
    }
  }

  if (after === 'results') return <Navigate to={`/competitions/${slug}/results`} replace />

  return (
    <div className="voting" style={accentVars(c.project.accentColor)}>
      <div className="container">
        <Breadcrumbs
          items={[
            { label: platformName, to: '/' },
            { label: c.project.name, to: `/projects/${c.project.slug}` },
            { label: c.title, to: `/competitions/${c.slug}` },
            { label: 'Səsvermə' },
          ]}
        />

        {after === 'review' ? (
          /* The vote is over and the result not yet published (the owner's decision of 7 October 2026): the review
             status heads the page, the ranking stays — provisional, on valid votes, updating as votes are set aside —
             and the audit lines follow. The works stay below, to be looked at; nothing on them can be pressed. */
          <>
            <div className="voting__review">
              <ReviewStatus audit={audit} headingLevel="h1">
                <a href="#works" className="btn">
                  Müsabiqə işlərinə bax
                </a>
              </ReviewStatus>
            </div>
            {/* Why the ranking changed after the vote closed (D14): above the ranking, never hidden. */}
            <AuditNotice audit={audit} />
            <div className="voting__live">
              <LiveLeaderboard
                live={live}
                slug={slug}
                galleryHref="#works"
                votedReference={viewer?.votedWorks[0] ?? null}
                competitionTitle={c.title}
              />
            </div>
            <div className="voting__audit">
              <AuditUpdates audit={audit} />
            </div>
          </>
        ) : (
          <>
            <header className="voting__head">
              <p className="voting__eyebrow">{c.title}</p>
              <h1 className="voting__title">Səsvermə siyahısı</h1>
              <p className="voting__lede">Müsabiqə işlərini nəzərdən keçirin və bəyəndiyiniz bir işi seçib səs verin.</p>
              <VotingClosedNote voting={voting} />
              <VotePulse voting={voting} slug={slug} className="voting__pulse" />
              {voting.state === 'Open' && (viewer?.votedWorks.length ?? 0) > 0 && !chosen && (
                // Somebody who has voted already, here again: the invitation, folded away until they want it.
                <details className="voting__invite">
                  <summary>Səsiniz qeydə alınıb. Dostlarınızı da səs verməyə çağırın</summary>
                  <ShareVote slug={slug} competitionTitle={c.title} lead="Onlara bu mesaj gedəcək:" />
                </details>
              )}
            </header>

            <div className="voting__live">
              <LiveLeaderboard
                live={live}
                slug={slug}
                galleryHref="#works"
                votedReference={viewer?.votedWorks[0] ?? null}
                competitionTitle={c.title}
              />
            </div>
          </>
        )}

        <div className="voting__filters" id="works">
          {/* The categories only narrow what is shown — the one vote is the same whichever is picked. One category
              is no choice at all: a filter with a single chip only repeats the total. */}
          {!isSinglePool(voting) && (
            <GalleryFilter
              label={c.categoryLabel ?? 'Kateqoriyalar'}
              allLabel="Hamısı"
              param="nomination"
              categories={voting.nominations.map((n) => ({ slug: n.slug, name: n.name, count: n.workCount }))}
              selected={active?.slug ?? null}
              total={voting.workCount}
              collapsible
            />
          )}
          <Search value={q} onChange={onSearch} />
        </div>

        {works.status === 'error' ? (
          <PageError error={works.error} onRetry={works.retry} />
        ) : !page ? (
          <WorksSkeleton />
        ) : page.items.length === 0 ? (
          <div className="empty">
            <h3>Nəticə tapılmadı</h3>
            <p>Axtarış meyarlarınıza uyğun gələn heç bir müsabiqə işi tapılmadı.</p>
            <Link to="?" className="btn">
              Bütün işlərə bax
            </Link>
          </div>
        ) : (
          <>
            <p className="voting__count" aria-live="polite">
              {active ? `${active.name}: ` : ''}
              <span className="tabular">{page.total}</span> iş
            </p>
            <ul role="list" className="voting__grid">
              {page.items.map((w) => (
                <VotingCard
                  key={w.reference}
                  work={withLiveCount(after === 'review' ? closedWork(w) : w, live.counts)}
                  slug={slug}
                  // Whether the running total is public while the window is open is LIVE_COUNTS; the hero says which.
                  showCount={countsVisible(voting.state)}
                  singlePool={isSinglePool(voting)}
                  busy={casting && chosen?.reference === w.reference}
                  onVote={() => {
                    setVoteError(null)
                    setChosen(w)
                  }}
                />
              ))}
            </ul>
            {page.items.length < page.total && (
              <div className="voting__more">
                <p className="muted">
                  <span className="tabular">{page.items.length}</span> / <span className="tabular">{page.total}</span> iş
                  göstərilir
                </p>
                <button type="button" className="btn" onClick={loadMore} disabled={more === 'loading'}>
                  {more === 'loading' ? 'Yüklənir…' : 'Daha çox göstər'}
                </button>
                {more === 'failed' && (
                  <p className="vnote" role="alert">
                    Növbəti işlər yüklənmədi. Yenidən cəhd edin.
                  </p>
                )}
              </div>
            )}
          </>
        )}
      </div>

      {chosen && (
        <VoteDialog
          work={chosen}
          busy={casting}
          error={voteError}
          cast={cast}
          competition={{ slug, title: c.title }}
          onConfirm={confirm}
          onClose={() => {
            setChosen(null)
            setVoteError(null)
            setCast(false)
          }}
        />
      )}
    </div>
  )
}

/**
 * A card once the window has ended by the server's clock: what can no longer be done says so. A receipt for a vote
 * already cast, and the reasons that were never about the window, stay as the server gave them.
 */
function closedWork(w: VotingWork): VotingWork {
  return w.eligibility === 'Allowed' || w.eligibility === 'NotSignedIn' || w.eligibility === 'EmailNotConfirmed'
    ? { ...w, eligibility: 'VotingNotOpen' }
    : w
}

/** Search over the works' names. Not over authors: on this surface a work has no author — D2. */
function Search({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const [text, setText] = useState(value)
  const latest = useRef(onChange)
  latest.current = onChange

  // The address follows the box, a moment behind, so a shared link carries the search and typing costs one request.
  useEffect(() => {
    if (text === value) return
    const timer = setTimeout(() => latest.current(text), SEARCH_DELAY_MS)
    return () => clearTimeout(timer)
  }, [text, value])

  // Somebody else changed it — the "Bütün işlərə bax" link, or the back button.
  useEffect(() => setText(value), [value])

  return (
    <div className="vsearch">
      <label htmlFor="voting-q" className="gfilter__label">
        İşin adı üzrə axtar
      </label>
      <input
        id="voting-q"
        type="search"
        className="input"
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder="İşlərin adı üzrə axtar…"
        autoComplete="off"
        spellCheck={false}
        enterKeyHint="search"
      />
    </div>
  )
}

/** The grid's own loading state, so the header and the filters stay put while the works change. */
function WorksSkeleton() {
  return (
    <ul role="list" className="voting__grid" aria-hidden="true">
      {[0, 1, 2, 3, 4, 5].map((i) => (
        <li key={i} className="vcard vcard--skeleton">
          <div className="skeleton" style={{ height: 200 }} />
          <div className="vcard__body">
            <div className="skeleton" style={{ height: 14, width: '40%' }} />
            <div className="skeleton" style={{ height: 24, width: '80%' }} />
            <div className="skeleton" style={{ height: 37 }} />
          </div>
        </li>
      ))}
    </ul>
  )
}
