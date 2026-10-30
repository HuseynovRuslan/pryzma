import { act, render, renderHook, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { CompetitionSummary, Voting } from '../../api/types'
import { lifecycleOf, remainingUntil, shortRemaining, useCompetitionLifecycle } from '../../lib/competitionLifecycle'
import { FeaturedContext, pickFeatured } from '../../lib/featuredCompetitionContext'
import { CompetitionStageHero } from './CompetitionStageHero'
import { VotingStatusBadge } from './VotingStatusBadge'

const NOW = Date.parse('2026-03-05T12:00:00+04:00')
const at = (offsetMs: number) => new Date(NOW + offsetMs).toISOString()
const DAY = 24 * 60 * 60 * 1000
const HOUR = 60 * 60 * 1000

const competition = (over: Partial<CompetitionSummary> = {}): CompetitionSummary => ({
  id: 7,
  slug: 'emircan',
  title: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi',
  shortDescription: 'Əmircanın tarixi irsini müasir dillə əks etdirən açıq müsabiqə.',
  coverImage: null,
  status: 'Published',
  phase: 'Open',
  startDate: at(-30 * DAY),
  endDate: at(10 * DAY),
  hasPrize: true,
  hasCategories: false,
  categoryLabel: 'Nominasiyalar',
  categoryCount: 3,
  project: { slug: 'abseron', name: 'Abşeronun simvolları', accentColor: '#56652A' },
  resultsPublished: false,
  voting: 'NotConfigured',
  ...over,
})

const voting = (over: Partial<Voting> = {}): Voting => ({
  competition: {
    slug: 'emircan',
    title: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi',
    shortDescription: 'x',
    categoryLabel: 'Nominasiyalar',
    hasCategories: false,
    project: { slug: 'abseron', name: 'Abşeronun simvolları', accentColor: '#56652A' },
  },
  state: 'Open',
  startsAt: at(-2 * DAY),
  endsAt: at(2 * DAY + 23 * HOUR + 59 * 60 * 1000),
  nominations: [],
  workCount: 62,
  voteCount: 0,
  viewer: null,
  voterCount: 0,
  lastVoteAt: null,
  ...over,
})

const show = (c: CompetitionSummary, v: Voting | null) => {
  const lifecycle = lifecycleOf(c, v, NOW)
  render(
    <MemoryRouter>
      <CompetitionStageHero competition={c} voting={v} lifecycle={lifecycle} />
    </MemoryRouter>,
  )
  return lifecycle
}

describe('the competition lifecycle', () => {
  it('is taking entries while the deadline is ahead', () => {
    const l = show(competition(), null)
    expect(l.stage).toBe('submissions')
    expect(l.step).toBe(0)
    expect(screen.getByText(/İşlərin qəbulu davam edir/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'İşini təqdim et' })).toHaveAttribute('href', '/competitions/emircan/submit')
    expect(screen.getByText('QƏBULUN BİTMƏSİNƏ')).toBeInTheDocument()
  })

  it('is in review once entries have closed and no window is set', () => {
    const l = show(competition({ endDate: at(-DAY), phase: 'Closed', isAcceptingSubmissions: false } as Partial<CompetitionSummary>), null)
    expect(l.stage).toBe('review')
    expect(l.step).toBe(1)
    expect(screen.getByText(/qəbulu başa çatdı\. Təqdim olunan işlər yoxlanılır/)).toBeInTheDocument()
    // Nothing to count towards until a voting date exists — and nothing invented in its place.
    expect(l.countdown).toBeNull()
    expect(screen.queryByText('GÜN')).not.toBeInTheDocument()
  })

  it('fills the panel during review with what is known, not with an empty box', () => {
    const closed = competition({ endDate: '2026-03-03T19:59:00+00:00', phase: 'Closed' }) // before NOW
    show(closed, null)
    // The panel says when entries closed — in Baku time, as written on the competition page.
    expect(screen.getByText('Qəbul bağlandı')).toBeInTheDocument()
    expect(screen.getByText('3 mart 2026')).toBeInTheDocument()
    expect(screen.getByText('saat 23:59')).toBeInTheDocument()
    // And that the next date will appear here — without naming one or promising which stage it is.
    expect(screen.getByText(/növbəti mərhələnin tarixi burada elan olunacaq/)).toBeInTheDocument()
    expect(document.querySelectorAll('.stage__tile')).toHaveLength(0)
    expect(screen.getByRole('link', { name: 'Müsabiqəyə bax' })).toBeInTheDocument()
  })

  it('counts towards the opening once a window is set, while still in review', () => {
    const l = show(
      competition({ endDate: at(-DAY), voting: 'Upcoming' }),
      voting({ state: 'Upcoming', startsAt: at(3 * DAY), endsAt: at(10 * DAY) }),
    )
    expect(l.stage).toBe('votingUpcoming')
    expect(screen.getByText('SƏSVERMƏNİN BAŞLAMASINA')).toBeInTheDocument()
    expect(screen.getByText(/Səsvermə tezliklə başlayır/)).toBeInTheDocument()
    expect(l.badge?.text).toBe('Səsvermə tezliklə')
  })

  it('is live during the window, and counts down to its end', () => {
    const l = show(competition({ voting: 'Open' }), voting())
    expect(l.stage).toBe('votingLive')
    expect(l.step).toBe(2)
    expect(screen.getByRole('heading', { name: 'Əmircanın simvolu' })).toBeInTheDocument()
    expect(screen.getByText('SƏSVERMƏNİN BİTMƏSİNƏ QALAN VAXT')).toBeInTheDocument()
    const tiles = screen.getAllByText(/^\d\d$/).map((e) => e.textContent)
    expect(tiles).toEqual(['02', '23', '59', '00'])
    expect(screen.getByText('SANİYƏ')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'İndi səs ver' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'İşlərə bax' })).toHaveAttribute('href', '/competitions/emircan/voting')
  })

  it('asks for one work in the whole competition, and on the home page leads straight down to the works', () => {
    const c = competition({ voting: 'Open' })
    const v = voting()
    render(
      <MemoryRouter>
        <CompetitionStageHero competition={c} voting={v} lifecycle={lifecycleOf(c, v, NOW)} voteHref="#finalists" />
        <section id="finalists">İşlər</section>
      </MemoryRouter>,
    )
    expect(screen.getByText('Müsabiqə işlərini nəzərdən keçirin və bəyəndiyiniz bir işi seçib səs verin.')).toBeInTheDocument()
    expect(document.body.textContent).not.toMatch(/hər nominasiyada/i)

    const jump = screen.getByRole('link', { name: 'İşlərə keç ↓' })
    expect(jump).toHaveAttribute('href', '#finalists')
    const scrolled = vi.fn()
    document.getElementById('finalists')!.scrollIntoView = scrolled
    jump.click()
    expect(scrolled).toHaveBeenCalledWith(expect.objectContaining({ block: 'start' }))
  })

  it('has no jump where the works are on another page', () => {
    show(competition({ voting: 'Open' }), voting())
    expect(screen.queryByRole('link', { name: 'İşlərə keç ↓' })).not.toBeInTheDocument()
  })

  it('marks the last day, and only the last day', () => {
    const twoDays = lifecycleOf(competition({ voting: 'Open' }), voting(), NOW)
    expect(twoDays.isFinalDay).toBe(false)
    expect(twoDays.badge?.text).toBe('● Səsvermə • 2 gün qalıb')

    const l = show(competition({ voting: 'Open' }), voting({ endsAt: at(5 * HOUR) }))
    expect(l.isFinalDay).toBe(true)
    expect(l.badge?.text).toBe('● Son 24 saat')
    expect(screen.getByText('● Son 24 saat')).toBeInTheDocument()
  })

  it('is only the summary once the window has shut, and offers no vote', () => {
    const l = show(competition({ voting: 'Closed' }), voting({ state: 'Closed', endsAt: at(-HOUR) }))
    expect(l.stage).toBe('votingClosed')
    expect(screen.getByText('Səsvermə mərhələsi başa çatdı')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Əmircanın simvolu' })).toBeInTheDocument()
    expect(screen.getByText(/Əmircanın tarixi irsini/)).toBeInTheDocument()
    expect(screen.getByText('Əsas tarixlər:')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'İndi səs ver' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'İşlərə bax' })).toHaveAttribute('href', '/competitions/emircan/voting')
    expect(l.badge).toBeNull()
    // The rail shows the road to the result: the vote behind, the review under way, the result next.
    expect(l.steps.map((s) => [s.label, s.state, s.note])).toEqual([
      ['Qəbul', 'done', 'Tamamlandı'],
      ['Səsvermə', 'done', 'Tamamlandı'],
      ['Səslərin yoxlanması', 'current', 'Hazırda bu mərhələdəyik'],
      ['Yekun nəticələr', 'upcoming', 'Elan olunacaq'],
    ])
    // What is under way is the status section's to say, once, below the hero: no panel here, nothing repeated.
    expect(document.querySelector('.stage__panel')).toBeNull()
    expect(document.querySelector('.stage__grid')!.children).toHaveLength(1)
    const text = document.body.textContent ?? ''
    for (const repeated of ['yoxlanılır', 'dəyərləndir', 'münsif', 'Yekun nəticələr səslərin', 'İLKİN NƏTİCƏLƏR', 'Növbəti addım']) {
      expect(text).not.toContain(repeated)
    }
    expect(screen.queryByRole('timer')).not.toBeInTheDocument()
  })

  it('runs down to zero and stops there', () => {
    const almost = remainingUntil(at(1500), NOW)!
    expect([almost.days, almost.hours, almost.minutes, almost.seconds]).toEqual([0, 0, 0, 1])

    const passed = remainingUntil(at(-5 * HOUR), NOW)!
    expect(passed.ms).toBe(0)
    expect([passed.days, passed.hours, passed.minutes, passed.seconds]).toEqual([0, 0, 0, 0])
    expect(shortRemaining(passed)).toBe('bitdi')

    // A window whose end has gone by, by the server's clock, is over even while the last answer still says Open:
    // the closed stage with no countdown at all — never 00/00/00 left standing, never negatives.
    const l = show(competition({ voting: 'Open' }), voting({ endsAt: at(-HOUR) }))
    expect(l.stage).toBe('votingClosed')
    expect(screen.queryAllByText('00')).toHaveLength(0)
    expect(screen.queryByRole('link', { name: 'İndi səs ver' })).not.toBeInTheDocument()
  })

  it('never names a participant, whatever the stage', () => {
    for (const [c, v] of [
      [competition(), null],
      [competition({ voting: 'Upcoming' }), voting({ state: 'Upcoming', startsAt: at(DAY) })],
      [competition({ voting: 'Open' }), voting()],
      [competition({ voting: 'Closed' }), voting({ state: 'Closed' })],
    ] as [CompetitionSummary, Voting | null][]) {
      const { container, unmount } = render(
        <MemoryRouter>
          <CompetitionStageHero competition={c} voting={v} lifecycle={lifecycleOf(c, v, NOW)} />
        </MemoryRouter>,
      )
      const text = container.textContent ?? ''
      for (const forbidden of ['Müəllif:', 'İştirakçı:', '@', 'Nigar', 'studio', 'Studio']) {
        expect(text).not.toContain(forbidden)
      }
      unmount()
    }
  })

  it('says what is kept private while people are voting, and claims nothing more', () => {
    show(competition({ voting: 'Open' }), voting())
    const note = screen.getByText(/Səsvermə müddətində işlərin müəllif məlumatları gizli saxlanılır/)
    expect(note).toBeInTheDocument()
    // The organiser publishes each work's running total, so the note says so — and never that it is hidden.
    expect(note.textContent).toContain('Hər işin səs sayı canlı göstərilir')
    expect(note.textContent).not.toContain('göstərilmir')
    // Never a claim the system cannot keep: a vote is stored against the account that cast it.
    expect(screen.queryByText(/səslər anonim/i)).not.toBeInTheDocument()
  })

  it('shows only the dates the competition actually has', () => {
    show(competition({ voting: 'Open' }), voting())
    const dates = screen.getByText('Əsas tarixlər:').closest('p')!
    expect(within(dates).getByText(/Qəbul \(/)).toBeInTheDocument()
    expect(within(dates).getByText(/Səsvermə \(/)).toBeInTheDocument()
    // No results date is set on this competition, so none is printed.
    expect(dates.textContent).not.toContain('Nəticələr (')
  })

  it('leads with the competition that has something to do today', () => {
    const upcoming = competition({ slug: 'a', voting: 'Upcoming' })
    const live = competition({ slug: 'b', voting: 'Open' })
    const accepting = competition({ slug: 'c', voting: 'NotConfigured', phase: 'Open' })
    expect(pickFeatured([upcoming, live, accepting])?.slug).toBe('b')
    expect(pickFeatured([accepting, upcoming])?.slug).toBe('a')
    expect(pickFeatured([accepting])?.slug).toBe('c')
    expect(pickFeatured([])).toBeNull()
  })

  it('stacks into one column on a phone', () => {
    // The grid is declared by CSS, so what is testable here is that nothing forces a second column into the
    // markup: the hero is one element with two children, and both are block-level regions.
    show(competition({ voting: 'Open' }), voting())
    const grid = document.querySelector('.stage__grid')!
    expect(grid.children).toHaveLength(2)
    expect(document.querySelectorAll('.stage__tile')).toHaveLength(4)
    expect(document.querySelector('.stage__rail')!.children).toHaveLength(4)
  })

  it('keeps the final-day badge inside the exact 24-hour boundary', () => {
    const atBoundary = lifecycleOf(
      competition({ voting: 'Open' }),
      voting({ endsAt: at(DAY) }),
      NOW,
    )
    const outside = lifecycleOf(
      competition({ voting: 'Open' }),
      voting({ endsAt: at(DAY + 1) }),
      NOW,
    )
    const finished = lifecycleOf(
      competition({ voting: 'Open' }),
      voting({ endsAt: at(0) }),
      NOW,
    )

    expect(atBoundary.isFinalDay).toBe(true)
    expect(outside.isFinalDay).toBe(false)
    expect(finished.isFinalDay).toBe(false)
  })

  it('keeps numeric seconds out of the compact navbar badge', () => {
    const c = competition({ voting: 'Open' })
    const v = voting({ endsAt: at(2 * DAY + 42_000) })
    const lifecycle = lifecycleOf(c, v, NOW)
    render(
      <MemoryRouter>
        <FeaturedContext.Provider
          value={{ competition: c, voting: v, lifecycle, slides: [{ competition: c, voting: v, lifecycle }], ready: true, published: [] }}
        >
          <VotingStatusBadge />
        </FeaturedContext.Provider>
      </MemoryRouter>,
    )

    const badge = screen.getByRole('link', { name: /Səsvermə/ })
    expect(badge).toHaveTextContent('2 gün qalıb')
    expect(badge).not.toHaveTextContent('42')
    expect(badge).not.toHaveTextContent('SANİYƏ')
  })
})

describe('the ticking lifecycle hook', () => {
  afterEach(() => vi.useRealTimers())

  it('updates seconds from target minus Date.now, corrects a background-time jump, and cleans up', () => {
    vi.useFakeTimers()
    vi.setSystemTime(NOW)
    const c = competition({ endDate: at(5 * 60_000 + 5_000) })
    const { result, unmount } = renderHook(() => useCompetitionLifecycle(c, null))

    expect(result.current?.countdown?.remaining).toMatchObject({ minutes: 5, seconds: 5 })
    act(() => vi.advanceTimersByTime(1_000))
    expect(result.current?.countdown?.remaining).toMatchObject({ minutes: 5, seconds: 4 })

    // Simulate returning after two minutes in a background tab; the next tick recomputes from the real clock.
    act(() => {
      vi.setSystemTime(NOW + 2 * 60_000)
      vi.advanceTimersByTime(1_000)
    })
    expect(result.current?.countdown?.remaining).toMatchObject({ minutes: 3, seconds: 4 })

    expect(vi.getTimerCount()).toBe(1)
    unmount()
    expect(vi.getTimerCount()).toBe(0)
  })
})
