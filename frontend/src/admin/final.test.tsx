import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import { PlatformContext } from '../platform/context'
import { platformDefaults } from '../platform/defaults'
import type { AdminCompetition, AdminFinal, AdminPrintItem, FinalShow, FinalShowWork, JuryFinalist } from './api'
import { buildDeck, parseMark, TOP_THREE_ORDER } from './finalShow'
import FinalShowPage from './pages/FinalShowPage'
import { FinalTab } from './pages/FinalTab'

const adminApi = vi.hoisted(() => ({
  final: vi.fn(),
  finalShow: vi.fn(),
  saveJuryMarks: vi.fn(),
  lockJury: vi.fn(),
  publicAssetBlob: vi.fn(),
  documentPageBlob: vi.fn(),
  workMaterial: vi.fn(),
  renameJuror: vi.fn(),
  freezeFinal: vi.fn(),
}))
vi.mock('./api', () => ({ adminApi }))
vi.mock('../auth/AuthContext', () => ({ useAuth: () => ({ status: 'authenticated', user: { roles: ['Admin'] } }) }))
vi.mock('../api/endpoints', () => ({ submissionsApi: { thumbBlob: vi.fn(() => Promise.resolve(new Blob())) } }))
vi.mock('../components/submission/AuthedImage', () => ({ AuthedImage: () => null }))

beforeAll(() => {
  URL.createObjectURL = vi.fn(() => 'blob:show')
  URL.revokeObjectURL = vi.fn()
})
beforeEach(() => {
  vi.clearAllMocks()
  window.location.hash = ''
  adminApi.publicAssetBlob.mockResolvedValue(new Blob())
  adminApi.documentPageBlob.mockResolvedValue(new Blob())
})

const ref = (n: number) => `SUB-2026-${String(n).padStart(5, '0')}`

/**
 * Ten finalists. Public ranks follow the number (1 is the public's first); jury ranks run the other way round for
 * the first five. Places 1–3: works 2, 5, 3.
 */
const work = (n: number, over: Partial<FinalShowWork> = {}): FinalShowWork => ({
  submissionId: n,
  reference: ref(n),
  title: `Əsər ${n}`,
  image: null,
  validVotes: 300 - n * 20,
  publicRank: n,
  publicPoints: 11 - n,
  juryTotal: 30 + n,
  juryRank: 11 - n,
  juryPoints: n,
  finalScore: 11,
  rank: n,
  place: n,
  tie: 'None',
  prizeAzn: null,
  ...over,
})

const places: Record<number, number> = { 2: 1, 5: 2, 3: 3, 1: 4, 4: 5, 6: 6, 7: 7, 8: 8, 9: 9, 10: 10 }
const prize: Record<number, number> = { 1: 250, 2: 150, 3: 100 }
const show = (over: Partial<FinalShow> = {}, works?: FinalShowWork[]): FinalShow => ({
  competitionTitle: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi',
  jurorCount: 2,
  juryMax: 60,
  works:
    works ??
    Array.from({ length: 10 }, (_, i) => {
      const n = i + 1
      return work(n, { place: places[n], rank: places[n], prizeAzn: prize[places[n]] ?? null })
    }),
  prizesDecided: true,
  prizes: [250, 150, 100],
  ...over,
})

describe('the final show running order', () => {
  it('opens, shows the ten, then the jury, then the public — each tenth to first — then places ten to four, then the top three', () => {
    const deck = buildDeck(show())
    const kinds = deck.map((s) => s.kind)
    expect(kinds.slice(0, 2)).toEqual(['opening', 'finalists'])
    expect(kinds.filter((k) => k === 'finalist')).toHaveLength(10)

    const jury = deck.flatMap((s) => (s.kind === 'jury' ? [s.work.juryRank] : []))
    expect(jury).toEqual([10, 9, 8, 7, 6, 5, 4, 3, 2, 1])
    const pub = deck.flatMap((s) => (s.kind === 'public' ? [s.work.publicRank] : []))
    expect(pub).toEqual([10, 9, 8, 7, 6, 5, 4, 3, 2, 1])
    // Intros come before their reveals.
    expect(kinds.indexOf('juryIntro')).toBeLessThan(kinds.indexOf('jury'))
    expect(kinds.indexOf('publicIntro')).toBeLessThan(kinds.indexOf('public'))
    expect(kinds.indexOf('jury')).toBeLessThan(kinds.indexOf('public'))

    // Places ten to four only; the top three stay closed until their own part.
    const below = deck.flatMap((s) => (s.kind === 'place' ? [s.work.place] : []))
    expect(below).toEqual([10, 9, 8, 7, 6, 5, 4])

    // Third, second, a pause, then the winner — with the prize money — and the table at the end.
    const top = deck.slice(kinds.indexOf('top3Intro'))
    expect(top.map((s) => s.kind)).toEqual(['top3Intro', 'placeCard', 'prize', 'placeCard', 'prize', 'winnerPause', 'prize', 'table'])
    const prizes = top.flatMap((s) => (s.kind === 'prize' ? [[s.place, s.work.reference, s.work.prizeAzn]] : []))
    expect(prizes).toEqual([
      [3, ref(3), 100],
      [2, ref(5), 150],
      [1, ref(2), 250],
    ])
    expect(TOP_THREE_ORDER).toEqual([3, 2, 1])
  })

  it('stops before the top three while a tie at a prize waits for the jury', () => {
    const works = show().works.map((w) =>
      w.submissionId === 5 || w.submissionId === 3 ? { ...w, place: null, rank: 2, tie: 'NeedsJuryDecision' as const, prizeAzn: null } : w,
    )
    const deck = buildDeck(show({ prizesDecided: false }, works))
    const kinds = deck.map((s) => s.kind)
    expect(kinds[kinds.length - 1]).toBe('blocked')
    expect(kinds).not.toContain('top3Intro')
    expect(kinds).not.toContain('prize')
    expect(kinds).not.toContain('table')
    // The places below the prizes are still shown; nothing of the tied works' place.
    expect(deck.flatMap((s) => (s.kind === 'place' ? [s.work.submissionId] : []))).not.toContain(5)
  })

  it('is the same show however many times it is built', () => {
    expect(JSON.stringify(buildDeck(show()))).toBe(JSON.stringify(buildDeck(show())))
  })

  it('reads a juror\'s mark as typed: 0–10 in halves, comma or point', () => {
    expect(['', '7', '7,5', '7.5', '0', '10', ' 9,5 '].map(parseMark)).toEqual([null, 7, 7.5, 7.5, 0, 10, 9.5])
    expect(['10,5', '-1', '7,25', 'x', '11'].map(parseMark)).toEqual(Array(5).fill('invalid'))
  })
})

describe('the final show page', () => {
  const page = () =>
    render(
      <PlatformContext.Provider value={{ ...platformDefaults, platformName: 'PRYZMA' }}>
        <MemoryRouter initialEntries={['/admin/final-show/7']}>
          <Routes>
            <Route path="/admin/final-show/:competitionId" element={<FinalShowPage />} />
          </Routes>
        </MemoryRouter>
      </PlatformContext.Provider>,
    )

  it('opens on the title, moves only when the presenter says so, and only ever reads', async () => {
    adminApi.finalShow.mockResolvedValue(show())
    page()
    expect(await screen.findByRole('heading', { name: 'PRYZMA FINAL' })).toBeInTheDocument()
    expect(screen.getByText('Bir mövzu, min baxış')).toBeInTheDocument()
    expect(screen.getByText(/Xalq səsverməsi tamamlandı/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Tam ekran' })).toBeInTheDocument()

    await userEvent.click(screen.getAllByRole('button', { name: /Finalı başlat/ })[0])
    expect(screen.getByRole('heading', { name: 'PRYZMA-nın 10 finalçısı' })).toBeInTheDocument()
    // The ten, with nothing of any number yet.
    expect(document.body.textContent).not.toMatch(/xal|səs\b|etibarlı/)

    // The arrow keys and a clicker's Page Down / Page Up.
    await userEvent.keyboard('{PageDown}')
    expect(screen.getByText('Finalçı 1 / 10')).toBeInTheDocument()
    await userEvent.keyboard('{ArrowLeft}')
    expect(screen.getByRole('heading', { name: 'PRYZMA-nın 10 finalçısı' })).toBeInTheDocument()
    expect(window.location.hash).toBe('#1')

    // No timer moves it.
    vi.useFakeTimers()
    act(() => vi.advanceTimersByTime(120_000))
    vi.useRealTimers()
    expect(screen.getByRole('heading', { name: 'PRYZMA-nın 10 finalçısı' })).toBeInTheDocument()
    expect(adminApi.finalShow).toHaveBeenCalledTimes(1)
  })

  it('reveals the winner with how the place was reached, and comes back to the same screen after a reload', async () => {
    adminApi.finalShow.mockResolvedValue(show())
    const deck = buildDeck(show())
    const winner = deck.findIndex((s) => s.kind === 'prize' && s.place === 1)
    window.location.hash = `#${winner}`
    page()
    expect(await screen.findByText('1-ci yer')).toBeInTheDocument()
    expect(document.querySelector('.fs-eyebrow')).toHaveTextContent('PRYZMA qalibi')
    expect(screen.getByText('250 AZN')).toBeInTheDocument()
    const why = document.querySelector('.fs-why') as HTMLElement
    expect(within(why).getByText('Xalq')).toBeInTheDocument()
    expect(why).toHaveTextContent('260 etibarlı səs · 9 xal')
    expect(why).toHaveTextContent('32 / 60 · 2 xal')
    expect(why).toHaveTextContent('Yekun11 xal')
  })

  it('shows each finalist by the page that stands for it — the admin\'s choice, else the automatic one', async () => {
    // Work 1: a six-page PDF the admin set to page 1; work 2: a four-page PDF left automatic (page 2); work 3: a picture.
    const works = show().works.map((w) =>
      w.submissionId === 1
        ? { ...w, image: { fileId: 10, large: true, width: 1600, height: 1131, page: 1, pages: 6 } }
        : w.submissionId === 2
          ? { ...w, image: { fileId: 20, large: true, width: null, height: null, page: 2, pages: 4 } }
          : w.submissionId === 3
            ? { ...w, image: { fileId: 30, large: true, width: 800, height: 800 } }
            : w,
    )
    adminApi.finalShow.mockResolvedValue(show({}, works))
    page()
    await screen.findByRole('heading', { name: 'PRYZMA FINAL' })
    await waitFor(() => expect(adminApi.documentPageBlob).toHaveBeenCalledWith(2, 20, 2, expect.anything()))
    expect(adminApi.publicAssetBlob).toHaveBeenCalledWith(1, 10, expect.anything())
    expect(adminApi.publicAssetBlob).toHaveBeenCalledWith(3, 30, expect.anything())
    expect(adminApi.documentPageBlob).toHaveBeenCalledTimes(1)
  })

  it('says why it cannot open before the jury is locked', async () => {
    adminApi.finalShow.mockRejectedValue(new ApiError(409, 'Final şousu münsif qiymətləndirməsi kilidləndikdən sonra açılır.'))
    page()
    expect(await screen.findByRole('alert')).toHaveTextContent('kilidləndikdən sonra açılır')
  })
})

describe('the final tab', () => {
  const competition = { id: 7, slug: 'emircanin-simvolu', title: 'Əmircanın simvolu' } as AdminCompetition
  const finalist = (n: number, complete: boolean): JuryFinalist => ({
    submissionId: n,
    reference: ref(n),
    title: `Əsər ${n}`,
    image: null,
    marks: [1, 2].map((jurorId) => ({
      jurorId,
      idea: complete ? 7 : null,
      execution: complete ? 8 : null,
      relevance: complete ? 9.5 : null,
      subtotal: complete ? 24.5 : 0,
      complete,
    })),
    juryTotal: complete ? 49 : 0,
    complete,
  })
  const frozen = (over: Partial<AdminFinal> = {}): AdminFinal => ({
    competitionId: 7,
    competitionTitle: 'Əmircanın simvolu',
    stage: 'Frozen',
    preview: null,
    frozenAt: '2026-10-07T12:00:00Z',
    frozenBy: 'Admin',
    frozenValidVotes: 1370,
    frozenInvalidatedVotes: 256,
    frozenVotesFingerprint: 'ef066f24c62547841d095c9caed65665',
    votesChangedSinceFreeze: false,
    prizes: [250, 150, 100],
    jurors: [
      { id: 1, label: 'Münsif 1', hasMarks: false },
      { id: 2, label: 'Münsif 2', hasMarks: false },
    ],
    finalists: Array.from({ length: 10 }, (_, i) => finalist(i + 1, false)),
    missingMarks: 60,
    canRefreeze: true,
    juryLockedAt: null,
    juryLockedBy: null,
    calculation: null,
    publishedAt: null,
    publishedBy: null,
    ...over,
  })

  const tab = () =>
    render(
      <MemoryRouter>
        <FinalTab competition={competition} />
      </MemoryRouter>,
    )

  it('enters the jury\'s marks blind to the vote and locks only when every mark is in', async () => {
    adminApi.final.mockResolvedValue(frozen())
    tab()
    expect(await screen.findByRole('heading', { name: 'Münsif balları' })).toBeInTheDocument()
    expect(screen.getByRole('note')).toHaveTextContent(
      'Qiymətləndirmə zamanı xalq səsverməsinin nəticələrinə baxmadan işin özünü dəyərləndirin.',
    )
    // Nothing of the public vote while the jury's marks are typed in.
    const jury = screen.getByRole('heading', { name: 'Münsif balları' }).closest('section')!
    expect(jury.textContent).not.toMatch(/etibarlı|Xalq yeri|Xalq xalı|Yekun|\d+ səs|\d+ xal/)
    expect(jury.querySelectorAll('[data-votes], .fin-result')).toHaveLength(0)
    expect(screen.getByRole('button', { name: 'Münsif qiymətləndirməsini kilidlə' })).toBeDisabled()
    // Print: the jury's sheets, and — apart — the public vote's two documents.
    const print = screen.getByRole('group', { name: 'Çap' })
    const docs = within(print).getAllByRole('link')
    expect(docs.map((d) => d.querySelector('.fin-print__doc-title')!.textContent)).toEqual([
      'Münsif paketi — Top 10',
      'Bütün işləri çap et',
      'Top 10 xalq nəticəsi',
      'Tam səs sıralaması',
    ])
    expect(docs.map((d) => d.getAttribute('href'))).toEqual([
      '/admin/submissions/print?competitionId=7&finalists=1',
      '/admin/submissions/print?competitionId=7',
      '/admin/final-print/7?mode=top10',
      '/admin/final-print/7?mode=full',
    ])

    // A mark out of range is refused before it is sent.
    const idea = screen.getByRole('textbox', { name: `Münsif 1: ${ref(1)}, İdeya` })
    await userEvent.type(idea, '10,5')
    expect(screen.getByText(/0,5 addımla olmalıdır/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Münsif 1 — balları yadda saxla' })).toBeDisabled()
    await userEvent.clear(idea)
    await userEvent.type(idea, '7,5')
    adminApi.saveJuryMarks.mockResolvedValue(frozen({ missingMarks: 59 }))
    await userEvent.click(screen.getByRole('button', { name: 'Münsif 1 — balları yadda saxla' }))
    expect(adminApi.saveJuryMarks).toHaveBeenCalledWith(
      7,
      1,
      expect.arrayContaining([{ submissionId: 1, idea: 7.5, execution: null, relevance: null }]),
    )
  })

  it('lets the whole work be read from the jury\'s row — every page — and never with its vote', async () => {
    const pdf = { fileId: 10, large: true, width: 1600, height: 1131, page: 1, pages: 6 }
    adminApi.final.mockResolvedValue(frozen({ finalists: [{ ...finalist(1, false), image: pdf }, finalist(2, false)], missingMarks: 12 }))
    const item = (id: number): AdminPrintItem => ({
      id,
      reference: ref(id),
      title: `Əsər ${id}`,
      category: null,
      facts: [],
      conceptLabel: 'Konseptin izahı',
      concept: 'Konsept',
      image: id === 1 ? pdf : null,
      author: `Müəllif ${id}`,
      pdfPages: id === 1 ? 6 : 0,
      material: id === 1 ? [1, 2, 3, 4, 5, 6].map((page) => ({ fileId: 10, kind: 'pdf' as const, page, pages: 6 })) : [],
    })
    adminApi.workMaterial.mockImplementation((id: number) => Promise.resolve(item(id)))
    tab()
    await screen.findByRole('heading', { name: 'Münsif balları' })
    const grid = screen.getByRole('table', { name: 'Münsif 1: ballar' })
    // The row says which page its thumbnail is, for the admin.
    expect(within(grid).getByText(/Önizləmə: 1\/6/)).toBeInTheDocument()

    await userEvent.click(within(grid).getByRole('button', { name: `${ref(1)}: tam materiala bax` }))
    const dialog = await screen.findByRole('dialog', { name: 'Əsər 1' })
    expect(within(dialog).getByText('1 / 2')).toBeInTheDocument()
    // Opens on the chosen page; all six are there.
    expect(within(dialog).getByText(/PDF, səhifə 1 \/ 6/)).toBeInTheDocument()
    expect(within(dialog).getAllByRole('button', { name: /^PDF, səhifə \d \/ 6$/ })).toHaveLength(6)
    // The work, and nothing of the vote: no rank, no votes, no context block, no link to the admin page.
    expect(dialog.textContent).not.toMatch(/Xalq|səs\b|Etibarlı|Etibarsız|sıralaması/)
    expect(within(dialog).queryByRole('region')).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('link', { name: 'Ətraflı səhifə' })).not.toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bağla' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('names a juror, shows each work\'s author internally, and keeps unsaved marks while a work is read', async () => {
    const named = (n: number) => ({ ...finalist(n, false), author: `Müəllif ${n} Soyadlı` })
    adminApi.final.mockResolvedValue(frozen({ finalists: [named(1), named(2)], missingMarks: 12 }))
    adminApi.workMaterial.mockImplementation((id: number) =>
      Promise.resolve({
        id, reference: ref(id), title: `Əsər ${id}`, category: null, facts: [], conceptLabel: null, concept: null,
        image: null, author: `Müəllif ${id} Soyadlı`, pdfPages: 0, material: [],
      } satisfies AdminPrintItem),
    )
    tab()
    await screen.findByRole('heading', { name: 'Münsif balları' })
    const grid = screen.getByRole('table', { name: 'Münsif 1: ballar' })
    // The author, for the admin — and still nothing of the vote.
    expect(within(grid).getByText('Müəllif: Müəllif 1 Soyadlı')).toBeInTheDocument()
    expect(grid.textContent).not.toMatch(/etibarlı|\d+ səs|xal\b/)

    // A mark typed in but not saved survives opening and closing the work.
    const idea = screen.getByRole('textbox', { name: `Münsif 1: ${ref(1)}, İdeya` })
    await userEvent.type(idea, '7,5')
    await userEvent.click(within(grid).getByRole('button', { name: `${ref(1)}: tam materiala bax` }))
    expect(await screen.findByRole('dialog', { name: 'Əsər 1' })).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: `Münsif 1: ${ref(1)}, İdeya` })).toHaveValue('7,5')
    expect(screen.getByRole('button', { name: 'Münsif 1 — balları yadda saxla' })).toBeEnabled()
    expect(adminApi.saveJuryMarks).not.toHaveBeenCalled()

    // The juror's own name, for the screens and sheets: the tab, the sheet and the summary follow it.
    const renamed = frozen({
      finalists: [named(1), named(2)],
      missingMarks: 12,
      jurors: [
        { id: 1, label: 'Aysel Məmmədova', hasMarks: false },
        { id: 2, label: 'Münsif 2', hasMarks: false },
      ],
    })
    adminApi.renameJuror.mockResolvedValue(renamed)
    const name = screen.getByRole('textbox', { name: 'Münsifin adı' })
    expect(name).toHaveValue('Münsif 1')
    await userEvent.clear(name)
    await userEvent.type(name, '  Aysel   Məmmədova ')
    await userEvent.click(screen.getByRole('button', { name: 'Adı saxla' }))
    expect(adminApi.renameJuror).toHaveBeenCalledWith(7, 1, 'Aysel Məmmədova')
    expect(await screen.findByRole('tab', { name: 'Aysel Məmmədova' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('columnheader', { name: 'Aysel Məmmədova' })).toBeInTheDocument()
    // Two jurors: the summary's sum is out of 60.
    expect(screen.getByRole('columnheader', { name: 'Cəm / 60' })).toBeInTheDocument()
  })

  it('re-freezes the ten only with a reason, which goes to the audit with the superseded snapshot', async () => {
    adminApi.final.mockResolvedValue(frozen({ votesChangedSinceFreeze: true }))
    adminApi.freezeFinal.mockResolvedValue(frozen())
    tab()
    expect(await screen.findByText(/səs vəziyyəti dəyişib/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Yenidən təsdiqlə' }))
    const dialog = screen.getByRole('dialog', { name: 'Top 10 yenidən dondurulsun?' })
    expect(dialog).toHaveTextContent('Əvvəlki Top 10 silinmir')
    const confirm = within(dialog).getByRole('button', { name: 'Yenidən dondur' })
    await userEvent.click(within(dialog).getByRole('checkbox'))
    expect(confirm).toBeDisabled()
    const why = 'Post-close integrity audit approved a new competition-wide disposable-email invalidation rule before jury scoring began.'
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Əvvəlki siyahı niyə əvəz olunur?' }), why)
    expect(confirm).toBeEnabled()
    await userEvent.click(confirm)
    expect(adminApi.freezeFinal).toHaveBeenCalledWith(7, [250, 150, 100], why)
  })

  it("works out each sheet's sum, steps marks by halves from the keyboard and refuses a mark like 7,3", async () => {
    adminApi.final.mockResolvedValue(
      frozen({ finalists: [finalist(1, true), finalist(2, false)], missingMarks: 6 }),
    )
    tab()
    await screen.findByRole('heading', { name: 'Münsif balları' })
    // One grid for the header and every row: the work, three marks, the sum.
    const grid = screen.getByRole('table', { name: 'Münsif 1: ballar' })
    const rows = within(grid).getAllByRole('row')
    expect(rows).toHaveLength(3)
    expect(within(rows[0]).getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      'İş',
      'İdeya / 10',
      'Vizual / icra / 10',
      'Mövzuya uyğunluq / 10',
      'Cəm / 30',
    ])
    rows.forEach((r) => expect(r).toHaveClass('fin-marks__row'))
    // The sum is worked out, never typed: 7 + 8 + 9,5.
    expect(screen.getByRole('status', { name: `Münsif 1: ${ref(1)}, cəm` })).toHaveTextContent('24,5')
    expect(screen.getByRole('status', { name: `Münsif 1: ${ref(2)}, cəm` })).toHaveTextContent('—')

    const idea = screen.getByRole('textbox', { name: `Münsif 1: ${ref(2)}, İdeya` })
    const visual = screen.getByRole('textbox', { name: `Münsif 1: ${ref(2)}, Vizual / icra` })
    const theme = screen.getByRole('textbox', { name: `Münsif 1: ${ref(2)}, Mövzuya uyğunluq` })
    await userEvent.type(idea, '8,5')
    await userEvent.click(visual)
    await userEvent.keyboard('{ArrowUp}')
    expect(visual).toHaveValue('0')
    await userEvent.clear(visual)
    await userEvent.type(visual, '9')
    await userEvent.click(theme)
    await userEvent.type(theme, '7,5')
    await userEvent.keyboard('{ArrowUp}{ArrowUp}{ArrowDown}')
    expect(theme).toHaveValue('8')
    expect(screen.getByRole('status', { name: `Münsif 1: ${ref(2)}, cəm` })).toHaveTextContent('25,5')
    // Ten is the most: a step past it stays at 10.
    await userEvent.clear(theme)
    await userEvent.type(theme, '10')
    await userEvent.keyboard('{ArrowUp}')
    expect(theme).toHaveValue('10')

    await userEvent.clear(idea)
    await userEvent.type(idea, '7,3')
    expect(idea).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByText(/0,5 addımla olmalıdır/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Münsif 1 — balları yadda saxla' })).toBeDisabled()
  })

  it('opens the lock once complete, and shows the combined result with the tie the jury must settle', async () => {
    adminApi.final.mockResolvedValue(
      frozen({ finalists: Array.from({ length: 10 }, (_, i) => finalist(i + 1, true)), missingMarks: 0 }),
    )
    tab()
    const lock = await screen.findByRole('button', { name: 'Münsif qiymətləndirməsini kilidlə' })
    expect(lock).toBeEnabled()

    const row = (n: number, place: number | null, tie: 'None' | 'NeedsJuryDecision') => ({
      submissionId: n,
      reference: ref(n),
      title: null,
      validVotes: 100 - n,
      publicRank: n,
      publicPoints: 11 - n,
      juryTotal: 49,
      juryRank: 1,
      juryPoints: 10,
      finalScore: 21 - n,
      rank: place ?? 2,
      place,
      tie,
      prizeAzn: place === 1 ? 250 : null,
    })
    adminApi.lockJury.mockResolvedValue(
      frozen({
        stage: 'JuryLocked',
        missingMarks: 0,
        juryLockedAt: '2026-10-08T10:00:00Z',
        juryLockedBy: 'Admin',
        calculation: {
          jurorCount: 2,
          juryMax: 60,
          rows: [row(1, 1, 'None'), row(2, null, 'NeedsJuryDecision'), row(3, null, 'NeedsJuryDecision')],
          unresolvedTies: [{ rank: 2, works: [2, 3].map((n) => ({ submissionId: n, reference: ref(n), title: null })) }],
          tieBreaks: [],
          publishProblems: [`Münsif qərarı tələb olunur: ${ref(2)}, ${ref(3)}.`],
        },
      }),
    )
    await userEvent.click(lock)
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Kilidlə' }))

    expect(await screen.findByRole('heading', { name: 'Yekun hesablama' })).toBeInTheDocument()
    expect(screen.getByRole('group', { name: 'Münsif qərarı tələb olunur' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Rəsmi nəticələri dərc et' })).toBeDisabled()
    expect(screen.getByRole('link', { name: 'Final şousunu aç' })).toHaveAttribute('href', '/admin/final-show/7')
    // The tie is recorded only with the jury's reason.
    expect(screen.getByRole('button', { name: 'Münsif qərarını qeyd et' })).toBeDisabled()
  })
})
