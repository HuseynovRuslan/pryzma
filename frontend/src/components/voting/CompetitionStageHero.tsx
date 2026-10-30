import { Link } from 'react-router-dom'
import type { CompetitionSummary, Voting } from '../../api/types'
import type { Lifecycle, LifecycleStage } from '../../lib/competitionLifecycle'
import { formatDate, formatTime, shortCompetitionTitle } from '../../lib/format'
import { LIVE_COUNTS } from '../../pages/voting/lib'
import { VotePulse } from './VotePulse'
import './stage.css'

/**
 * Where the competition stands, as the front of the page: the stage it is in, the four steps it moves through,
 * and the one deadline that matters today.
 *
 * Every word and number comes from the API — the stage from the dates, the countdown from the same clock the
 * navbar reads. Nothing is hardcoded to a particular competition, so the same component serves the one running
 * now and the next one.
 */

interface Props {
  competition: CompetitionSummary
  voting: Voting | null
  lifecycle: Lifecycle
  headingLevel?: 'h1' | 'h2'
  /** Where "İndi səs ver" goes. On the home page that is the grid below (#finalists); elsewhere, the ballot. */
  voteHref?: string
  /** Inside a carousel the surrounding card is already drawn, so the hero does not draw a second one. */
  bare?: boolean
}

/**
 * One line per stage: the badge beside the stage name, and the sentence under the heading. After the vote there is
 * no sentence: what is under way is said once, by the status section under the hero (see below).
 */
const COPY: Record<LifecycleStage, { badge: string; lede: string | null }> = {
  submissions: {
    badge: 'Qəbul mərhələsi',
    lede: 'İşlərin qəbulu davam edir.',
  },
  review: {
    badge: 'Yoxlama mərhələsi',
    lede: 'İşlərin qəbulu başa çatdı. Təqdim olunan işlər yoxlanılır.',
  },
  votingUpcoming: {
    badge: 'Səsvermə mərhələsi',
    lede: 'Səsvermə tezliklə başlayır.',
  },
  votingLive: {
    badge: 'Səsvermə mərhələsi',
    // One vote per person per competition: categories only sort the works.
    lede: 'Müsabiqə işlərini nəzərdən keçirin və bəyəndiyiniz bir işi seçib səs verin.',
  },
  votingClosed: {
    badge: 'Səsvermə mərhələsi başa çatdı',
    lede: null,
  },
  results: {
    badge: 'Nəticələr açıqlanıb',
    lede: 'Müsabiqənin nəticələri elan olunub.',
  },
}

const EYEBROW: Record<LifecycleStage, string | null> = {
  submissions: 'MƏRHƏLƏ 01: İŞLƏRİN QƏBULU',
  review: 'MƏRHƏLƏ 02: TEXNİKİ YOXLAMA',
  votingUpcoming: 'MƏRHƏLƏ 03: İCTİMAİ SƏSVERMƏ',
  votingLive: 'MƏRHƏLƏ 03: İCTİMAİ SƏSVERMƏ',
  // "İlkin nəticələr" is the status section's own heading, right below.
  votingClosed: null,
  results: 'MƏRHƏLƏ 04: NƏTİCƏLƏR',
}

export function CompetitionStageHero({
  competition,
  voting,
  lifecycle,
  headingLevel = 'h2',
  voteHref,
  bare = false,
}: Props) {
  const { stage, steps, countdown, isFinalDay } = lifecycle
  const copy = COPY[stage]
  const voting_ = stage === 'votingLive'
  const name = shortCompetitionTitle(competition.title)
  const Heading = headingLevel
  /*
   * After the vote and before the result the hero is the competition's summary and nothing more: the stage, the
   * name, the steps, the dates, and the way to the works. The review status — votes being checked, when the result
   * comes — and its figures are the home page's next section, and said once, there (the
   * owner's decision of 7 October 2026). With no panel beside it the hero is one column.
   */
  const summary = stage === 'votingClosed'

  return (
    <section className={`stage${bare ? ' stage--bare' : ''}`} data-stage={stage} aria-labelledby="stage-title">
      <div className="stage__meta">
        <span className="stage__phase">
          <span className="stage__pulse" aria-hidden="true" />
          {copy.badge}
        </span>
        {/* Said plainly, and true: the ballot carries no author. */}
        {(voting_ || stage === 'votingUpcoming') && (
          <span className="stage__privacy">Müəllif məlumatları gizli saxlanılır</span>
        )}
      </div>

      <div className={`stage__grid${summary ? ' stage__grid--summary' : ''}`}>
        <div className="stage__main">
          {EYEBROW[stage] && (
            <p className="stage__eyebrow">
              <span className="stage__dash" aria-hidden="true" />
              {EYEBROW[stage]}
            </p>
          )}
          <Heading id="stage-title" className="stage__title">
            {name}
          </Heading>
          {copy.lede && <p className="stage__lede">{copy.lede}</p>}
          {competition.shortDescription && <p className="stage__about">{competition.shortDescription}</p>}

          <div className="stage__steps">
            <p className="stage__steps-label">Müsabiqənin mərhələləri</p>
            <ol className="stage__rail">
              {steps.map((s) => (
                <li key={s.label} className={`stage__step stage__step--${s.state}`}>
                  <span className="stage__dot" aria-hidden="true">
                    {s.state === 'done' ? '✓' : s.state === 'current' ? '●' : '○'}
                  </span>
                  {s.short ? (
                    /* The full name is always what a screen reader hears; a phone shows the short one. */
                    <span className="stage__step-label">
                      <span className="stage__step-full">{s.label}</span>
                      <span className="stage__step-short" aria-hidden="true">
                        {s.short}
                      </span>
                    </span>
                  ) : (
                    <span className="stage__step-label">{s.label}</span>
                  )}
                  <span className="stage__step-note">{s.note}</span>
                </li>
              ))}
            </ol>
          </div>

          <Dates competition={competition} voting={voting} stage={stage} />
          {summary && <Actions competition={competition} stage={stage} voteHref={voteHref} />}
        </div>

        {!summary && (
          <aside className="stage__panel">
            {countdown ? (
              <>
                <p className="stage__countdown-head">
                  <span className="stage__countdown-label">{countdown.label}</span>
                  {isFinalDay && <span className="stage__urgent">● Son 24 saat</span>}
                </p>
                <div className="stage__tiles" role="timer" aria-live="off">
                  <Tile value={countdown.remaining.days} unit="GÜN" />
                  <Tile value={countdown.remaining.hours} unit="SAAT" />
                  <Tile value={countdown.remaining.minutes} unit="DƏQİQƏ" />
                  <Tile value={countdown.remaining.seconds} unit="SANİYƏ" />
                </div>
                {/* On a phone the works are a long way down; the way to them comes straight after the time left. */}
                {voting_ && voteHref?.startsWith('#') && <JumpToWorks target={voteHref.slice(1)} />}
                {voting_ && voting && <VotePulse voting={voting} slug={competition.slug} className="stage__live" />}
              </>
            ) : stage === 'review' && competition.endDate ? (
              /* Nothing to count towards until a date is set — so say what is known: when entries closed, and
                 that the next date will appear here. No invented date, and no promise of which stage comes next:
                 some competitions go from review straight to their result. */
              <div className="stage__waiting">
                <p className="stage__countdown-head">
                  <span className="stage__countdown-label">Qəbul bağlandı</span>
                </p>
                <p className="stage__closed">
                  <span className="stage__closed-date">{formatDate(competition.endDate)}</span>
                  <span className="stage__closed-time tabular">saat {formatTime(competition.endDate)}</span>
                </p>
                <p className="stage__next">İşlər yoxlanıldıqdan sonra növbəti mərhələnin tarixi burada elan olunacaq.</p>
              </div>
            ) : (
              <p className="stage__countdown-head">
                <span className="stage__countdown-label">{copy.badge}</span>
              </p>
            )}

            {(voting_ || stage === 'votingUpcoming') && (
              /* Two promises, and the second follows the switch: a page that prints the count must not say it hides it. */
              <p className="stage__note">
                Səsvermə müddətində işlərin müəllif məlumatları gizli saxlanılır.{' '}
                {LIVE_COUNTS ? 'Hər işin səs sayı canlı göstərilir.' : 'Aralıq səs nəticələri səsvermə bitənədək göstərilmir.'}
              </p>
            )}

            <Actions competition={competition} stage={stage} voteHref={voteHref} />
          </aside>
        )}
      </div>
    </section>
  )
}

/**
 * "İşlərə keç ↓": the works on the same page, reached at once. Shown on a phone only — on a wide screen the panel's
 * own "İndi səs ver" sits right beside the countdown and does the same.
 */
function JumpToWorks({ target }: { target: string }) {
  return (
    <a
      href={`#${target}`}
      className="btn btn--primary stage__jump"
      onClick={(e) => {
        const el = document.getElementById(target)
        if (!el) return
        e.preventDefault()
        const still = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
        el.scrollIntoView({ behavior: still ? 'auto' : 'smooth', block: 'start' })
      }}
    >
      İşlərə keç ↓
    </a>
  )
}

function Tile({ value, unit }: { value: number; unit: string }) {
  return (
    <span className="stage__tile">
      <span className="stage__tile-value tabular">{String(value).padStart(2, '0')}</span>
      <span className="stage__tile-unit">{unit}</span>
    </span>
  )
}

/** The dates the competition actually has; anything undecided is simply not listed. */
function Dates({
  competition,
  voting,
  stage,
}: {
  competition: CompetitionSummary
  voting: Voting | null
  stage: LifecycleStage
}) {
  const rows: { label: string; value: string; live?: boolean }[] = []
  if (competition.startDate || competition.endDate) {
    const from = competition.startDate ? formatDate(competition.startDate) : null
    const to = competition.endDate ? formatDate(competition.endDate) : null
    rows.push({
      label: 'Qəbul',
      value: from && to ? `${from} – ${to}` : (to ?? from ?? ''),
      live: stage === 'submissions',
    })
  }
  if (voting?.startsAt && voting.endsAt) {
    rows.push({
      label: 'Səsvermə',
      value: `${formatDate(voting.startsAt)} – ${formatDate(voting.endsAt)}`,
      live: stage === 'votingLive',
    })
  }
  if (rows.length === 0) return null
  return (
    <p className="stage__dates">
      <span className="stage__dates-label">Əsas tarixlər:</span>
      {rows.map((r, i) => (
        <span key={r.label} className={r.live ? 'stage__date stage__date--live' : 'stage__date'}>
          {i > 0 && <span className="stage__sep" aria-hidden="true" />}
          {r.label} ({r.value})
        </span>
      ))}
    </p>
  )
}

function Actions({
  competition,
  stage,
  voteHref,
}: {
  competition: CompetitionSummary
  stage: LifecycleStage
  voteHref?: string
}) {
  const slug = competition.slug
  const ballot = `/competitions/${slug}/voting`
  const page = `/competitions/${slug}`

  if (stage === 'submissions')
    return (
      <div className="stage__cta">
        <Link to={`/competitions/${slug}/submit`} className="btn btn--primary">
          İşini təqdim et
        </Link>
        <Link to={page} className="btn stage__cta-ghost">
          Müsabiqəyə bax
        </Link>
      </div>
    )

  if (stage === 'votingLive')
    return (
      <div className="stage__cta">
        {/* On the home page this is the grid below; everywhere else, the ballot itself. */}
        <a href={voteHref ?? ballot} className={`btn btn--primary${voteHref?.startsWith('#') ? ' stage__cta-wide' : ''}`}>
          İndi səs ver
        </a>
        <Link to={ballot} className="btn stage__cta-ghost">
          İşlərə bax
        </Link>
      </div>
    )

  if (stage === 'votingClosed')
    // No vote button once the window has shut — the ballot stays readable, nothing more. Under the dates, not in a
    // panel: one quiet button, not a call to act.
    return (
      <div className="stage__cta">
        <Link to={ballot} className="btn stage__cta-ghost">
          İşlərə bax
        </Link>
      </div>
    )

  return (
    <div className="stage__cta">
      <Link to={page} className="btn stage__cta-ghost">
        Müsabiqəyə bax
      </Link>
    </div>
  )
}
