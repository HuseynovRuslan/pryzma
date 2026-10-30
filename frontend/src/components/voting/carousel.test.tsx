import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { CompetitionSummary } from '../../api/types'
import { lifecycleOf } from '../../lib/competitionLifecycle'
import { MAX_FEATURED, pickFeatured, pickFeaturedList, type FeaturedSlide } from '../../lib/featuredCompetitionContext'
import { CompetitionStageCarousel } from './CompetitionStageCarousel'

const NOW = Date.parse('2026-03-05T12:00:00+04:00')
const DAY = 24 * 60 * 60 * 1000
const at = (offset: number) => new Date(NOW + offset).toISOString()

const competition = (over: Partial<CompetitionSummary> = {}): CompetitionSummary => ({
  id: 1,
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
  categoryLabel: null,
  categoryCount: 0,
  project: { slug: 'abseron', name: 'Abşeronun simvolları', accentColor: '#56652A' },
  resultsPublished: false,
  voting: 'NotConfigured',
  ...over,
})

const slideOf = (over: Partial<CompetitionSummary>): FeaturedSlide => {
  const c = competition(over)
  return { competition: c, voting: null, lifecycle: lifecycleOf(c, null, NOW) }
}

const THREE = [
  slideOf({ id: 1, slug: 'emircan', title: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi' }),
  slideOf({ id: 2, slug: 'susa', title: 'Şuşa Mədəni Kitabxanası Memarlıq müsabiqəsi' }),
  slideOf({ id: 3, slug: 'sebeke', title: 'Şəbəkə Motivləri Müasir İnteryerlərdə' }),
]

const show = (slides: FeaturedSlide[]) =>
  render(
    <MemoryRouter>
      <CompetitionStageCarousel slides={slides} voteHref="#finalists" />
    </MemoryRouter>,
  )

const heading = () => screen.getByRole('heading', { level: 1 }).textContent
const counter = () => document.querySelector('.stagecar__count')?.textContent?.replace(/\s+/g, ' ').trim()

afterEach(() => vi.useRealTimers())

describe('the stage carousel', () => {
  it('shows nothing at all without a competition', () => {
    const { container } = show([])
    expect(container).toBeEmptyDOMElement()
  })

  it('is a plain hero with one competition — no tabs, arrows, counter or dots', () => {
    show([THREE[0]])
    expect(screen.getByRole('heading', { level: 1, name: 'Əmircanın simvolu' })).toBeInTheDocument()
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Növbəti müsabiqə' })).not.toBeInTheDocument()
    expect(document.querySelector('.stagecar__dots')).toBeNull()
    expect(document.querySelector('.stagecar__count')).toBeNull()
  })

  it('carries one tab per competition and counts them', () => {
    show(THREE)
    const tabs = within(screen.getByRole('tablist')).getAllByRole('tab')
    expect(tabs.map((t) => t.textContent)).toEqual([
      '01Əmircanın simvolu',
      '02Şuşa Mədəni Kitabxanası Memarlıq müsabiqəsi',
      '03Şəbəkə Motivləri Müasir İnteryerlərdə',
    ])
    expect(counter()).toBe('01 / 03')
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true')
  })

  it('keeps one slide in the document, not three behind display:none', () => {
    show(THREE)
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
    expect(document.querySelectorAll('.stage')).toHaveLength(1)
  })

  it('moves when a tab is pressed', async () => {
    const user = userEvent.setup()
    show(THREE)
    await user.click(screen.getByRole('tab', { name: /Şuşa/ }))
    expect(heading()).toBe('Şuşa Mədəni Kitabxanası Memarlıq müsabiqəsi')
    expect(counter()).toBe('02 / 03')
    expect(screen.getByRole('tab', { name: /Şuşa/ })).toHaveAttribute('aria-selected', 'true')
  })

  it('steps with the arrows and wraps round at both ends', async () => {
    const user = userEvent.setup()
    show(THREE)
    const next = screen.getByRole('button', { name: 'Növbəti müsabiqə' })
    const prev = screen.getByRole('button', { name: 'Əvvəlki müsabiqə' })

    await user.click(next)
    expect(counter()).toBe('02 / 03')
    await user.click(next)
    await user.click(next)
    expect(counter()).toBe('01 / 03') // past the end, back to the first
    await user.click(prev)
    expect(counter()).toBe('03 / 03') // and before the first, round to the last
    expect(heading()).toBe('Şəbəkə Motivləri Müasir İnteryerlərdə')
  })

  it('answers the arrow keys', async () => {
    const user = userEvent.setup()
    show(THREE)
    await user.click(screen.getByRole('tab', { name: /Əmircan/ }))
    await user.keyboard('{ArrowRight}')
    expect(counter()).toBe('02 / 03')
    await user.keyboard('{ArrowLeft}')
    expect(counter()).toBe('01 / 03')
  })

  it('marks the dot of the slide being shown', async () => {
    const user = userEvent.setup()
    show(THREE)
    const dots = [...document.querySelectorAll('.stagecar__dot')]
    expect(dots).toHaveLength(3)
    expect(dots[0]).toHaveAttribute('aria-current', 'true')
    await user.click(dots[2] as HTMLElement)
    expect(counter()).toBe('03 / 03')
    expect(dots[2]).toHaveAttribute('aria-current', 'true')
    expect(dots[0]).not.toHaveAttribute('aria-current')
  })

  it('never moves on its own', () => {
    vi.useFakeTimers()
    show(THREE)
    expect(counter()).toBe('01 / 03')
    vi.advanceTimersByTime(5 * 60 * 1000)
    expect(counter()).toBe('01 / 03')
    expect(heading()).toBe('Əmircanın simvolu')
  })

  it('starts no clock of its own, however many competitions it carries', () => {
    vi.useFakeTimers()
    const before = vi.getTimerCount()
    const { unmount } = show(THREE)
    // The page's one clock lives in the provider; a slide out of sight must not leave an interval behind it.
    expect(vi.getTimerCount()).toBe(before)
    unmount()
    expect(vi.getTimerCount()).toBe(before)
  })

  it('does not strand the reader when a competition drops off the list', () => {
    const { rerender } = show(THREE)
    rerender(
      <MemoryRouter>
        <CompetitionStageCarousel slides={THREE.slice(0, 1)} voteHref="#finalists" />
      </MemoryRouter>,
    )
    expect(screen.getByRole('heading', { level: 1, name: 'Əmircanın simvolu' })).toBeInTheDocument()
  })
})

describe('which competitions are led with', () => {
  it('puts a live vote first, then one opening, then an open call, then review, then one just closed', () => {
    const list = [
      competition({ slug: 'closed', voting: 'Closed', phase: 'Closed' }),
      competition({ slug: 'review', voting: 'NotConfigured', phase: 'Closed' }),
      competition({ slug: 'open-call', voting: 'NotConfigured', phase: 'Open' }),
      competition({ slug: 'upcoming', voting: 'Upcoming' }),
      competition({ slug: 'live', voting: 'Open' }),
    ]
    expect(pickFeaturedList(list).slice(0, 4).map((c) => c.slug)).toEqual(['live', 'upcoming', 'open-call', 'review'])
    expect(pickFeaturedList(list.filter((c) => c.slug !== 'live')).map((c) => c.slug)).toEqual([
      'upcoming',
      'open-call',
      'review',
      'closed',
    ])
  })

  it('keeps a competition on the front page the morning after its deadline, while its works are checked', () => {
    // The day this was written: entries closed overnight, no voting window yet, the front page went blank.
    const list = [competition({ slug: 'emircan', voting: 'NotConfigured', phase: 'Closed', endDate: at(-DAY) })]
    expect(pickFeaturedList(list).map((c) => c.slug)).toEqual(['emircan'])
    const c = list[0]
    expect(lifecycleOf(c, null, NOW).stage).toBe('review')
  })

  it('leaves out a competition that has finished, in every band', () => {
    const finished = [
      competition({ slug: 'no-vote', voting: 'NotConfigured', phase: 'Closed', resultsPublished: true }),
      competition({ slug: 'after-vote', voting: 'Closed', phase: 'Closed', resultsPublished: true }),
    ]
    expect(pickFeaturedList(finished)).toEqual([])
  })

  it('leaves out a competition that has not opened yet', () => {
    const list = [competition({ slug: 'soon', voting: 'NotConfigured', phase: 'Upcoming', startDate: at(5 * DAY) })]
    expect(pickFeaturedList(list)).toEqual([])
  })

  it('calls a competition finished from the list alone, without the detail page', () => {
    // The list carries `resultsPublished`, not the date; the stage must not read it as a vote that closed.
    const c = competition({ voting: 'Closed', phase: 'Closed', endDate: at(-10 * DAY), resultsPublished: true })
    expect(lifecycleOf(c, null, NOW).stage).toBe('results')
  })

  it('names each competition once, however many bands it answers', () => {
    const list = [competition({ slug: 'both', voting: 'Open', phase: 'Open' })]
    expect(pickFeaturedList(list).map((c) => c.slug)).toEqual(['both'])
  })

  it('stops at four — a number a reader will actually step through', () => {
    expect(MAX_FEATURED).toBe(4)
    const many = Array.from({ length: 9 }, (_, i) => competition({ slug: `c${i}`, phase: 'Open' }))
    const led = pickFeaturedList(many)
    expect(led).toHaveLength(4)
    expect(led.map((c) => c.slug)).toEqual(['c0', 'c1', 'c2', 'c3'])
  })

  it('hands the navbar badge the same first competition the hero shows', () => {
    const list = [
      competition({ slug: 'open-call', voting: 'NotConfigured', phase: 'Open' }),
      competition({ slug: 'live', voting: 'Open' }),
      competition({ slug: 'upcoming', voting: 'Upcoming' }),
    ]
    // One ordering, read twice: the badge cannot name a competition the hero is not showing.
    expect(pickFeatured(list)).toBe(pickFeaturedList(list)[0])
    expect(pickFeatured(list)?.slug).toBe('live')
    expect(pickFeatured([])).toBeNull()
  })
})
