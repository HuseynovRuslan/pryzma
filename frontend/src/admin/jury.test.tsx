import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { PlatformContext } from '../platform/context'
import { platformDefaults } from '../platform/defaults'
import type { AdminCompetition } from './api'
import type { AdminJuryEvaluation, JuryMatrix, JuryPrint, JuryRanking } from './juryApi'

const juryAdminApi = vi.hoisted(() => ({
  get: vi.fn(),
  create: vi.fn(),
  freezeRoster: vi.fn(),
  addJuror: vi.fn(),
  renameJuror: vi.fn(),
  removeJuror: vi.fn(),
  issueInvitation: vi.fn(),
  revokeInvitation: vi.fn(),
  lockOnBehalf: vi.fn(),
  reopen: vi.fn(),
  matrix: vi.fn(),
  exportCsv: vi.fn(),
  paperPreview: vi.fn(),
  paperCommit: vi.fn(),
  ranking: vi.fn(),
  rankingCsv: vi.fn(),
  print: vi.fn(),
  audit: vi.fn(),
}))
vi.mock('./juryApi', async (original) => ({ ...(await original<typeof import('./juryApi')>()), juryAdminApi }))
vi.mock('./api', () => ({ adminApi: { publicAssetBlob: vi.fn(() => Promise.resolve(new Blob())), documentPageBlob: vi.fn(() => Promise.resolve(new Blob())) } }))
vi.mock('../auth/AuthContext', () => ({ useAuth: () => ({ status: 'authenticated', user: { roles: ['Admin'] } }) }))
vi.mock('../api/endpoints', () => ({ submissionsApi: { thumbBlob: vi.fn(() => Promise.resolve(new Blob())) } }))
vi.mock('../components/submission/AuthedImage', () => ({ AuthedImage: () => null }))

const { JuryTab } = await import('./pages/JuryTab')
const JuryPrintPage = (await import('./pages/JuryPrintPage')).default

beforeAll(() => {
  URL.createObjectURL = vi.fn(() => 'blob:x')
  URL.revokeObjectURL = vi.fn()
})

const competition = { id: 7, title: 'Əmircanın simvolu' } as AdminCompetition

const progress = (id: number, label: string, scored: number, status: 'NotStarted' | 'InProgress' | 'Submitted') => ({
  jurorId: id,
  label,
  scored,
  missing: 99 - scored,
  status,
  submittedAt: status === 'Submitted' ? '2026-10-09T09:00:00Z' : null,
  submittedByAdmin: false,
  invitation: { state: 'None' as const, issuedAt: null, expiresAt: null, lastSignInAt: null, identityConfirmedAt: null, failedAttempts: 0 },
})

const evaluation = (over: Partial<AdminJuryEvaluation> = {}): AdminJuryEvaluation => ({
  exists: true,
  competitionId: 7,
  competitionTitle: 'Əmircanın simvolu',
  format: 'InternalJuryRankingV1',
  status: 'Open',
  createdAt: '2026-10-08T09:00:00Z',
  rosterFrozenAt: '2026-10-08T09:01:00Z',
  eligibleNow: 99,
  rosterSize: 99,
  rosterDiffers: false,
  rosterLocked: true,
  jurorCount: 3,
  expectedScores: 297,
  completedScores: 150,
  missingScores: 147,
  jurors: [progress(1, 'Aysel Məmmədova', 99, 'Submitted'), progress(2, 'Rauf Əliyev', 51, 'InProgress'), progress(3, 'Nərmin Qasımova', 0, 'NotStarted')],
  hasCombinedFinal: true,
  ...over,
})

const matrix: JuryMatrix = {
  jurors: [
    { id: 1, label: 'Aysel Məmmədova', submitted: true },
    { id: 2, label: 'Rauf Əliyev', submitted: false },
    { id: 3, label: 'Nərmin Qasımova', submitted: false },
  ],
  rows: [
    { submissionId: 101, reference: 'SUB-2026-00001', title: 'Bir', scores: [7, 6, null], sources: ['Online', 'Paper', null], hasComment: [true, false, false], average: null },
    { submissionId: 102, reference: 'SUB-2026-00002', title: 'İki', scores: [5, null, null], sources: ['Online', null, null], hasComment: [false, false, false], average: null },
  ],
  complete: false,
}

const notYet: JuryRanking = {
  competitionTitle: 'Əmircanın simvolu',
  complete: false,
  rosterSize: 99,
  jurorCount: 3,
  submittedJurors: 1,
  expectedScores: 297,
  completedScores: 150,
  rows: [],
  tieGroups: 0,
  generatedAt: '2026-10-09T10:00:00Z',
}

/** 99 works: a three-way tie at the top, a pair at 5th, the rest one by one. */
const ranking = (): JuryRanking => {
  const sums = [29, 29, 29, 28, 27, 27, ...Array.from({ length: 93 }, (_, i) => 26 - Math.floor(i / 4))]
  const rows = sums.map((sum, i) => {
    const position = sums.indexOf(sum) + 1
    const tieSize = sums.filter((x) => x === sum).length
    return {
      position,
      submissionId: 500 + i,
      reference: `SUB-2026-${String(i + 1).padStart(5, '0')}`,
      title: `Əsər ${i + 1}`,
      average: Math.round((sum / 3) * 10000) / 10000,
      scoreSum: sum,
      scoreCount: 3,
      tied: tieSize > 1,
      tieSize,
    }
  })
  return { ...notYet, complete: true, submittedJurors: 3, completedScores: 297, rows, tieGroups: new Set(rows.filter((r) => r.tied).map((r) => r.position)).size }
}

function renderTab() {
  return render(
    <PlatformContext.Provider value={platformDefaults}>
      <MemoryRouter>
        <JuryTab competition={competition} />
      </MemoryRouter>
    </PlatformContext.Provider>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  juryAdminApi.matrix.mockResolvedValue(matrix)
  juryAdminApi.ranking.mockResolvedValue(notYet)
  juryAdminApi.audit.mockResolvedValue([
    { id: 1, action: 'JuryRosterFrozen', entityType: 'Competition', entityId: '7', summary: '99 works', userName: 'Admin', createdAt: '2026-10-08T09:01:00Z' },
  ])
})

describe('the admin jury tab', () => {
  it('offers to start the internal evaluation, saying it is never public and the old 50/50 final stays as history', async () => {
    juryAdminApi.get.mockResolvedValue(evaluation({ exists: false, status: null, format: null, jurors: [], jurorCount: 0 }))
    renderTab()
    expect(await screen.findByRole('heading', { name: 'Bütün işlərin daxili münsif qiymətləndirməsi' })).toBeInTheDocument()
    expect(screen.getByText(/heç bir ictimai səhifədə görünmür, mükafat və pul mükafatı vermir/)).toBeInTheDocument()
    expect(screen.getByText(/Xalq səsverməsi, onun nəticələri və dondurulmuş ictimai sıralama dəyişmir/)).toBeInTheDocument()
    expect(screen.getByText(/köhnə 50\/50 final tarixçə kimi qalır/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Qiymətləndirməni başlat' })).toBeEnabled()
  })

  it('shows progress per juror, the matrix with paper and note marks, the ranking’s wait and the audit', async () => {
    juryAdminApi.get.mockResolvedValue(evaluation())
    renderTab()
    expect(await screen.findByText('150 / 297')).toBeInTheDocument()
    const jurors = screen.getAllByRole('table', { name: '' })[0] // the first table is the jurors'
    expect(within(jurors).getByText('Yekunlaşdırıb')).toBeInTheDocument()
    expect(within(jurors).getByText('51 / 99')).toBeInTheDocument()
    expect(await screen.findByTitle('Kağız kataloqdan')).toBeInTheDocument()
    expect(screen.getByTitle('Qeyd var')).toBeInTheDocument()
    // Before every juror has submitted: no position, no average — the progress only.
    expect(await screen.findByText(/yekunlaşdırdıqdan sonra hesablanır/)).toBeInTheDocument()
    expect(screen.queryByRole('table', { name: 'Daxili reytinq' })).not.toBeInTheDocument()
    // Nothing of awards: no prize, no publication, no show.
    for (const word of [/mükafat/i, /dərc et/i, /şou/i, /AZN/]) expect(screen.queryByText(word)).not.toBeInTheDocument()
    expect(await screen.findByText('İşlərin siyahısı donduruldu')).toBeInTheDocument()
  })

  it('shows a new invitation once — the link and the code apart — with the owner’s “not yet” warning', async () => {
    juryAdminApi.get.mockResolvedValue(evaluation())
    juryAdminApi.issueInvitation.mockResolvedValue({
      jurorId: 2,
      label: 'Rauf Əliyev',
      link: '/jury/start#t=SECRET',
      code: '482 913',
      expiresAt: '2026-10-22T09:00:00Z',
    })
    renderTab()
    const buttons = await screen.findAllByRole('button', { name: 'Dəvət ver' })
    await userEvent.click(buttons[1])
    expect(screen.getByText(/real dəvətlər hələ göndərilmir/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Dəvət yarat' }))
    expect(juryAdminApi.issueInvitation).toHaveBeenCalledWith(7, 2, 14)
    expect(await screen.findByText(`${window.location.origin}/jury/start#t=SECRET`)).toBeInTheDocument()
    expect(screen.getByText('482 913')).toBeInTheDocument()
    expect(screen.getByText(/yalnız indi/)).toBeInTheDocument()
  })

  it('a paper sheet is previewed, and each conflict needs a choice and a reason before anything is saved', async () => {
    juryAdminApi.get.mockResolvedValue(evaluation())
    juryAdminApi.paperPreview.mockResolvedValue({
      jurorId: 2,
      label: 'Rauf Əliyev',
      rows: [
        { submissionId: 101, reference: 'SUB-2026-00001', paper: 8, existing: 6, existingSource: 'Online', status: 'Conflict' },
        { submissionId: 102, reference: 'SUB-2026-00002', paper: 5, existing: null, existingSource: null, status: 'New' },
      ],
      new: 1,
      same: 0,
      conflicts: 1,
      missing: 0,
      invalidLines: [],
      jurorLocked: false,
    })
    juryAdminApi.paperCommit.mockResolvedValue({ saved: 2, unchanged: 0, conflictsResolved: 1, scored: 52, total: 99 })
    renderTab()
    await userEvent.selectOptions(await screen.findByLabelText('Münsif'), '2')
    await userEvent.type(await screen.findByLabelText('İş №1 balı'), '8')
    await userEvent.type(screen.getByLabelText('İş №2 balı'), '5')
    await userEvent.click(screen.getByRole('button', { name: 'Yoxla (önizləmə)' }))
    const save = await screen.findByRole('button', { name: 'Saxla' })
    expect(save).toBeDisabled()
    await userEvent.click(screen.getByRole('radio', { name: /Kağız: 8/ }))
    expect(save).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Səbəb (ən azı 10 simvol)'), 'Münsif vərəqdə 8 yazıb.')
    expect(save).toBeEnabled()
    await userEvent.click(save)
    await waitFor(() =>
      expect(juryAdminApi.paperCommit).toHaveBeenCalledWith(
        7,
        2,
        [
          { submissionId: 101, score: '8' },
          { submissionId: 102, score: '5' },
        ],
        [{ submissionId: 101, keep: 'paper', seen: 6 }],
        'Münsif vərəqdə 8 yazıb.',
      ),
    )
  })

  it('ranks every work 1 to 99 with ties shown as ties, and exports it as CSV and PDF', async () => {
    juryAdminApi.get.mockResolvedValue(evaluation())
    juryAdminApi.ranking.mockResolvedValue(ranking())
    juryAdminApi.rankingCsv.mockResolvedValue(new Blob(['x']))
    renderTab()
    const table = await screen.findByRole('table', { name: 'Daxili reytinq' })
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows).toHaveLength(99)
    const cells = (i: number) => within(rows[i]).getAllByRole('cell').map((c) => c.textContent)
    // Three works level at the top: all three are 1st and marked; nobody is made the winner. The next is 4th.
    expect([cells(0), cells(1), cells(2)].map((c) => c[0])).toEqual(['1', '1', '1'])
    expect(cells(0)).toEqual(['1', 'İş №1Əsər 1', '9,67', '3 / 3', 'Bərabər · 3 iş'])
    expect(cells(3)[0]).toBe('4')
    expect(cells(3)[4]).toBe('')
    expect(cells(4).slice(0, 1).concat(cells(4)[4])).toEqual(['5', 'Bərabər · 2 iş'])
    expect(cells(98)[3]).toBe('3 / 3')
    expect(screen.getByText(/bərabərlik qrupu/)).toHaveTextContent(`${ranking().tieGroups} bərabərlik qrupu`)
    expect(screen.getByText(/ictimai deyil və mükafat vermir/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('checkbox', { name: 'Yalnız bərabərliklər' }))
    expect(within(table).getAllByRole('row').slice(1).every((r) => r.className === 'is-tied')).toBe(true)

    const panel = screen.getByRole('region', { name: 'Daxili reytinq' })
    await userEvent.click(within(panel).getByRole('button', { name: 'CSV ixrac' }))
    expect(juryAdminApi.rankingCsv).toHaveBeenCalledWith(7)
    expect(within(panel).getByRole('link', { name: 'PDF / çap' })).toHaveAttribute('href', '/admin/jury-print/7?doc=ranking')
  })
})

const NAMES = ['Aysel Məmmədova', 'Rauf Əliyev', 'Nərmin Qasımova', 'Leyla Hüseynova', 'Kamran Rzayev', 'Səbinə Əhmədova', 'Tural Quliyev']

/**
 * 99 works as the server sends them, most valid votes first: the first two level on 180 (both 1st), the third lost 162
 * votes to an audit decision; references run the other way (İş №99 first). `jurors` of them configured.
 */
const printOf = (jurors: number): JuryPrint => ({
  competitionTitle: 'Əmircanın simvolu',
  works: Array.from({ length: 99 }, (_, i) => ({
    submissionId: 1000 + i,
    reference: `SUB-2026-${String(99 - i).padStart(5, '0')}`,
    title: `Əsər ${99 - i}`,
    image: null,
    pdfPages: i === 0 ? 4 : 0,
    material: [],
    vote: {
      position: i <= 1 ? 1 : i + 1,
      tieSize: i <= 1 ? 2 : 1,
      validVotes: i <= 1 ? 180 : 180 - 2 * i,
      invalidatedVotes: i === 2 ? 162 : 0,
    },
  })),
  jurors: NAMES.slice(0, jurors).map((label, k) => ({ id: k + 1, label, submitted: false })),
})

describe('the printed jury catalog — the evaluation form itself', () => {
  const at = (search: string) =>
    render(
      <PlatformContext.Provider value={platformDefaults}>
        <MemoryRouter initialEntries={[`/admin/jury-print/7${search}`]}>
          <Routes>
            <Route path="/admin/jury-print/:competitionId" element={<JuryPrintPage />} />
          </Routes>
        </MemoryRouter>
      </PlatformContext.Provider>,
    )
  const sheets = () => [...document.querySelectorAll<HTMLElement>('.jp-sheet')]
  const cards = () => [...document.querySelectorAll<HTMLElement>('.jp-card')]
  const keysOf = (card: HTMLElement) => [...card.querySelectorAll('.jp-mark__key')].map((k) => k.textContent)

  it('puts four works on each A4 page in the server’s vote order, each with its public vote and a field per juror', async () => {
    juryAdminApi.print.mockResolvedValue(printOf(5))
    at('?doc=catalog')
    await screen.findByText(/99 iş · 25 səhifə \(A4\) · 5 münsif \(M1–M5\)/)
    expect(sheets()).toHaveLength(25)
    expect(sheets()[0].querySelectorAll('.jp-card')).toHaveLength(4)
    expect(sheets()[24].querySelectorAll('.jp-card')).toHaveLength(3)
    expect(within(sheets()[0]).getAllByText('Səhifə 1 / 25')).toHaveLength(2)

    // The server's order, kept: by valid votes, not by number.
    expect(cards().slice(0, 4).map((c) => c.querySelector('.jp-card__no')?.textContent)).toEqual(['İş №99', 'İş №98', 'İş №97', 'İş №96'])
    expect(cards().slice(0, 4).map((c) => c.querySelector('.jp-card__pos')?.textContent)).toEqual(['1-ci yer', '1-ci yer', '3-cü yer', '4-cü yer'])
    expect(within(cards()[0]).getByText('bərabər · 2 iş')).toBeInTheDocument()
    expect(within(cards()[0]).getByText('PDF · 4 səhifə')).toBeInTheDocument()
    const third = cards()[2]
    expect(within(third).getByText('176')).toBeInTheDocument()
    expect(within(third).getByText('162 etibarsız sayılıb')).toBeInTheDocument()
    expect((third.querySelector('.vbar__valid') as HTMLElement).style.flexGrow).toBe('176')
    expect((third.querySelector('.vbar__invalid') as HTMLElement).style.flexGrow).toBe('162')
    expect(cards()[0].querySelector('.vbar__invalid')).toBeNull()
    expect(within(cards()[0]).getByText('etibarsız səs yoxdur')).toBeInTheDocument()
    // Nothing yellow: no "pending" state is recorded, so none is drawn.
    expect(document.querySelector('[class*="pending"]')).toBeNull()

    // Five fields on every card, keyed M1–M5; the names only in the key at the top of every page.
    for (const card of cards()) expect(keysOf(card)).toEqual(['M1', 'M2', 'M3', 'M4', 'M5'])
    for (const sheet of sheets()) {
      const legend = [...sheet.querySelectorAll('.jp-legend__item')].map((x) => x.textContent)
      expect(legend).toEqual(NAMES.slice(0, 5).map((n, k) => `M${k + 1} ${n}`))
    }
    for (const card of cards()) for (const name of NAMES) expect(card.textContent).not.toContain(name)

    // No long reference, author or audit rule anywhere on paper; the instruction names the fields.
    const paper = document.querySelector('.pr-pages')?.textContent ?? ''
    expect(paper).not.toMatch(/SUB-|Müəllif|D12|D14|\bR2\b|telefon/)
    expect(within(sheets()[0]).getByText(/yalnız öz xananıza \(M1–M5\)/)).toBeInTheDocument()
    expect(within(sheets()[24]).getByText(/Məxfi: yalnız münsiflər və səlahiyyətli təşkilatçılar üçün/)).toBeInTheDocument()
  })

  it('draws exactly the jurors configured: three fields for three, all seven for seven — none invented, none left out', async () => {
    juryAdminApi.print.mockResolvedValue(printOf(3))
    const { unmount } = at('?doc=catalog')
    await screen.findByText(/3 münsif \(M1–M3\)/)
    for (const card of cards()) expect(keysOf(card)).toEqual(['M1', 'M2', 'M3'])
    expect(document.querySelector('.jp-marks--rows-1')).not.toBeNull()
    unmount()

    juryAdminApi.print.mockResolvedValue(printOf(7))
    at('?doc=catalog')
    await screen.findByText(/7 münsif \(M1–M7\)/)
    for (const card of cards()) expect(keysOf(card)).toEqual(['M1', 'M2', 'M3', 'M4', 'M5', 'M6', 'M7'])
    // Five to a row, the rest on a second row of the same card.
    expect(cards()[0].querySelector('.jp-marks--rows-2')).not.toBeNull()
    expect(sheets()[0].querySelectorAll('.jp-legend__item')).toHaveLength(7)
    expect(sheets()).toHaveLength(25)
  })

  it('with no juror configured it draws no field and says so', async () => {
    juryAdminApi.print.mockResolvedValue(printOf(0))
    at('?doc=catalog')
    expect(await screen.findByText(/Münsif təyin edilməyib: kataloqda bal xanası olmayacaq/)).toBeInTheDocument()
    expect(document.querySelectorAll('.jp-mark')).toHaveLength(0)
  })

  it('an old score-sheet link opens the catalog — there are no separate sheets any more', async () => {
    juryAdminApi.print.mockResolvedValue(printOf(5))
    at('?doc=sheets&juror=2')
    await screen.findByText(/99 iş · 25 səhifə/)
    expect(document.querySelectorAll('.jp-scores')).toHaveLength(0)
    expect(screen.queryByRole('radio', { name: /Bal vərəqləri/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('radio', { name: /6 iş/ })).not.toBeInTheDocument()
  })
})

describe('the printed internal ranking', () => {
  const at = () =>
    render(
      <PlatformContext.Provider value={platformDefaults}>
        <MemoryRouter initialEntries={['/admin/jury-print/7?doc=ranking']}>
          <Routes>
            <Route path="/admin/jury-print/:competitionId" element={<JuryPrintPage />} />
          </Routes>
        </MemoryRouter>
      </PlatformContext.Provider>,
    )

  it('prints every work on numbered A4 pages, marked internal, ties marked', async () => {
    juryAdminApi.print.mockResolvedValue({ competitionTitle: 'Əmircanın simvolu', works: [], jurors: [] })
    juryAdminApi.ranking.mockResolvedValue(ranking())
    at()
    await screen.findByText(/99 iş · 3 səhifə/)
    const pages = document.querySelectorAll('.jp-ranking')
    expect(pages).toHaveLength(3)
    expect(pages[0].querySelectorAll('tbody tr')).toHaveLength(33)
    expect(within(pages[0] as HTMLElement).getByText('Səhifə 1 / 3')).toBeInTheDocument()
    expect(within(pages[0] as HTMLElement).getAllByText(/Daxili sənəd — ictimai deyil/).length).toBeGreaterThan(0)
    expect(within(pages[0] as HTMLElement).getAllByText('bərabər (3)')).toHaveLength(3)
  })

  it('says it is not ready before every juror has submitted', async () => {
    juryAdminApi.print.mockResolvedValue({ competitionTitle: 'Əmircanın simvolu', works: [], jurors: [] })
    juryAdminApi.ranking.mockResolvedValue(notYet)
    at()
    expect(await screen.findByText(/yekunlaşdırdıqdan sonra hazır olur/)).toBeInTheDocument()
    expect(document.querySelectorAll('.jp-ranking')).toHaveLength(0)
  })
})
