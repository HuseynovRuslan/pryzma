import { act, fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { VotingLeaderboard, VotingWork } from '../../api/types'
import { ApiError } from '../../api/client'

const api = vi.hoisted(() => ({ leaderboard: vi.fn() }))
vi.mock('../../api/endpoints', () => ({ votingApi: { leaderboard: api.leaderboard } }))

const { LiveLeaderboard, PROVISIONAL } = await import('./LiveLeaderboard')
const { barShare, BUMP_MS, CLOSED_POLL_MS, gapLine, POLL_MS, useLeaderboard, withLiveCount } = await import(
  '../../pages/voting/leaderboard'
)

const ref = (n: number) => `SUB-2026-${String(n).padStart(5, '0')}`

/** A ranking from (work number, votes) pairs in the server's order, with the server's shared ranks. */
function board(rows: [number, number][], over: Partial<VotingLeaderboard> = {}): VotingLeaderboard {
  const entries = rows.map(([n, votes]) => ({
    rank: 0,
    reference: ref(n),
    title: null,
    voteCount: votes,
    preview: { url: `/f/${n}`, thumbUrl: `/f/${n}/thumb`, width: 400, height: 400 },
  }))
  entries.forEach((e, i) => (e.rank = i > 0 && e.voteCount === entries[i - 1].voteCount ? entries[i - 1].rank : i + 1))
  return {
    state: 'Open',
    updatedAt: '2026-10-05T12:00:00Z',
    totalVotes: entries.reduce((s, e) => s + e.voteCount, 0),
    entries,
    myVoteReference: null,
    ...over,
  }
}

/** The Figma's sample: #36 128, #44 121, #110 118, #52 113, #71 109. */
const SAMPLE: [number, number][] = [
  [36, 128],
  [44, 121],
  [110, 118],
  [52, 113],
  [71, 109],
]

function Harness({ voted = null }: { voted?: string | null }) {
  const live = useLeaderboard('emircan')
  return (
    <MemoryRouter>
      <LiveLeaderboard live={live} slug="emircan" galleryHref="#works" votedReference={voted} />
    </MemoryRouter>
  )
}

/** Lets the pending read answer, without moving the clock. */
const settle = () => act(() => vi.advanceTimersByTimeAsync(0))
const tick = (ms = POLL_MS) => act(() => vi.advanceTimersByTimeAsync(ms))

const rows = () => screen.getAllByRole('listitem').map((li) => li.getAttribute('data-ref'))
const row = (n: number) => document.querySelector<HTMLElement>(`[data-ref="${ref(n)}"]`)!

beforeEach(() => {
  vi.useFakeTimers()
  Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true })
})
afterEach(() => {
  vi.useRealTimers()
  vi.clearAllMocks()
  api.leaderboard.mockReset()
})

describe('the live ranking', () => {
  it('shows the top three, their counts and bars, and the way to the works', async () => {
    api.leaderboard.mockResolvedValue(board(SAMPLE))
    render(<Harness />)
    await settle()

    expect(screen.getByRole('heading', { name: 'Canlı reytinq' })).toBeInTheDocument()
    expect(screen.getByText('Canlı')).toBeInTheDocument()
    expect(rows()).toEqual([ref(36), ref(44), ref(110)])
    expect(within(row(36)).getByText('İş #36')).toBeInTheDocument()
    expect(within(row(36)).getByText('128 səs')).toBeInTheDocument()
    expect(row(36).querySelector('img')).toHaveAttribute('src', '/f/36/thumb')
    // Bars compare with the leader, nothing more.
    expect(row(36).querySelector<HTMLElement>('.lb__fill')!.style.width).toBe('100%')
    expect(row(110).querySelector<HTMLElement>('.lb__fill')!.style.width).toBe(`${(118 / 128) * 100}%`)
    expect(row(36).querySelector('a')).toHaveAttribute('href', `/competitions/emircan/voting/${ref(36)}`)
    expect(screen.getByText('#110 · 2-ci yerə 3 səs qalıb')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'İşlərə bax və səsini ver' })).toHaveAttribute('href', '#works')
    expect(screen.getByText('Müsabiqədə yalnız bir işə səs verə bilərsiniz.')).toBeInTheDocument()
    // Provisional, said quietly (D12).
    expect(screen.getByText(PROVISIONAL)).toHaveClass('lb__provisional')
    // Nobody votes from the ranking.
    expect(screen.queryByRole('button', { name: /Səs ver/ })).not.toBeInTheDocument()
  })

  it('invents no movement on the first read', async () => {
    api.leaderboard.mockResolvedValue(board(SAMPLE))
    render(<Harness />)
    await settle()
    expect(document.querySelectorAll('.lb__move.is-up, .lb__move.is-down')).toHaveLength(0)
    expect(document.querySelectorAll('.lb__move')).toHaveLength(3)
    expect(screen.queryByText(/pillə/)).not.toBeInTheDocument()
  })

  it('reads once at once, then every ten seconds, never two at a time', async () => {
    let answer: (b: VotingLeaderboard) => void = () => undefined
    api.leaderboard.mockImplementationOnce(() => new Promise((resolve) => (answer = resolve)))
    api.leaderboard.mockResolvedValue(board(SAMPLE))
    render(<Harness />)
    expect(api.leaderboard).toHaveBeenCalledTimes(1)
    expect(api.leaderboard).toHaveBeenCalledWith('emircan', expect.any(AbortSignal))

    // The first read is slow: no second one starts behind it.
    await tick(3 * POLL_MS)
    expect(api.leaderboard).toHaveBeenCalledTimes(1)

    answer(board(SAMPLE))
    await settle()
    await tick(POLL_MS - 1)
    expect(api.leaderboard).toHaveBeenCalledTimes(1)
    await tick(1)
    expect(api.leaderboard).toHaveBeenCalledTimes(2)
    await tick()
    expect(api.leaderboard).toHaveBeenCalledTimes(3)
  })

  it('does not start a second read while one is on its way, and reads once more after it', async () => {
    let answer: (b: VotingLeaderboard) => void = () => undefined
    api.leaderboard.mockImplementationOnce(() => new Promise((resolve) => (answer = resolve)))
    api.leaderboard.mockResolvedValue(board(SAMPLE))
    render(<Harness />)
    // The tab is shown again while the first read is still out: it waits its turn.
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'))
    })
    expect(api.leaderboard).toHaveBeenCalledTimes(1)

    answer(board(SAMPLE))
    await settle()
    expect(api.leaderboard).toHaveBeenCalledTimes(2)
    await settle()
    expect(api.leaderboard).toHaveBeenCalledTimes(2)
  })

  it('counts a new vote with a brief "+1 yeni səs" and a closer gap', async () => {
    api.leaderboard.mockResolvedValueOnce(board(SAMPLE))
    api.leaderboard.mockResolvedValue(board([[36, 128], [44, 121], [110, 119], [52, 113], [71, 109]]))
    render(<Harness voted={ref(110)} />)
    await settle()
    expect(screen.getByText('#110 · 2-ci yerə 3 səs qalıb')).toBeInTheDocument()

    await tick()
    expect(within(row(110)).getByText('119 səs')).toBeInTheDocument()
    expect(within(row(110)).getByText('+1 yeni səs')).toBeInTheDocument()
    expect(row(110)).toHaveClass('is-gained')
    expect(screen.getByText('#110 · 2-ci yerə 2 səs qalıb')).toBeInTheDocument()
    // A count that only rose does not reorder: no arrows.
    expect(document.querySelectorAll('.lb__move.is-up')).toHaveLength(0)

    await tick(BUMP_MS)
    expect(screen.queryByText('+1 yeni səs')).not.toBeInTheDocument()
    expect(row(110)).not.toHaveClass('is-gained')
  })

  it('moves a work up from fourth to third, and the one it passed down', async () => {
    api.leaderboard.mockResolvedValueOnce(board([[36, 128], [44, 121], [52, 118], [110, 117], [71, 109]]))
    api.leaderboard.mockResolvedValue(board([[36, 128], [44, 121], [110, 119], [52, 118], [71, 109]]))
    render(<Harness />)
    await settle()
    expect(rows()).toEqual([ref(36), ref(44), ref(52)])

    await tick()
    expect(rows()).toEqual([ref(36), ref(44), ref(110)])
    expect(within(row(110)).getByText('↑ 1')).toBeInTheDocument()
    expect(within(row(110)).getByText('1 pillə yüksəlib')).toBeInTheDocument()
    expect(screen.getByText('İş #110 · 4-cü → 3-cü · 1 pillə yüksəldi')).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent(
      'Reytinqdə ilk üçlük dəyişdi: 1-ci yer İş #36, 2-ci yer İş #44, 3-cü yer İş #110.',
    )

    fireEvent.click(screen.getByRole('button', { name: /Bütün reytinqə bax/ }))
    expect(within(row(52)).getByText('↓ 1')).toBeInTheDocument()
    expect(within(row(52)).getByText('1 pillə enib')).toBeInTheDocument()
    expect(within(row(36)).getByText('—')).toBeInTheDocument()
  })

  it('keeps the latest reorder on screen until the next one', async () => {
    api.leaderboard.mockResolvedValueOnce(board([[36, 128], [44, 121], [52, 118], [110, 117]]))
    api.leaderboard.mockResolvedValue(board([[36, 128], [44, 121], [110, 119], [52, 118]]))
    render(<Harness />)
    await settle()
    await tick()
    await tick()
    expect(within(row(110)).getByText('↑ 1')).toBeInTheDocument()
  })

  it('marks the viewer\'s own work and offers no second vote', async () => {
    api.leaderboard.mockResolvedValue(board(SAMPLE, { myVoteReference: ref(110) }))
    render(<Harness />)
    await settle()
    expect(row(110)).toHaveClass('is-mine')
    expect(within(row(110)).getByText('✓ Sizin səs verdiyiniz iş')).toBeInTheDocument()
    expect(screen.getByText('Səsiniz qeydə alınıb. Bütün sıralama açıq qalır.')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'İşlərə bax və səsini ver' })).not.toBeInTheDocument()
  })

  it('shows the viewer\'s own work under the top three when it is further down', async () => {
    api.leaderboard.mockResolvedValue(board(SAMPLE, { myVoteReference: ref(71) }))
    render(<Harness />)
    await settle()
    expect(rows()).toEqual([ref(36), ref(44), ref(110), ref(71)])
    expect(row(71)).toHaveClass('is-mine', 'is-detached')
    expect(screen.getByText('#71 · 4-cü yerə 4 səs qalıb')).toBeInTheDocument()
  })

  it('opens the whole ranking in place and folds it again', async () => {
    api.leaderboard.mockResolvedValue(board(SAMPLE))
    render(<Harness />)
    await settle()
    const more = screen.getByRole('button', { name: /Bütün reytinqə bax/ })
    expect(more).toHaveAttribute('aria-expanded', 'false')
    fireEvent.click(more)
    expect(rows()).toHaveLength(5)
    const fold = screen.getByRole('button', { name: /Reytinqi yığ/ })
    expect(fold).toHaveAttribute('aria-expanded', 'true')
    fireEvent.click(fold)
    expect(rows()).toHaveLength(3)
  })

  it('opens a long ranking a few dozen rows at a time', async () => {
    const many = Array.from({ length: 30 }, (_, i): [number, number] => [i + 1, 100 - i])
    api.leaderboard.mockResolvedValue(board(many))
    render(<Harness />)
    await settle()
    fireEvent.click(screen.getByRole('button', { name: /Bütün reytinqə bax/ }))
    expect(rows()).toHaveLength(25)
    fireEvent.click(screen.getByRole('button', { name: /Daha 5 iş göstər/ }))
    expect(rows()).toHaveLength(30)
    expect(screen.queryByRole('button', { name: /Daha .* iş göstər/ })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Reytinqi yığ/ }))
    expect(rows()).toHaveLength(3)
  })

  it('stays after voting closes as the provisional ranking, never as the result', async () => {
    api.leaderboard.mockResolvedValue(board(SAMPLE, { state: 'Closed' }))
    render(<Harness />)
    await settle()
    // The owner's decision of 7 October 2026: the ranking is not hidden, and not called final.
    expect(screen.getByRole('heading', { name: 'İlkin səs sıralaması' })).toBeInTheDocument()
    expect(screen.getByText('Bu sıralama audit davam etdiyi üçün dəyişə bilər.')).toBeInTheDocument()
    expect(screen.queryByText(/Yekun reytinq/)).not.toBeInTheDocument()
    expect(
      screen.getByText('Göstərilən sıralama etibarlı səslərə əsaslanan ilkin nəticədir. Yekun nəticələr ayrıca elan ediləcək.'),
    ).toBeInTheDocument()
    // D16: nothing ties the public result to the jury.
    expect(document.body.textContent).not.toMatch(/münsif/i)
    expect(rows()).toEqual([ref(36), ref(44), ref(110)])
    expect(screen.queryByText('Canlı')).not.toBeInTheDocument()
    expect(document.querySelector('.lb__dot')).toBeNull()
    expect(document.querySelectorAll('.lb__move')).toHaveLength(0)
    expect(screen.queryByRole('link', { name: 'İşlərə bax və səsini ver' })).not.toBeInTheDocument()
  })

  it('keeps reading the closed ranking, less often, so a vote set aside leaves it without a reload', async () => {
    api.leaderboard.mockResolvedValueOnce(board(SAMPLE))
    api.leaderboard.mockResolvedValueOnce(board(SAMPLE, { state: 'Closed' }))
    // An admin sets votes aside: the leader loses some, and the order changes.
    api.leaderboard.mockResolvedValue(board([[44, 130], [110, 118], [36, 100]], { state: 'Closed' }))
    render(<Harness />)
    await settle()
    await tick()
    expect(screen.getByRole('heading', { name: 'İlkin səs sıralaması' })).toBeInTheDocument()
    expect(api.leaderboard).toHaveBeenCalledTimes(2)

    // Not every ten seconds any more…
    await tick(CLOSED_POLL_MS - 1_000)
    expect(api.leaderboard).toHaveBeenCalledTimes(2)
    // …but within the half-minute.
    await tick(1_000)
    expect(api.leaderboard).toHaveBeenCalledTimes(3)
    expect(rows()).toEqual([ref(44), ref(110), ref(36)])
    expect(within(row(36)).getByText('100 səs')).toBeInTheDocument()
  })

  it('keeps the last ranking through a failed read and recovers on the next', async () => {
    api.leaderboard.mockResolvedValueOnce(board(SAMPLE))
    api.leaderboard.mockRejectedValueOnce(new ApiError(503, 'Unavailable'))
    api.leaderboard.mockResolvedValue(board([[44, 130], [36, 128], [110, 118]]))
    render(<Harness />)
    await settle()
    await tick()
    expect(rows()).toEqual([ref(36), ref(44), ref(110)])
    expect(within(row(36)).getByText('128 səs')).toBeInTheDocument()

    await tick()
    expect(rows()).toEqual([ref(44), ref(36), ref(110)])
  })

  it('shows nothing, and stops, where there is no ranking to show', async () => {
    api.leaderboard.mockRejectedValue(new ApiError(404, 'Səsvermə hələ başlamayıb.'))
    const { container } = render(<Harness />)
    await settle()
    expect(container).toBeEmptyDOMElement()
    await tick(5 * POLL_MS)
    expect(api.leaderboard).toHaveBeenCalledTimes(1)
  })

  it('pauses in a hidden tab and reads at once when it is shown again', async () => {
    api.leaderboard.mockResolvedValue(board(SAMPLE))
    render(<Harness />)
    await settle()
    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true })
    await tick() // the read already set before the tab was hidden
    await tick(5 * POLL_MS)
    expect(api.leaderboard).toHaveBeenCalledTimes(2)

    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true })
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'))
    })
    expect(api.leaderboard).toHaveBeenCalledTimes(3)
  })

  it('cancels its read and its timer when the page goes away', async () => {
    let signal: AbortSignal | undefined
    api.leaderboard.mockResolvedValueOnce(board(SAMPLE))
    api.leaderboard.mockImplementation((_slug: string, s: AbortSignal) => {
      signal = s
      return new Promise(() => undefined)
    })
    const { unmount } = render(<Harness />)
    await settle()
    await tick()
    expect(signal?.aborted).toBe(false)
    unmount()
    expect(signal?.aborted).toBe(true)
    await tick(5 * POLL_MS)
    expect(api.leaderboard).toHaveBeenCalledTimes(2)
    expect(vi.getTimerCount()).toBe(0)
  })

  describe('the reorder slide', () => {
    const animate = vi.fn()
    let reduce = false

    beforeEach(() => {
      reduce = false
      // Like the browser's, it hands back an Animation.
      animate.mockReset().mockImplementation(() => ({}) as Animation)
      HTMLElement.prototype.animate = animate as unknown as HTMLElement['animate']
      // jsdom lays nothing out; a row's place in its list is its index here.
      Object.defineProperty(HTMLElement.prototype, 'offsetTop', {
        configurable: true,
        get(this: HTMLElement) {
          return this.parentElement ? [...this.parentElement.children].indexOf(this) * 64 : 0
        },
      })
      window.matchMedia = ((query: string) => ({ matches: reduce && query.includes('reduce') })) as typeof window.matchMedia
    })
    afterEach(() => {
      delete (HTMLElement.prototype as Partial<HTMLElement>).animate
      delete (HTMLElement.prototype as { offsetTop?: number }).offsetTop
      delete (window as Partial<Window>).matchMedia
    })

    const race = async () => {
      api.leaderboard.mockResolvedValueOnce(board([[36, 128], [44, 121], [52, 118], [110, 117]]))
      api.leaderboard.mockResolvedValue(board([[36, 128], [110, 122], [44, 121], [52, 118]]))
      render(<Harness />)
      await settle()
      await tick()
    }

    it('slides a row that changed places from where it stood', async () => {
      await race()
      // #44 went from the second row to the third; #36 did not move and does not slide.
      const slid = animate.mock.contexts.map((el) => (el as HTMLElement).dataset.ref)
      expect(slid).toContain(ref(44))
      expect(slid).not.toContain(ref(36))
      const call = animate.mock.calls[slid.indexOf(ref(44))]
      expect(call[0][0]).toEqual({ transform: 'translateY(-64px)' })
    })

    it('with reduced motion, simply shows the new order', async () => {
      reduce = true
      await race()
      expect(rows()).toEqual([ref(36), ref(110), ref(44)])
      expect(animate).not.toHaveBeenCalled()
    })
  })
})

describe('the words and numbers', () => {
  const entries = (rows: [number, number][]) => board(rows).entries

  it('speaks of ties as ties, and never a negative gap', () => {
    expect(gapLine(entries([[36, 10], [44, 10], [110, 4]]), null)).toBe('#110 · Liderliyə 6 səs qalıb')
    expect(gapLine(entries([[36, 10], [44, 10], [110, 4]]), ref(36))).toBe('#36 · liderliyi #44 ilə bölüşür')
    expect(gapLine(entries([[36, 10], [44, 10], [52, 10]]), ref(52))).toBe('#52 · liderliyi daha 2 işlə bölüşür')
    expect(gapLine(entries([[36, 12], [44, 9], [110, 9]]), ref(110))).toBe('#110 · #44 ilə bərabərdir · Liderliyə 3 səs qalıb')
    expect(gapLine(entries([[36, 12], [44, 5]]), ref(36))).toBe('#36 · liderdir, 2-ci yerdən 7 səs öndədir')
    expect(gapLine(entries([[36, 0], [44, 0]]), null)).toBeNull()
    expect(gapLine([], null)).toBeNull()
  })

  it('draws bars relative to the leader, safely at zero', () => {
    expect(barShare(118, 128)).toBeCloseTo(0.922, 3)
    expect(barShare(0, 128)).toBe(0)
    expect(barShare(5, 0)).toBe(0)
  })

  it('keeps a card\'s count in step with the ranking, and never lowers it', () => {
    const work = { reference: ref(36), voteCount: 10 } as VotingWork
    expect(withLiveCount(work, new Map([[ref(36), 12]])).voteCount).toBe(12)
    expect(withLiveCount(work, new Map([[ref(36), 9]])).voteCount).toBe(10)
    expect(withLiveCount(work, undefined)).toBe(work)
  })
})
