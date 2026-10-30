import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { catalog } from '../api/endpoints'
import type { CompetitionDetail } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { authPath, participatePath, saveIntent } from '../auth/intent'
import { Breadcrumbs } from '../components/Breadcrumbs'
import { CategoryPicker } from '../components/CategoryPicker'
import { PageError, PageLoading } from '../components/PageState'
import { PhaseStatus } from '../components/Status'
import { ReviewStatus } from '../components/voting/VotingReview'
import { campaignFor } from '../content/campaigns'
import { entrySteps } from '../content/steps'
import { accentVars } from '../lib/accent'
import { track } from '../lib/analytics'
import { daysLeft, formatDate, formatDayMonth, formatTime, shortCompetitionTitle } from '../lib/format'
import { usePlatform } from '../platform/context'
import { formatPrizeFund, parsePrizes, prizeFund } from '../lib/prizes'
import { useApi } from '../lib/useApi'
import { useMediaQuery } from '../lib/useMediaQuery'
import { useTitle } from '../lib/useTitle'
import { afterVote } from './voting/lib'
import { useLiveVoting } from './voting/useLiveVoting'
import { useVotingAudit } from './voting/useVotingAudit'
import './CompetitionDetailPage.css'

/**
 * A competition's page — and, for a competition an ad points at, the page the ad opens (see content/campaigns.ts).
 * `slug` is given by such an address ("/emircan"); otherwise it comes from /competitions/:slug.
 */
export default function CompetitionDetailPage({ slug: fixedSlug }: { slug?: string }) {
  const params = useParams()
  const slug = fixedSlug ?? params.slug ?? ''
  const state = useApi((signal) => catalog.competition(slug, signal), [slug])
  const campaign = state.data?.isAcceptingSubmissions ? campaignFor(slug) : null
  useTitle(campaign?.headline ?? state.data?.title)

  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  return <CompetitionView competition={state.data} reload={state.retry} />
}

function Paragraphs({ text }: { text: string }) {
  return (
    <div className="prose">
      {text.split(/\n{2,}/).map((para, i) => (
        <p key={i}>{para}</p>
      ))}
    </div>
  )
}

/**
 * The nominations as the admin wrote them: one block per nomination, its first line the name. Anything without a
 * name is skipped, so a stray blank line never produces an empty entry.
 */
function Nominations({ text }: { text: string }) {
  const items = text
    .split(/\n{2,}/)
    .map((block) => {
      const [name, ...rest] = block.split('\n')
      return { name: name.trim(), body: rest.join('\n').trim() }
    })
    .filter((item) => item.name)
  if (items.length === 0) return null
  return (
    <ol role="list" className="nominations">
      {items.map((item, i) => (
        <li key={item.name} className="nominations__item">
          <span className="nominations__n tabular" aria-hidden="true">
            {String(i + 1).padStart(2, '0')}
          </span>
          <h3 className="nominations__name">{item.name}</h3>
          {item.body && <p className="nominations__body">{item.body}</p>}
        </li>
      ))}
    </ol>
  )
}

/**
 * The prizes, read the way prizes are read: the amount first, the place under it. Only what the admin's text
 * actually spells out as a place is set this way; anything else stays the paragraph it was written as.
 */
function Prizes({ text }: { text: string }) {
  const { places, note } = parsePrizes(text)
  if (places.length === 0) return <Paragraphs text={text} />
  return (
    <div className="prizes">
      <ol role="list" className="prizes__list">
        {places.map((p) => (
          <li key={p.place} className="prizes__item">
            <span className="prizes__award">{p.award}</span>
            <span className="prizes__place">{p.place}</span>
            {p.extra && <span className="prizes__extra">+ {p.extra}</span>}
          </li>
        ))}
      </ol>
      {note && <div className="prizes__note prose">{note}</div>}
    </div>
  )
}

/**
 * A section of the page: a heading and its text. On a phone the long ones fold away behind their heading, so the
 * reader who came to enter is not made to scroll through the rules first — and the reader who wants them opens one.
 */
function Block({
  id,
  title,
  folded = false,
  children,
}: {
  id: string
  title: string
  folded?: boolean
  children: ReactNode
}) {
  if (folded) {
    return (
      <details className="comp-fold">
        <summary className="comp-fold__summary">
          <h2 id={id} className="comp-fold__title">
            {title}
          </h2>
          <span className="comp-fold__mark" aria-hidden="true" />
        </summary>
        <div className="comp-fold__body">{children}</div>
      </details>
    )
  }
  return (
    <section className="comp-block" aria-labelledby={id}>
      <h2 id={id} className="comp-block__title">
        {title}
      </h2>
      <div className="comp-block__body">{children}</div>
    </section>
  )
}

type Placement = 'hero' | 'sticky' | 'final' | 'panel'

function CompetitionView({ competition: c, reload }: { competition: CompetitionDetail; reload: () => void }) {
  const navigate = useNavigate()
  const { pathname } = useLocation()
  const { platformName } = usePlatform()
  const { status: authStatus } = useAuth()
  const [params, setParams] = useSearchParams()
  const pickerRef = useRef<HTMLFieldSetElement>(null)
  const barRef = useRef<HTMLDivElement>(null)
  const heroCtaRef = useRef<HTMLDivElement>(null)
  const finalRef = useRef<HTMLElement>(null)
  const [pickError, setPickError] = useState<string>()
  // Hidden until the observer has had its say, so a page opened at the top never flashes a second button.
  const [barShown, setBarShown] = useState(false)
  const phone = useMediaQuery('(max-width: 767.98px)')

  // The vote's own summary, for a competition that has one: it carries the window and follows it, so a page left
  // open turns into the review stage at the end, and back into the competition page once the result is public.
  const hasVote = c.voting === 'Open' || c.voting === 'Closed'
  const vote = useLiveVoting(c.slug, hasVote)
  const voteAfter = vote.data ? afterVote(vote.data) : null
  const publishedMeanwhile = voteAfter === 'results' && c.resultsPublishedAt === null
  useEffect(() => {
    // The page was read before the result was published: read it again, so the result block below shows it.
    if (publishedMeanwhile) reload()
  }, [publishedMeanwhile, reload])
  const inReview = voteAfter === 'review'
  const audit = useVotingAudit(c.slug, inReview)

  // An ad's words apply only while there is something to act on; a closed competition speaks for itself.
  const campaign = c.isAcceptingSubmissions ? campaignFor(c.slug) : null
  // Somebody who arrived through the ad has no list of competitions behind them to go back to.
  const arrivedByAd = campaign !== null && pathname === campaign.path

  // Once per competition — StrictMode's second run in development finds the ref already set.
  const viewed = useRef<string>(null)
  useEffect(() => {
    if (viewed.current === c.slug) return
    viewed.current = c.slug
    track({ name: 'landing_viewed', slug: c.slug, via: pathname })
  }, [c.slug, pathname])

  // The fixed mobile bar reserves exactly its own height (incl. the iOS home-indicator inset) at the end
  // of the page, so no category card or footer link can sit hidden behind it.
  useEffect(() => {
    const bar = barRef.current
    if (!bar) return
    const html = document.documentElement
    const apply = () => html.style.setProperty('--action-bar-h', `${bar.offsetHeight}px`)
    apply()
    const observer = new ResizeObserver(apply)
    observer.observe(bar)
    return () => {
      observer.disconnect()
      html.style.removeProperty('--action-bar-h')
    }
  }, [])

  const label = c.categoryLabel ?? 'Kateqoriyalar'
  const needsCategory = c.hasCategories && c.categories.length > 0
  const categoriesPending = c.hasCategories && c.categories.length === 0
  const canEnter = c.isAcceptingSubmissions && !categoriesPending

  // The bar is the way in once the page's own buttons are out of sight — and only then: with the hero's button on
  // screen a second one at the bottom would only compete with it, and at the closing invitation it would repeat it.
  useEffect(() => {
    if (!canEnter) return
    const targets = [heroCtaRef.current, finalRef.current].filter((el): el is HTMLElement => el !== null)
    if (typeof IntersectionObserver === 'undefined' || targets.length === 0) {
      setBarShown(true)
      return
    }
    const inView = new Set<Element>()
    const observer = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) inView.add(entry.target)
        else inView.delete(entry.target)
      }
      setBarShown(inView.size === 0)
    })
    targets.forEach((t) => observer.observe(t))
    return () => observer.disconnect()
  }, [canEnter])

  const requested = params.get('category')
  const selected = c.categories.find((x) => x.slug === requested) ?? null

  const choose = (categorySlug: string) => {
    setPickError(undefined)
    const next = new URLSearchParams(params)
    next.set('category', categorySlug)
    // Selection lives in the URL: shareable, survives refresh and the login detour.
    setParams(next, { replace: true, preventScrollReset: true })
  }

  const participate = (placement: Placement) => {
    if (needsCategory && !selected) {
      setPickError(`İştirak üçün əvvəlcə ${label.toLocaleLowerCase('az')} siyahısından birini seçin.`)
      pickerRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })
      pickerRef.current?.focus({ preventScroll: true })
      return
    }
    const target = participatePath(c.slug, selected?.slug)
    saveIntent({
      competitionSlug: c.slug,
      competitionTitle: c.title,
      projectName: c.project.name,
      categorySlug: selected?.slug ?? null,
      categoryName: selected?.name ?? null,
      categoryLabel: c.categoryLabel,
    })
    track({ name: 'cta_clicked', slug: c.slug, placement })
    navigate(authStatus === 'authenticated' ? target : authPath('login', target))
  }

  const prizes = c.prize ? parsePrizes(c.prize) : null
  const fund = prizes ? prizeFund(prizes.places) : null
  const prizeSummary = prizes ? prizes.places.map((p) => p.award).join(' · ') : ''
  const left = c.isAcceptingSubmissions ? daysLeft(c.endDate) : null
  const cta = campaign?.cta ?? 'İştirak et'

  const dates = [
    c.startDate && { label: 'Qəbulun başlanması', value: formatDate(c.startDate) },
    c.endDate && { label: 'Son təqdim tarixi', value: formatDate(c.endDate) },
    c.resultsAnnouncedOn && { label: 'Nəticələrin elanı', value: formatDate(c.resultsAnnouncedOn) },
    c.exhibitionOn && { label: 'Sərgi', value: formatDate(c.exhibitionOn) },
  ].filter(Boolean) as { label: string; value: string }[]

  // Every message below follows real state from the server: status + dates (phase) and the result publication.
  const resultsOut = c.resultsPublishedAt !== null
  const finished = !c.isAcceptingSubmissions && c.phase !== 'Upcoming'
  const closedMessage =
    c.phase === 'Upcoming'
      ? 'Qəbul hələ başlamayıb. Başlayanda bu səhifədən təklif göndərə biləcəksiniz.'
      : resultsOut
        ? 'Qəbul başa çatıb, nəticələr açıqlanıb.'
        : 'Qəbul başa çatıb. Nəticələr hazırlanır.'

  return (
    <div className="comp" style={accentVars(c.project.accentColor)}>
      <div className="container">
        <Breadcrumbs
          className="comp__crumbs"
          items={[
            { label: platformName, to: '/' },
            { label: 'Müsabiqələr', to: '/competitions' },
            { label: c.title },
          ]}
        />
        {/* On phones the title follows immediately, so one step back is all the wayfinding needed. */}
        {!arrivedByAd && (
          <Link to="/competitions" className="comp__back">
            <span aria-hidden="true" className="comp__back-arrow" />
            Müsabiqələr
          </Link>
        )}

        {/*
          The words come first and the poster after them — on a phone that puts what it is, what can be won, until
          when and the way in on the first screen, with the poster rising into view under the button. On a wide
          screen the two stand side by side as before.
        */}
        <header className={`comp__head${c.coverImage ? ' comp__head--poster' : ''}${arrivedByAd ? ' comp__head--ad' : ''}`}>
          <div className="comp__intro">
            <div className="comp__tags">
              <PhaseStatus phase={c.phase} />
              {campaign ? (
                // The ad's headline replaces the title, so the competition's own name stays right above it.
                <span className="comp__kicker">{c.title}</span>
              ) : (
                <span className="comp__project">
                  <span className="comp__swatch" aria-hidden="true" />
                  {c.project.name}
                </span>
              )}
            </div>
            <h1 className="comp__title">{campaign?.headline ?? c.title}</h1>
            <p className="comp__lede">{campaign?.lede ?? c.shortDescription}</p>

            {(c.endDate || prizeSummary) && (
              <dl className="comp__facts">
                {(fund || prizeSummary) && (
                  <div className="comp__fact">
                    <dt>{fund ? 'Mükafat fondu' : 'Mükafatlar'}</dt>
                    <dd className="comp__fact-value tabular">{fund ? formatPrizeFund(fund) : prizeSummary}</dd>
                  </div>
                )}
                {c.endDate && (
                  <div className="comp__fact">
                    <dt>Son tarix</dt>
                    <dd className="comp__fact-value tabular">
                      {c.isAcceptingSubmissions
                        ? `${formatDayMonth(c.endDate)}, ${formatTime(c.endDate)}`
                        : formatDate(c.endDate)}
                    </dd>
                    {left !== null && left <= 30 && (
                      <dd className="comp__fact-note">{left === 0 ? 'Bu gün son gündür' : `${left} gün qalıb`}</dd>
                    )}
                  </div>
                )}
              </dl>
            )}

            {canEnter && (
              <div className="comp__head-cta" ref={heroCtaRef}>
                <button
                  type="button"
                  className="btn btn--primary btn--lg comp__cta"
                  onClick={() => participate('hero')}
                >
                  {cta}
                </button>
                {campaign && campaign.reassurance.length > 0 && (
                  <ul role="list" className="comp__reassure">
                    {campaign.reassurance.map((line) => (
                      <li key={line}>{line}</li>
                    ))}
                  </ul>
                )}
              </div>
            )}
          </div>

          {/* The poster is the competition's public face: shown whole, at a size worth looking at. */}
          {c.coverImage && (
            <div className="comp__poster">
              <img src={c.coverImage} alt="" fetchPriority="high" decoding="async" />
            </div>
          )}
        </header>

        {finished && !inReview && (
          <section className={`comp-state${resultsOut ? ' comp-state--results' : ''}`} aria-labelledby="comp-state-title">
            <div className="comp-state__text">
              <h2 id="comp-state-title" className="comp-state__title">
                {resultsOut ? 'Nəticələr açıqlanıb' : 'Qəbul başa çatıb'}
              </h2>
              <p>
                {resultsOut
                  ? `${c.hasCategories ? 'Qaliblər' : 'Qalib'} ${formatDate(c.resultsPublishedAt)} tarixində elan olunub. Müsabiqəyə göndərilmiş işlər qalereyada.`
                  : `Nəticələr hazırlanır. Açıqlananda ${c.hasCategories ? 'qaliblər' : 'qalib'} və işlər bu səhifədən görünəcək.`}
              </p>
            </div>
            {resultsOut && (
              <div className="comp-state__actions">
                <Link to={`/competitions/${c.slug}/results`} className="btn btn--primary">
                  Nəticələrə bax
                </Link>
                <Link to={`/competitions/${c.slug}/gallery`} className="btn">
                  İşlərə bax
                </Link>
              </div>
            )}
          </section>
        )}

        {/* The vote, while it is on. Above the prize because it is the only thing on this page with a deadline
            of its own, and the one thing a reader who is not entering can still do. */}
        {inReview ? (
          /* After the window and before the result: the vote's place on this page becomes the review status, with
             the real totals; the provisional ranking and the audit lines are one click away, on the ballot. */
          <div className="comp-review">
            <ReviewStatus audit={audit}>
              <Link to={`/competitions/${c.slug}/voting`} className="btn">
                İlkin səs sıralamasına bax
              </Link>
            </ReviewStatus>
          </div>
        ) : (c.voting === 'Open' || c.voting === 'Closed') && (
          <section className="comp-voting" aria-labelledby="comp-voting-title">
            <div className="comp-voting__text">
              <p className="comp-voting__eyebrow">Xalq seçimi</p>
              <h2 id="comp-voting-title" className="comp-voting__title">
                {c.voting === 'Open' ? 'Bu müsabiqədə səsvermə açıqdır' : 'Səsvermə başa çatıb'}
              </h2>
              <p>
                {c.voting === 'Open'
                  ? 'Göndərilmiş işlərə baxın və bəyəndiyiniz işə səs verin.'
                  : 'Səslər yekunlaşıb. Verilmiş səsləri və işləri hələ də görə bilərsiniz.'}
              </p>
            </div>
            <div className="comp-voting__actions">
              <Link to={`/competitions/${c.slug}/voting`} className={`btn${c.voting === 'Open' ? ' btn--primary' : ''}`}>
                {c.voting === 'Open' ? 'Səs ver' : 'Səsverməyə bax'}
              </Link>
            </div>
          </section>
        )}

        {/* What can be won comes straight after the hero: it is the second thing anybody wants to know. */}
        {c.prize && (
          <section className="comp-prizes" aria-labelledby="prize">
            <h2 id="prize" className="comp-prizes__title">
              Mükafatlar
            </h2>
            <Prizes text={c.prize} />
          </section>
        )}

        {canEnter && (
          <section className="comp-steps" aria-labelledby="comp-steps-title">
            <h2 id="comp-steps-title" className="comp-steps__title">
              Necə iştirak etməli?
            </h2>
            <ol role="list" className="comp-steps__list">
              {entrySteps.map((s, i) => (
                <li key={s.title} className="comp-steps__item">
                  <span className="comp-steps__n tabular" aria-hidden="true">
                    {i + 1}
                  </span>
                  <h3 className="comp-steps__name">{s.title}</h3>
                  <p className="comp-steps__text">{s.text}</p>
                </li>
              ))}
            </ol>
          </section>
        )}

        <div className="comp__layout">
          <div className="comp__content">
            {c.description && (
              <Block id="about" title="Müsabiqə haqqında">
                <Paragraphs text={c.description} />
              </Block>
            )}

            {c.hasCategories && (
              <Block id="categories" title={label}>
                {categoriesPending ? (
                  <p className="comp__pending">Siyahı hazırlanır. Dərc olunan kimi burada seçim edə biləcəksiniz.</p>
                ) : (
                  <>
                    <p className="comp__hint">
                      {c.isAcceptingSubmissions
                        ? 'Siyahıdan birini seçin. Seçiminiz təklif formasına avtomatik keçəcək.'
                        : resultsOut
                          ? 'Müsabiqədə iştirak edən siyahı. Qalereyada işləri bu siyahı üzrə süzə bilərsiniz.'
                          : 'Müsabiqədə iştirak edən siyahı.'}
                    </p>
                    <CategoryPicker
                      ref={pickerRef}
                      label={label}
                      categories={c.categories}
                      value={selected?.slug ?? null}
                      onChange={choose}
                      error={pickError}
                      disabled={!c.isAcceptingSubmissions}
                    />
                  </>
                )}
              </Block>
            )}

            {c.nominations && (
              <Block id="nominations" title="Nominasiyalar">
                <Nominations text={c.nominations} />
              </Block>
            )}

            {/* The long texts: open on a wide screen, folded on a phone. */}
            <div className="comp__details">
              {c.requirements && (
                <Block id="requirements" title="İştirak tələbləri" folded={phone}>
                  <Paragraphs text={c.requirements} />
                </Block>
              )}

              {c.rules && (
                <Block id="rules" title="Qaydalar" folded={phone}>
                  <Paragraphs text={c.rules} />
                </Block>
              )}

              {c.judgingCriteria && (
                <Block id="judging" title="Qiymətləndirmə meyarları" folded={phone}>
                  <Paragraphs text={c.judgingCriteria} />
                </Block>
              )}

              {c.usageTerms && (
                <Block id="usage" title="Müəllif hüquqları və istifadə" folded={phone}>
                  <Paragraphs text={c.usageTerms} />
                </Block>
              )}

              {dates.length > 0 && (
                <Block id="timeline" title="Təqvim" folded={phone}>
                  <ol role="list" className="timeline">
                    {dates.map((d) => (
                      <li key={d.label}>
                        <span className="timeline__label">{d.label}</span>
                        <span className="timeline__value tabular">{d.value}</span>
                      </li>
                    ))}
                  </ol>
                </Block>
              )}

              {c.contactNote && (
                <Block id="contact" title="Əlaqə" folded={phone}>
                  <Paragraphs text={c.contactNote} />
                </Block>
              )}
            </div>

            {canEnter && (
              <section className="comp-final" aria-labelledby="comp-final-title" ref={finalRef}>
                <h2 id="comp-final-title" className="comp-final__title">
                  İştirak etməyə hazırsınız?
                </h2>
                <p className="comp-final__text">
                  Nominasiyanı seçin, işinizi və izahını yükləyin. Göndərdikdən sonra istinad nömrəsi alırsınız.
                </p>
                <button type="button" className="btn btn--primary btn--lg" onClick={() => participate('final')}>
                  {cta}
                </button>
              </section>
            )}
          </div>

          <aside className="comp__panel" aria-label="İştirak">
            <div className="panel">
              {c.isAcceptingSubmissions ? (
                <>
                  {c.hasCategories && (
                    <div className="panel__choice" aria-live="polite">
                      <span className="panel__choice-label">Seçiminiz</span>
                      <span className={`panel__choice-value${selected ? '' : ' is-empty'}`}>
                        {selected ? selected.name : categoriesPending ? 'Siyahı hazırlanır' : 'Hələ seçilməyib'}
                      </span>
                    </div>
                  )}
                  <button
                    type="button"
                    className="btn btn--primary btn--lg btn--block"
                    onClick={() => participate('panel')}
                    disabled={categoriesPending}
                  >
                    {cta}
                  </button>
                  {authStatus !== 'authenticated' && (
                    <p className="panel__note">
                      Göndərmək üçün {platformName} hesabı lazımdır. Daxil olduqdan sonra seçiminiz saxlanılır.
                    </p>
                  )}
                </>
              ) : (
                <p className="panel__closed">{closedMessage}</p>
              )}

              {(c.endDate || c.maxSubmissionsPerUser) && (
                <dl className="panel__facts">
                  {c.endDate && (
                    <div>
                      <dt>Son tarix</dt>
                      <dd className="tabular">{formatDate(c.endDate)}</dd>
                    </div>
                  )}
                  {c.maxSubmissionsPerUser && (
                    <div>
                      <dt>Bir iştirakçıdan</dt>
                      <dd>ən çox {c.maxSubmissionsPerUser} təklif</dd>
                    </div>
                  )}
                </dl>
              )}
            </div>
          </aside>
        </div>
      </div>

      {canEnter && (
        <div
          className="action-bar"
          ref={barRef}
          role="region"
          aria-label="İştirak et"
          data-shown={barShown}
          // Out of sight means out of reach: a hidden bar must not take keyboard focus or be read out.
          inert={!barShown}
        >
          <div className="action-bar__choice">
            {c.hasCategories ? (
              <>
                <span className="action-bar__label">Seçiminiz</span>
                <span className={`action-bar__value${selected ? '' : ' is-empty'}`}>
                  {selected ? selected.name : 'Hələ seçilməyib'}
                </span>
              </>
            ) : fund ? (
              <>
                {/* The competition keeps its name here; what it offers goes under it. */}
                <span className="action-bar__label">{shortCompetitionTitle(c.title)}</span>
                <span className="action-bar__value">{formatPrizeFund(fund)} mükafat fondu</span>
              </>
            ) : (
              // Inside a competition the project name says nothing the reader needs; the competition does.
              <span className="action-bar__value">{shortCompetitionTitle(c.title)}</span>
            )}
          </div>
          {/* Looks inactive until a choice is made, but stays a working button that explains what is missing. */}
          <button
            type="button"
            className={`btn btn--primary${needsCategory && !selected ? ' btn--waiting' : ''}`}
            aria-describedby={needsCategory && !selected ? 'choose-first' : undefined}
            onClick={() => participate('sticky')}
          >
            İştirak et
          </button>
          {needsCategory && !selected && (
            <span id="choose-first" className="visually-hidden">
              Əvvəlcə {label.toLocaleLowerCase('az')} siyahısından birini seçin.
            </span>
          )}
        </div>
      )}
    </div>
  )
}
