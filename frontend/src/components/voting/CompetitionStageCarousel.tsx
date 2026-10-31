import { useCallback, useEffect, useRef, useState } from 'react'
import type { FeaturedSlide } from '../../lib/featuredCompetitionContext'
import { shortCompetitionTitle } from '../../lib/format'
import { CompetitionStageHero } from './CompetitionStageHero'
import './stage.css'
import './carousel.css'

/**
 * The front of the home page when more than one competition is worth leading with: the same stage hero, with a
 * way to step between them.
 *
 * It does not move on its own. The reference design's own script has no timer either, and the panel it carries
 * is a live countdown beside two calls to action — a hero that changes under a reader who is reading it takes
 * the page out of their hands. Arrows, the tabs, the dots, the arrow keys and a swipe all move it; nothing else
 * does.
 *
 * With a single competition there is nothing to step between, so the chrome is not rendered at all rather than
 * shown disabled: a lone "01 / 01" beside two dead arrows is furniture, not information.
 */
export function CompetitionStageCarousel({
  slides,
  voteHref: voteHrefFor,
}: {
  slides: FeaturedSlide[]
  /** Where "İndi səs ver" goes: one address, or each competition's own (its works on the home page). */
  voteHref?: string | ((slug: string) => string)
}) {
  const [at, setAt] = useState(0)
  const region = useRef<HTMLDivElement>(null)
  const strip = useRef<HTMLDivElement>(null)
  const touchX = useRef<number | null>(null)
  const total = slides.length

  // A competition can end while the page is open, which shortens the list under the reader.
  const current = Math.min(at, Math.max(total - 1, 0))
  useEffect(() => {
    if (at !== current) setAt(current)
  }, [at, current])

  /*
   * On a phone the tabs are a strip that scrolls sideways, so the one just chosen can sit outside it — press
   * the arrow and the bar appears not to have answered. Bring it into the middle of the strip instead. Only
   * the strip scrolls, never the page: a hero that jumps the reader's position is worse than a hidden tab.
   */
  useEffect(() => {
    const bar = strip.current
    const tab = bar?.children[current] as HTMLElement | undefined
    if (!bar || !tab || typeof bar.scrollTo !== 'function') return
    if (bar.scrollWidth <= bar.clientWidth) return
    const still = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
    bar.scrollTo({
      left: Math.max(0, tab.offsetLeft - (bar.clientWidth - tab.clientWidth) / 2),
      behavior: still ? 'auto' : 'smooth',
    })
  }, [current])

  const go = useCallback((next: number) => setAt(((next % total) + total) % total), [total])

  if (total === 0) return null
  const slide = slides[current]
  const voteHref = typeof voteHrefFor === 'function' ? voteHrefFor(slide.competition.slug) : voteHrefFor

  if (total === 1) {
    return (
      <CompetitionStageHero
        competition={slide.competition}
        voting={slide.voting}
        lifecycle={slide.lifecycle}
        headingLevel="h1"
        voteHref={voteHref}
      />
    )
  }

  const pad = (n: number) => String(n).padStart(2, '0')

  return (
    <section
      className="stagecar"
      aria-roledescription="karusel"
      aria-label="Aparıcı müsabiqələr"
      ref={region}
      onKeyDown={(e) => {
        if (e.key === 'ArrowRight') {
          e.preventDefault()
          go(current + 1)
        } else if (e.key === 'ArrowLeft') {
          e.preventDefault()
          go(current - 1)
        }
      }}
      onTouchStart={(e) => {
        touchX.current = e.touches[0]?.clientX ?? null
      }}
      onTouchEnd={(e) => {
        const from = touchX.current
        const to = e.changedTouches[0]?.clientX
        touchX.current = null
        if (from === null || to === undefined) return
        const travelled = to - from
        if (Math.abs(travelled) > 56) go(current + (travelled < 0 ? 1 : -1))
      }}
    >
      <div className="stagecar__bar">
        <div className="stagecar__tabs" role="tablist" aria-label="Müsabiqəni seç" ref={strip}>
          {slides.map((s, i) => (
            <button
              key={s.competition.slug}
              type="button"
              role="tab"
              id={`stagecar-tab-${i}`}
              aria-selected={i === current}
              aria-controls="stagecar-panel"
              tabIndex={i === current ? 0 : -1}
              className={`stagecar__tab${i === current ? ' is-current' : ''}`}
              onClick={() => go(i)}
            >
              <span className="stagecar__tab-dot" aria-hidden="true" />
              <span className="stagecar__tab-n tabular" aria-hidden="true">
                {pad(i + 1)}
              </span>
              <span className="stagecar__tab-name">{shortCompetitionTitle(s.competition.title)}</span>
            </button>
          ))}
        </div>

        <div className="stagecar__controls">
          <span className="stagecar__count tabular" aria-hidden="true">
            {pad(current + 1)} / {pad(total)}
          </span>
          <div className="stagecar__arrows">
            <button type="button" className="stagecar__arrow" aria-label="Əvvəlki müsabiqə" onClick={() => go(current - 1)}>
              <Chevron direction="left" />
            </button>
            <button type="button" className="stagecar__arrow" aria-label="Növbəti müsabiqə" onClick={() => go(current + 1)}>
              <Chevron direction="right" />
            </button>
          </div>
        </div>
      </div>

      {/* One slide is in the document at a time: the hero carries a running clock and two links, and nine of
          those behind `display: none` would still tick and still be reachable by keyboard. */}
      <div
        className="stagecar__panel"
        id="stagecar-panel"
        role="tabpanel"
        aria-labelledby={`stagecar-tab-${current}`}
        aria-roledescription="slayd"
        aria-label={`${pad(current + 1)} / ${pad(total)}: ${slide.competition.title}`}
      >
        <CompetitionStageHero
          key={slide.competition.slug}
          competition={slide.competition}
          voting={slide.voting}
          lifecycle={slide.lifecycle}
          headingLevel="h1"
          voteHref={voteHref}
          bare
        />
      </div>

      <p className="stagecar__dots">
        {slides.map((s, i) => (
          <button
            key={s.competition.slug}
            type="button"
            className={`stagecar__dot${i === current ? ' is-current' : ''}`}
            aria-label={`Slayd ${i + 1}: ${shortCompetitionTitle(s.competition.title)}`}
            aria-current={i === current ? 'true' : undefined}
            onClick={() => go(i)}
          />
        ))}
      </p>
    </section>
  )
}

function Chevron({ direction }: { direction: 'left' | 'right' }) {
  return (
    <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" aria-hidden="true">
      <path
        d={direction === 'left' ? 'M15 19l-7-7 7-7' : 'M9 5l7 7-7 7'}
        strokeWidth="2.5"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  )
}
