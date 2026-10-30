import { memo, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import type { ApiError } from '../api/client'
import { catalog } from '../api/endpoints'
import type { CompetitionSummary, Voting } from '../api/types'
import { CompetitionCard } from '../components/CompetitionCard'
import { CompetitionStageCarousel } from '../components/voting/CompetitionStageCarousel'
import { LiveLeaderboard } from '../components/voting/LiveLeaderboard'
import { ProvisionalRanking } from '../components/voting/ProvisionalRanking'
import { ReviewStatus } from '../components/voting/VotingReview'
import { VotingFinalists } from '../components/voting/VotingFinalists'
import { useLeaderboard } from './voting/leaderboard'
import { useVotingAudit } from './voting/useVotingAudit'
import { useFeaturedCompetition } from '../lib/featuredCompetitionContext'
import { PageError } from '../components/PageState'
import { participationSteps } from '../content/steps'
import { useApi } from '../lib/useApi'
import { usePlatform } from '../platform/context'
import { useTitle } from '../lib/useTitle'
import { HomeWinners, HomeWinnersStrip } from './HomeWinners'
import { useHomeWinners } from './homeWinnersData'
import './HomePage.css'

export default function HomePage() {
  const { homeHeroTitle, homeHeroSubtitle, platformName } = usePlatform()
  useTitle(undefined)
  const home = useApi((signal) => catalog.home(signal), [])
  const open = home.data?.openCompetitions ?? []

  return (
    <>
      <HomeLead title={homeHeroTitle} subtitle={homeHeroSubtitle} platformName={platformName} />

      <BelowLead homeSettled={home.status !== 'loading'} openCount={open.length}>
        {/* After the vote, before the result: the review status and the provisional top ten, right under the lead —
            the first thing on the page after the competition itself, with no click needed to see where it stands. */}
        <HomeProvisionalGate />

        <OpenCompetitionsGate status={home.status} open={open} error={home.error} retry={home.retry} />

        <HomeFinalistsGate />

        {/* What the place is, in two lines. The whole of it is at /about; this is only enough to answer someone
            who has just arrived and does not yet know what they are looking at. */}
        <section className="container about-strip" aria-labelledby="about-title">
          <h2 id="about-title">{platformName} nədir?</h2>
          <p>
            Müxtəlif mövzularda açıq yaradıcılıq müsabiqələrinin keçirildiyi platformadır. Peşəkar olmaq şərt deyil —
            əsas olan mövzuya özünəməxsus baxış və orijinal ideyadır
          </p>
          <Link to="/about" className="btn">
            {platformName} haqqında
          </Link>
        </section>

        <section className="steps-band" aria-labelledby="steps-title">
          <div className="container">
            <header className="steps-band__head">
              <h2 id="steps-title">Necə iştirak etməli?</h2>
              <Link to="/how-to-participate">Ətraflı</Link>
            </header>
            <ol role="list" className="steps">
              {participationSteps(platformName).map((s, i) => (
                <li key={s.title} className="steps__item">
                  <span className="steps__n tabular" aria-hidden="true">
                    {i + 1}
                  </span>
                  <h3>{s.title}</h3>
                  <p>{s.text}</p>
                </li>
              ))}
            </ol>
          </div>
        </section>
      </BelowLead>
    </>
  )
}

/**
 * Everything under the lead waits until the page knows what goes there — the open calls, and whether a published
 * result has winners to show first — and then appears in one frame. Showing the static bands at once and pushing them
 * down as each answer arrived moved the whole page under the reader (layout shift 0.32 on a desktop before the
 * winners, 0.84 with them). A placeholder holds the space meanwhile, so the footer does not ride up either.
 * Only this gate follows the one-second clock; the bands it is handed are the page's own, unchanged elements.
 */
function BelowLead({ homeSettled, openCount, children }: { homeSettled: boolean; openCount: number; children: ReactNode }) {
  const { ready, published, slides } = useFeaturedCompetition()
  const winners = useHomeWinners(ready, published)
  if (!homeSettled || !winners.settled) return <div className="home-pending" aria-busy="true" />
  // Another competition is being run (an open call, or the lead shows one): its calls come first, the winners shrink to a line.
  const live = openCount > 0 || slides.length > 0
  return (
    <>
      {/* A published result: its winners right under the lead, both choices, every place (the owner, 16 October 2026). */}
      {winners.result && (live ? <HomeWinnersStrip result={winners.result} /> : <HomeWinners result={winners.result} />)}
      {children}
    </>
  )
}

/** Only this small subtree follows the one-second lifecycle clock; the page and its competition grid do not. */
function HomeLead({ title, subtitle, platformName }: { title: string; subtitle: string; platformName: string }) {
  const featured = useFeaturedCompetition()
  // A competition whose results are out has nothing left to lead with; the others keep their place in the order.
  const slides = featured.slides.filter((s) => s.lifecycle.stage !== 'results')

  if (slides.length > 0) {
    return (
      <div className="container home-stage">
        <CompetitionStageCarousel slides={slides} voteHref="#finalists" />
      </div>
    )
  }

  return <PlatformIntro title={title} subtitle={subtitle} platformName={platformName} />
}

function PlatformIntro({ title, subtitle, platformName }: { title: string; subtitle: string; platformName: string }) {
  return (
    <section className="home-intro container" aria-labelledby="hero-title">
      <div className="home-intro__panel">
        <h1 id="hero-title" className="hero__title home-intro__title">
          {title
            .split(/\s+/)
            .filter(Boolean)
            .map((word, i, all) => (
              <span key={`${word}-${i}`}>
                <span className="hero__word" style={{ animationDelay: `${i * 90}ms` }}>
                  {word}
                </span>
                {i < all.length - 1 ? ' ' : ''}
              </span>
            ))}
        </h1>
        <div className="home-intro__copy">
          <p className="home-intro__lede">{subtitle}</p>
          <p className="home-intro__actions">
            <Link to="/how-to-participate">Necə iştirak etməli?</Link>
            <Link to="/about">{platformName} haqqında</Link>
          </p>
        </div>
      </div>
    </section>
  )
}

interface OpenProps {
  status: 'loading' | 'error' | 'success'
  open: CompetitionSummary[]
  error: ApiError | undefined
  retry: () => void
}

/**
 * The open calls — or, when there are none, a box saying so. That box is right on a quiet day and wrong on a busy
 * one: while the hero above leads with a competition (a vote, a review, a call about to close) it says "nothing is
 * open" between the hero and the works it is about, and pushes the works down the page. So it steps aside whenever
 * the hero has something to lead with, and stays when the page would otherwise be empty.
 *
 * Until the featured competitions have loaded the answer is not known, and nothing is shown rather than a box that
 * flashes and disappears. Only this gate follows the one-second clock; the section itself is memoised.
 */
function OpenCompetitionsGate(props: OpenProps) {
  const featured = useFeaturedCompetition()
  const leading = !featured.ready || featured.slides.some((s) => s.lifecycle.stage !== 'results')
  // The vote's stage as the hero reads it — the server's answer, followed past its boundaries — so a card of the same
  // competition never says "open" beside a hero that says it is over. A string, so the memoised section below
  // renders again only when a stage actually changes, not on every tick of the clock.
  const live = featured.slides
    .filter((s) => s.voting)
    .map((s) => `${s.competition.slug}=${s.lifecycle.stage}`)
    .join('|')
  return <StableOpenCompetitions {...props} quietWhenEmpty={leading} live={live} />
}

/** A card's competition with its vote's state taken from the live stage, where the hero has one for it. */
function withLiveStage(c: CompetitionSummary, live: string): CompetitionSummary {
  const stage = live
    .split('|')
    .find((entry) => entry.startsWith(`${c.slug}=`))
    ?.slice(c.slug.length + 1)
  if (stage === 'votingClosed') return { ...c, voting: 'Closed' }
  if (stage === 'votingLive') return { ...c, voting: 'Open' }
  if (stage === 'results') return { ...c, resultsPublished: true }
  return c
}

function OpenCompetitions({
  status,
  open,
  error,
  retry,
  quietWhenEmpty,
  live,
}: OpenProps & { quietWhenEmpty: boolean; live: string }) {
  if (status === 'success' && open.length === 0 && quietWhenEmpty) return null
  return (
    <section className="section container home-open" aria-labelledby="open-title" aria-busy={status === 'loading'}>
      <header className="section-head">
        <h2 id="open-title">Hazırda açıq müsabiqələr</h2>
        {status === 'success' && open.length > 0 && (
          <Link to="/competitions" className="section-head__aside">
            Bütün müsabiqələr
          </Link>
        )}
      </header>

      {status === 'loading' && <div className="skeleton" style={{ height: 620 }} />}
      {status === 'error' && error && <PageError error={error} onRetry={retry} />}
      {status === 'success' &&
        (open.length === 0 ? (
          <div className="empty">
            <h3>Hazırda qəbulu açıq müsabiqə yoxdur</h3>
            <p>Yeni çağırışlar elan olunan kimi burada görünəcək.</p>
            <Link to="/competitions" className="btn">
              Bitmiş müsabiqələrə bax
            </Link>
          </div>
        ) : (
          <div className="competition-card-grid">
            {open.slice(0, 6).map((c, i) => (
              <CompetitionCard key={c.id} competition={withLiveStage(c, live)} headingLevel="h3" eager={i < 3} />
            ))}
          </div>
        ))}
    </section>
  )
}

const StableOpenCompetitions = memo(OpenCompetitions)

const StableVotingFinalists = memo(VotingFinalists)

const StableLiveLeaderboard = memo(LiveLeaderboard)

/** The gate is light enough to tick; the approved finalist grid is memoized and updates only when its data does. */
function HomeFinalistsGate() {
  const featured = useFeaturedCompetition()
  if (!featured.competition || !featured.voting || featured.lifecycle?.stage !== 'votingLive') return null
  return <HomeLiveVote competition={featured.competition} voting={featured.voting} />
}

/**
 * The vote is over and the official result not yet published: the review status with the real totals, and under it
 * the provisional top ten, both on the home page itself (the owner's decision of 7 October 2026). Once the result is
 * published the stage is "results" and this gives way to the results the site already shows.
 */
function HomeProvisionalGate() {
  const featured = useFeaturedCompetition()
  if (!featured.competition || !featured.voting || featured.lifecycle?.stage !== 'votingClosed') return null
  return <HomeProvisional slug={featured.competition.slug} />
}

function HomeProvisional({ slug }: { slug: string }) {
  // Both keep reading while the vote is closed (every 30 s, only while the tab is visible), so a vote set aside
  // moves the ranking and the totals here without a reload.
  const live = useLeaderboard(slug)
  const audit = useVotingAudit(slug, true)
  return (
    <div className="container home-provisional">
      <ReviewStatus audit={audit} />
      <ProvisionalRanking board={live.board} slug={slug} />
    </div>
  )
}

/**
 * While a vote is live: the stage above, then where the vote stands, then the works. The ranking and the cards
 * share one read every ten seconds, so a card never says a different count from the row above it.
 */
function HomeLiveVote({ competition, voting }: { competition: CompetitionSummary; voting: Voting }) {
  const live = useLeaderboard(competition.slug)
  return (
    <>
      <div className="container home-live">
        <StableLiveLeaderboard
          live={live}
          slug={competition.slug}
          galleryHref="#finalists"
          votedReference={voting.viewer?.votedWorks[0] ?? null}
        />
      </div>
      <div className="container home-finalists">
        <StableVotingFinalists
          competition={competition}
          voting={voting}
          liveCounts={live.counts}
          onVoted={live.refresh}
        />
      </div>
    </>
  )
}
