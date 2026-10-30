import { act, render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { CompetitionSummary, Voting, VotingState } from '../api/types'

const api = vi.hoisted(() => ({
  competitions: vi.fn(),
  summary: vi.fn(),
}))

vi.mock('../api/endpoints', () => ({
  catalog: { competitions: api.competitions },
  votingApi: { summary: api.summary },
}))

import { FeaturedCompetitionProvider } from './featuredCompetition'
import { useFeaturedCompetition } from './featuredCompetitionContext'

const NOW = Date.parse('2026-03-05T12:00:00+04:00')
const at = (offsetMs: number) => new Date(NOW + offsetMs).toISOString()

const competition = (voting: VotingState): CompetitionSummary => ({
  id: 7,
  slug: 'emircan',
  title: 'Əmircanın simvolu',
  shortDescription: 'Açıq müsabiqə',
  coverImage: null,
  status: 'Published',
  phase: 'Closed',
  startDate: at(-10_000),
  endDate: at(-5_000),
  hasPrize: false,
  hasCategories: false,
  categoryLabel: null,
  categoryCount: 0,
  project: { slug: 'abseron', name: 'Abşeron', accentColor: null },
  resultsPublished: false,
  voting,
})

const voting = (state: VotingState, startsAt: string, endsAt: string): Voting => ({
  competition: {
    slug: 'emircan',
    title: 'Əmircanın simvolu',
    shortDescription: 'Açıq müsabiqə',
    categoryLabel: null,
    hasCategories: false,
    project: { slug: 'abseron', name: 'Abşeron', accentColor: null },
  },
  state,
  startsAt,
  endsAt,
  nominations: [],
  workCount: 0,
  voteCount: 0,
  viewer: null,
  voterCount: 0,
  lastVoteAt: null,
})

function Probe({ children }: { children?: ReactNode }) {
  const featured = useFeaturedCompetition()
  return (
    <div>
      <span data-testid="stage">{featured.lifecycle?.stage ?? 'none'}</span>
      {children}
    </div>
  )
}

async function flush() {
  await act(async () => {
    await Promise.resolve()
    await Promise.resolve()
    await Promise.resolve()
  })
}

describe('FeaturedCompetitionProvider boundary refresh', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(NOW)
    api.competitions.mockReset()
    api.summary.mockReset()
  })

  afterEach(() => vi.useRealTimers())

  it('moves Upcoming to Live at zero using the refreshed server state, without a page refresh', async () => {
    api.competitions.mockResolvedValue([competition('Upcoming')])
    api.summary
      .mockResolvedValueOnce(voting('Upcoming', at(1_000), at(60_000)))
      .mockResolvedValueOnce(voting('Open', at(1_000), at(60_000)))

    render(
      <FeaturedCompetitionProvider>
        <Probe />
      </FeaturedCompetitionProvider>,
    )
    await flush()
    expect(screen.getByTestId('stage')).toHaveTextContent('votingUpcoming')

    await act(async () => {
      vi.advanceTimersByTime(1_000)
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(api.summary).toHaveBeenCalledTimes(2)
    expect(screen.getByTestId('stage')).toHaveTextContent('votingLive')
  })

  it('moves Live to Closed at zero using the refreshed server state', async () => {
    api.competitions.mockResolvedValue([competition('Open')])
    api.summary
      .mockResolvedValueOnce(voting('Open', at(-60_000), at(1_000)))
      .mockResolvedValueOnce(voting('Closed', at(-60_000), at(1_000)))

    render(
      <FeaturedCompetitionProvider>
        <Probe />
      </FeaturedCompetitionProvider>,
    )
    await flush()
    expect(screen.getByTestId('stage')).toHaveTextContent('votingLive')

    await act(async () => {
      vi.advanceTimersByTime(1_000)
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(api.summary).toHaveBeenCalledTimes(2)
    expect(screen.getByTestId('stage')).toHaveTextContent('votingClosed')
  })

  it('does not rerender a heavy non-consumer subtree on one-second clock ticks', async () => {
    api.competitions.mockResolvedValue([competition('NotConfigured')])
    let heavyRenders = 0
    function HeavyGridProbe() {
      heavyRenders += 1
      return <div data-testid="heavy-grid" />
    }

    render(
      <FeaturedCompetitionProvider>
        <Probe />
        <HeavyGridProbe />
      </FeaturedCompetitionProvider>,
    )
    await flush()
    expect(screen.getByTestId('heavy-grid')).toBeInTheDocument()
    expect(heavyRenders).toBe(1)

    act(() => vi.advanceTimersByTime(3_000))
    expect(heavyRenders).toBe(1)
  })
})

describe('FeaturedCompetitionProvider published results', () => {
  beforeEach(() => {
    api.competitions.mockReset()
    api.summary.mockReset()
  })

  function Published() {
    const featured = useFeaturedCompetition()
    return (
      <>
        <span data-testid="published">{featured.ready ? featured.published.join(',') || 'none' : 'waiting'}</span>
        <span data-testid="leading">{featured.slides.map((s) => s.competition.slug).join(',')}</span>
      </>
    )
  }

  it('names the competitions whose results are public, read off the same list, and leads with none of them', async () => {
    api.competitions.mockResolvedValue([
      { ...competition('Closed'), slug: 'done', resultsPublished: true },
      { ...competition('NotConfigured'), id: 8, slug: 'open-call', phase: 'Open' },
    ])
    render(
      <FeaturedCompetitionProvider>
        <Published />
      </FeaturedCompetitionProvider>,
    )
    expect(await screen.findByText('done')).toBeInTheDocument()
    expect(screen.getByTestId('leading')).toHaveTextContent(/^open-call$/)
    expect(api.competitions).toHaveBeenCalledTimes(1)
    expect(api.summary).not.toHaveBeenCalled()
  })
})
