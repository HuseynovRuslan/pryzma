import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { PlatformContext } from '../platform/context'
import { platformDefaults } from '../platform/defaults'
import type { AdminPrintItem, LeaderboardPrint, LeaderboardPrintRow } from './api'
import LeaderboardPrintPage from './pages/LeaderboardPrintPage'
import { leaderboardPrintHref } from './print'

const adminApi = vi.hoisted(() => ({ leaderboardPrint: vi.fn(), publicAssetBlob: vi.fn(), documentPageBlob: vi.fn() }))
vi.mock('./api', () => ({ adminApi }))
const auth = vi.hoisted(() => ({ roles: ['Admin'] as string[] }))
vi.mock('../auth/AuthContext', () => ({ useAuth: () => ({ status: 'authenticated', user: { roles: auth.roles } }) }))
const submissionsApi = vi.hoisted(() => ({ thumbBlob: vi.fn(() => Promise.resolve(new Blob())) }))
vi.mock('../api/endpoints', () => ({ submissionsApi }))

beforeAll(() => {
  URL.createObjectURL = vi.fn(() => 'blob:picture')
  URL.revokeObjectURL = vi.fn()
})
beforeEach(() => {
  vi.clearAllMocks()
  auth.roles = ['Admin']
  window.print = vi.fn()
  adminApi.publicAssetBlob.mockResolvedValue(new Blob())
  adminApi.documentPageBlob.mockResolvedValue(new Blob())
})

const ref = (n: number) => `SUB-2026-${String(n).padStart(5, '0')}`

const work = (id: number, reference: string, title: string, author: string | null, over: Partial<AdminPrintItem> = {}): AdminPrintItem => ({
  id,
  reference,
  title,
  category: null,
  facts: [{ label: 'Nominasiya', value: 'Simvol və Loqotip Dizaynı' }],
  conceptLabel: null,
  concept: null,
  image: { fileId: id * 10, large: true, width: 1600, height: 1600 },
  author,
  pdfPages: 0,
  material: [],
  ...over,
})

const frozen = [271, 265, 200, 173, 114, 76, 71, 45, 35, 25]
const top10 = (withAuthor = true): LeaderboardPrint => ({
  mode: 'Top10',
  competitionTitle: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi',
  frozenAt: '2026-10-07T10:13:30Z',
  validVotes: 1370,
  invalidatedVotes: 256,
  totalVotes: 1626,
  votesFingerprint: 'ef066f24c62547841d095c9caed65665',
  generatedAt: '2026-10-07T11:00:00Z',
  rows: frozen.map((votes, i) => ({
    rank: i + 1,
    validVotes: votes,
    publicPoints: 10 - i,
    item: work(
      100 + i,
      ref([111, 61, 90, 96, 68, 134, 138, 136, 146, 84][i]),
      i === 0 ? 'XİLƏDƏN ƏMİRCANA' : `Əsər ${i + 1}`,
      withAuthor ? `Müəllif ${i + 1}` : null,
      // The first work is a four-page PDF: its card shows page 2.
      i === 0 ? { pdfPages: 4, image: { fileId: 1000, large: true, width: null, height: null, page: 2 } } : {},
    ),
  })),
})

/** 70 works; the 5th and 6th level on 50 votes, the last ten on 0. */
const full = (): LeaderboardPrint => {
  const votes = Array.from({ length: 70 }, (_, i) => (i >= 60 ? 0 : i === 4 || i === 5 ? 50 : 400 - i * 6))
  const rows: LeaderboardPrintRow[] = []
  votes.forEach((v, i) => {
    const rank = i > 0 && v === votes[i - 1] ? rows[i - 1].rank : i + 1
    rows.push({ rank, validVotes: v, publicPoints: null, item: work(200 + i, ref(200 + i), `İş ${i + 1}`, `Müəllif ${i + 1}`) })
  })
  return { ...top10(), mode: 'Full', frozenAt: null, rows }
}

function Where() {
  const l = useLocation()
  return <output data-testid="where">{l.pathname + l.search}</output>
}

const show = (at: string) =>
  render(
    <PlatformContext.Provider value={{ ...platformDefaults, platformName: 'PRYZMA' }}>
      <MemoryRouter initialEntries={[at]}>
        <Where />
        <Routes>
          <Route path="/admin/final-print/:competitionId" element={<LeaderboardPrintPage />} />
          <Route path="/admin/competitions/:id" element={<p>Final</p>} />
        </Routes>
      </MemoryRouter>
    </PlatformContext.Provider>,
  )

const sheetsOf = (c: HTMLElement) => [...c.querySelectorAll('.pr-sheet')] as HTMLElement[]

describe('the public vote on paper', () => {
  it('shows the frozen Top 10 as the works themselves — four cards to a page, rank, votes, points and author', async () => {
    adminApi.leaderboardPrint.mockResolvedValue(top10())
    const { container } = show(leaderboardPrintHref(7, 'top10'))
    expect((await screen.findAllByRole('heading', { name: 'PRYZMA — Xalq səsverməsinin nəticələri' })).length).toBe(3)
    // The author is asked for by default: an internal document.
    expect(adminApi.leaderboardPrint).toHaveBeenCalledWith(7, 'Top10', true, expect.anything())

    const sheets = sheetsOf(container)
    expect(sheets).toHaveLength(3)
    expect(sheets.map((s) => within(s).getAllByRole('article').length)).toEqual([4, 4, 2])
    const first = sheets[0]
    expect(first).toHaveTextContent('Dondurulub: 7 oktyabr 2026, 14:13 (Bakı vaxtı)')
    expect(first).toHaveTextContent('Etibarlı: 1370 · Etibarsız sayılmış: 256 · Ümumi: 1626')
    expect(first).toHaveTextContent('Bu siyahı final mərhələsi üçün dondurulmuş auditdən keçmiş xalq səsverməsi nəticəsidir.')

    const card = within(first).getByRole('article', { name: '1: SUB-2026-00111' })
    expect(card).toHaveTextContent('1xalq sıralaması')
    expect(card).toHaveTextContent('271 etibarlı səs')
    expect(card).toHaveTextContent('Xalq xalı: 10')
    expect(card).toHaveTextContent('XİLƏDƏN ƏMİRCANA')
    expect(card).toHaveTextContent('Müəllif: Müəllif 1')
    expect(card).toHaveTextContent('PDF · 4 səhifə')
    expect(within(sheets[2]).getByRole('article', { name: '10: SUB-2026-00084' })).toHaveTextContent('Xalq xalı: 1')
    expect(sheets[2]).toHaveTextContent('Səhifə 3 / 3')
    // Pictures with the admin's credentials; printing waits for them.
    await waitFor(() => expect(container.querySelectorAll('.pr-sheet img')).toHaveLength(10))
    expect(adminApi.documentPageBlob).toHaveBeenCalledWith(100, 1000, 2, expect.anything())
    expect(adminApi.publicAssetBlob).toHaveBeenCalledWith(101, 1010, expect.anything())
    // A vote print never carries the material appendix.
    expect(container.querySelectorAll('.pr-sheet--appendix')).toHaveLength(0)
    await userEvent.click(screen.getByRole('button', { name: 'Çap et' }))
    expect(window.print).toHaveBeenCalledTimes(1)
    expect(container.textContent).not.toMatch(/@|\+994/)
  })

  it('shows the full ranking as cards on numbered pages, level works sharing a rank, and claims no prize', async () => {
    adminApi.leaderboardPrint.mockResolvedValue(full())
    const { container } = show(leaderboardPrintHref(7, 'full'))
    expect((await screen.findAllByRole('heading', { name: 'Xalq səsverməsinin auditdən keçmiş sıralaması' })).length).toBe(18)
    expect(adminApi.leaderboardPrint).toHaveBeenCalledWith(7, 'Full', true, expect.anything())

    const sheets = sheetsOf(container)
    expect(sheets).toHaveLength(18) // 70 works, four to a page
    expect(sheets.every((s) => within(s).getAllByRole('article').length <= 4)).toBe(true)
    expect(sheets[0]).toHaveTextContent(
      'Bu sıralama xalq səsverməsinin nəticəsidir və rəsmi 1-ci, 2-ci, 3-cü yerləri avtomatik müəyyən etmir.',
    )
    expect(sheets[17]).toHaveTextContent('Səhifə 18 / 18')
    const ranks = sheets.flatMap((s) => within(s).getAllByRole('article').map((a) => a.querySelector('.lb-card__rank-n')!.textContent))
    expect(ranks.slice(0, 7)).toEqual(['1', '2', '3', '4', '5', '5', '7'])
    expect(ranks.slice(60)).toEqual(Array(10).fill('61'))
    const fifth = within(sheets[1]).getByRole('article', { name: '5: SUB-2026-00204' })
    expect(fifth).toHaveTextContent('50 etibarlı səs')
    expect(fifth).toHaveTextContent('Müəllif: Müəllif 5')
    // Votes, never points or a place.
    expect(container.querySelector('.pr-pages')!.textContent).not.toMatch(/Xalq xalı|-ci yer|qalib/i)
  })

  it('keeps the compact table as an option and the author as a choice', async () => {
    adminApi.leaderboardPrint.mockImplementation((_id: number, mode: string, withAuthor: boolean) =>
      Promise.resolve(mode === 'Top10' ? top10(withAuthor) : full()),
    )
    const { container } = show(leaderboardPrintHref(7, 'full'))
    await screen.findAllByRole('heading', { name: 'Xalq səsverməsinin auditdən keçmiş sıralaması' })
    expect(screen.getByRole('radio', { name: 'Şəkilli görünüş — 4 iş / A4' })).toBeChecked()
    await userEvent.click(screen.getByRole('radio', { name: 'Cədvəl görünüşü' }))
    expect(screen.getByTestId('where')).toHaveTextContent('view=table')
    expect(sheetsOf(container)).toHaveLength(3) // 70 rows, 32 to a page
    expect(container.querySelectorAll('.lb-table tbody tr')).toHaveLength(70)

    // The other document opens in its default view: the cards.
    await userEvent.click(screen.getByRole('link', { name: 'Top 10 xalq nəticəsi' }))
    expect((await screen.findAllByRole('heading', { name: 'PRYZMA — Xalq səsverməsinin nəticələri' })).length).toBe(3)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Müəllif adını göstər' }))
    await waitFor(() => expect(adminApi.leaderboardPrint).toHaveBeenLastCalledWith(7, 'Top10', false, expect.anything()))
    expect(screen.getByTestId('where')).toHaveTextContent('author=0')
    await waitFor(() => expect(container.textContent).not.toMatch(/Müəllif \d/))
  })

  it('goes back to the Final tab and shows nothing to somebody who is not an admin', async () => {
    adminApi.leaderboardPrint.mockResolvedValue(top10())
    const { unmount } = show(leaderboardPrintHref(7, 'top10'))
    await screen.findAllByRole('heading', { name: 'PRYZMA — Xalq səsverməsinin nəticələri' })
    await userEvent.click(screen.getByRole('link', { name: 'Geri' }))
    expect(screen.getByTestId('where')).toHaveTextContent('/admin/competitions/7?tab=final')
    unmount()

    vi.clearAllMocks()
    auth.roles = []
    show(leaderboardPrintHref(7, 'top10'))
    expect(await screen.findByText('Bu bölmə yalnız administratorlar üçündür')).toBeInTheDocument()
    expect(adminApi.leaderboardPrint).not.toHaveBeenCalled()
  })
})
