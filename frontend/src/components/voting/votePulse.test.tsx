import { act, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Voting } from '../../api/types'
import { lastVoteAgo, PULSE_FROM, shareMessage } from '../../pages/voting/lib'

const api = vi.hoisted(() => ({ summary: vi.fn() }))
vi.mock('../../api/endpoints', () => ({ votingApi: { summary: api.summary } }))

const { VotePulse } = await import('./VotePulse')

const NOW = Date.parse('2026-10-05T12:00:00+04:00')
const minutesAgo = (m: number) => new Date(NOW - m * 60_000).toISOString()

const voting = (over: Partial<Voting> = {}): Voting => ({
  competition: {
    slug: 'emircan',
    title: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi',
    shortDescription: 'x',
    categoryLabel: null,
    hasCategories: false,
    project: { slug: 'abseron', name: 'Abşeronun simvolları', accentColor: null },
  },
  state: 'Open',
  startsAt: '2026-10-04T07:40:00Z',
  endsAt: '2026-10-07T07:40:00Z',
  nominations: [{ slug: 'umumi', name: 'Ümumi', workCount: 99, voteCount: 40 }],
  workCount: 99,
  voteCount: 40,
  viewer: null,
  voterCount: 37,
  lastVoteAt: minutesAgo(4),
  ...over,
})

beforeEach(() => {
  vi.useFakeTimers({ now: NOW, shouldAdvanceTime: true })
  Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true })
})
afterEach(() => {
  vi.useRealTimers()
  vi.clearAllMocks()
})

describe('the live line', () => {
  it('says how many have voted and how lately, naming nobody', () => {
    const { container } = render(<VotePulse voting={voting()} slug="emircan" />)
    expect(container.querySelector('.vpulse')).toHaveTextContent('37 nəfər artıq səs verib. Sonuncu səs 4 dəqiqə əvvəl verildi.')
    expect(container.querySelector('.vpulse__dot')).toHaveClass('is-fresh')
  })

  it('stays quiet below the threshold, and once the window has shut', () => {
    const { container, rerender } = render(<VotePulse voting={voting({ voterCount: PULSE_FROM - 1 })} slug="emircan" />)
    expect(container).toBeEmptyDOMElement()
    rerender(<VotePulse voting={voting({ state: 'Closed' })} slug="emircan" />)
    expect(container).toBeEmptyDOMElement()
  })

  it('does not call an old vote news: no "last vote" line, and the dot stops breathing', () => {
    const { container } = render(<VotePulse voting={voting({ lastVoteAt: minutesAgo(120) })} slug="emircan" />)
    expect(container.querySelector('.vpulse')).toHaveTextContent('37 nəfər artıq səs verib.')
    expect(screen.queryByText(/Sonuncu səs/)).not.toBeInTheDocument()
    expect(container.querySelector('.vpulse__dot')).not.toHaveClass('is-fresh')
  })

  it('asks again every minute, so the line moves while someone reads', async () => {
    // A vote that came in just before the refresh.
    api.summary.mockResolvedValue(voting({ voterCount: 41, lastVoteAt: new Date(NOW + 50_000).toISOString() }))
    render(<VotePulse voting={voting()} slug="emircan" />)
    await act(() => vi.advanceTimersByTimeAsync(60_000))
    expect(api.summary).toHaveBeenCalledWith('emircan')
    expect(await screen.findByText('41')).toBeInTheDocument()
    expect(screen.getByText(/Sonuncu səs indicə verildi\./)).toBeInTheDocument()
  })
})

describe('the words around it', () => {
  it('reads the last vote in minutes, within the hour only', () => {
    expect(lastVoteAgo(minutesAgo(0.5), NOW)).toBe('indicə')
    expect(lastVoteAgo(minutesAgo(12), NOW)).toBe('12 dəqiqə əvvəl')
    expect(lastVoteAgo(minutesAgo(61), NOW)).toBeNull()
    expect(lastVoteAgo(null, NOW)).toBeNull()
  })

  it('writes the invitation in Azerbaijani, with the competition and never a work', () => {
    expect(shareMessage('PRYZMA', '“Əmircanın simvolu” yaradıcılıq müsabiqəsi')).toBe(
      'PRYZMA-da «Əmircanın simvolu» müsabiqəsində səs verdim. Sən də bəyəndiyin işə səs ver.',
    )
    expect(shareMessage('Meydan', 'Şuşa loqosu müsabiqəsi')).toBe(
      'Meydan-da «Şuşa loqosu» müsabiqəsində səs verdim. Sən də bəyəndiyin işə səs ver.',
    )
    expect(shareMessage('Bələdiyyə', 'Xəzər')).toMatch(/^Bələdiyyə-də «Xəzər»/)
  })
})
