import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminPrint, AdminPrintItem } from './api'
import SubmissionsPrintPage from './pages/SubmissionsPrintPage'
import { excerpt, printHref } from './print'

const adminApi = vi.hoisted(() => ({ printSubmissions: vi.fn(), publicAssetBlob: vi.fn(), documentPageBlob: vi.fn() }))
vi.mock('./api', () => ({ adminApi }))
const auth = vi.hoisted(() => ({ roles: ['Admin'] as string[] }))
vi.mock('../auth/AuthContext', () => ({ useAuth: () => ({ status: 'authenticated', user: { roles: auth.roles } }) }))
const submissionsApi = vi.hoisted(() => ({ thumbBlob: vi.fn(() => Promise.resolve(new Blob())) }))
vi.mock('../api/endpoints', () => ({ submissionsApi }))

beforeAll(() => {
  URL.createObjectURL = vi.fn(() => 'blob:print')
  URL.revokeObjectURL = vi.fn()
})

beforeEach(() => {
  vi.clearAllMocks()
  auth.roles = ['Admin']
  adminApi.publicAssetBlob.mockResolvedValue(new Blob())
  adminApi.documentPageBlob.mockResolvedValue(new Blob())
  window.print = vi.fn()
})

const item = (n: number, over: Partial<AdminPrintItem> = {}): AdminPrintItem => ({
  id: n,
  reference: `SUB-2026-000${String(n).padStart(2, '0')}`,
  title: `Əmircanın ruhu ${n}`,
  category: null,
  facts: [{ label: 'Nominasiya', value: 'Simvol və Loqotip Dizaynı' }],
  conceptLabel: 'Konseptin izahı',
  concept: `Konsept ${n}: qala, dəniz və neft.`,
  image: { fileId: n * 10, large: true, width: 1600, height: 1200 },
  author: null,
  pdfPages: 0,
  material: [],
  ...over,
})

const print = (items: AdminPrintItem[], over: Partial<AdminPrint> = {}): AdminPrint => ({
  competitionTitle: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi',
  total: items.length,
  limit: 300,
  authorIncluded: false,
  items,
  ...over,
})

function Where() {
  const l = useLocation()
  return <output data-testid="where">{l.pathname + l.search}</output>
}

const show = (at = '/admin/submissions/print?competitionId=7') =>
  render(
    <MemoryRouter initialEntries={[at]}>
      <Where />
      <Routes>
        <Route path="/admin/submissions/print" element={<SubmissionsPrintPage />} />
        <Route path="/admin/submissions" element={<p>Siyahı</p>} />
      </Routes>
    </MemoryRouter>,
  )

const sheets = (container: HTMLElement) => container.querySelectorAll('.pr-sheet')

describe('the submissions print view', () => {
  it('prints four works per page by default, each card with the jury\'s three marks and their sum — never a vote', async () => {
    adminApi.printSubmissions.mockResolvedValue(print([1, 2, 3, 4, 5].map((n) => item(n))))
    const { container } = show()

    expect(await screen.findByRole('heading', { name: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi' })).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: '4 iş / səhifə (münsif vərəqi)' })).toBeChecked()
    expect(sheets(container)).toHaveLength(2)
    const first = screen.getByRole('region', { name: 'Səhifə 1' })
    const cards = within(first).getAllByRole('article')
    expect(cards).toHaveLength(4)
    expect(within(screen.getByRole('region', { name: 'Səhifə 2' })).getAllByRole('article')).toHaveLength(1)
    const card = cards[1]
    expect(within(card).getByRole('heading', { name: 'Əmircanın ruhu 2' })).toBeInTheDocument()
    expect(within(card).getByText('SUB-2026-00002')).toBeInTheDocument()
    expect(within(card).getByText('Nominasiya: Simvol və Loqotip Dizaynı')).toBeInTheDocument()
    expect(within(card).getByText('Konsept 2: qala, dəniz və neft.')).toBeInTheDocument()
    for (const criterion of ['İdeya:', 'Vizual / icra:', 'Mövzuya uyğunluq:']) expect(within(card).getByText(criterion)).toBeInTheDocument()
    expect(within(card).getAllByText('/ 10')).toHaveLength(3)
    expect(within(card).getByText('Cəm:')).toBeInTheDocument()
    expect(within(card).getByText('/ 30')).toBeInTheDocument()
    expect(within(card).getByText(/Qeyd:/)).toBeInTheDocument()
    expect(container.querySelector('.pr-pages')!.textContent).not.toMatch(/səs|Səs|reytinq|sıralama/)
    expect(screen.getByText('Səhifə 2 / 2')).toBeInTheDocument()
    expect(container.querySelector('.pr-bar__meta')).toHaveTextContent('5 iş · 2 səhifə (A4)')
    // The request carries the list's filters, no names unless asked, and not the finalists.
    // The list's filters travel; the author's name is asked for by default (an internal document); not the finalists.
    expect(adminApi.printSubmissions).toHaveBeenCalledWith(expect.objectContaining({ competitionId: '7' }), true, expect.anything(), false)
  })

  it('prints one work per page as the detailed PDF, with the same marks and page numbers', async () => {
    adminApi.printSubmissions.mockResolvedValue(print([item(1), item(2), item(3)]))
    const { container } = show()
    await screen.findByRole('region', { name: 'Səhifə 1' })

    await userEvent.click(screen.getByRole('radio', { name: '1 iş / səhifə (ətraflı PDF)' }))
    expect(screen.getByTestId('where')).toHaveTextContent('layout=1')
    expect(sheets(container)).toHaveLength(3)
    const second = screen.getByRole('region', { name: 'Səhifə 2: SUB-2026-00002' })
    expect(within(second).getByRole('heading', { name: 'Əmircanın ruhu 2' })).toBeInTheDocument()
    expect(within(second).getByText('Mövzuya uyğunluq:')).toBeInTheDocument()
    expect(within(second).getByText('Qeyd:')).toBeInTheDocument()
    expect(within(second).getByText('Səhifə 2 / 3')).toBeInTheDocument()
  })

  it('prints the frozen finalists as the jury\'s sheets, with a line for the juror, and goes back to the final', async () => {
    adminApi.printSubmissions.mockResolvedValue(print([1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map((n) => item(n))))
    const { container } = show('/admin/submissions/print?competitionId=7&finalists=1')
    expect(await screen.findByRole('heading', { name: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi — münsif paketi (Top 10)' })).toBeInTheDocument()
    expect(adminApi.printSubmissions).toHaveBeenCalledWith(expect.objectContaining({ competitionId: '7' }), true, expect.anything(), true)
    // Ten works, four to a page: three sheets, each with a line for the juror's name.
    expect(sheets(container)).toHaveLength(3)
    expect(container.querySelectorAll('.pr-head__juror')).toHaveLength(3)
    // The owner's instruction heads every sheet of the jury's set.
    expect(screen.getAllByText('Qiymətləndirmə zamanı xalq səsverməsinin nəticələrinə baxmadan işin özünü dəyərləndirin.')).toHaveLength(3)
    expect(screen.getByText(/Səs sayı və xalq sıralaması vərəqdə yoxdur/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Geri' }))
    expect(screen.getByTestId('where')).toHaveTextContent('/admin/competitions/7?tab=final')
  })

  it('shows the author by default on an internal print, and leaves the name out when the admin unticks it', async () => {
    adminApi.printSubmissions.mockImplementation((_f: unknown, includeAuthor: boolean) =>
      Promise.resolve(
        print([item(1, { author: includeAuthor ? 'Leyla Quliyeva' : null })], { authorIncluded: includeAuthor }),
      ),
    )
    show()
    expect(await screen.findByText(/Müəllif: Leyla Quliyeva/)).toBeInTheDocument()
    const box = screen.getByRole('checkbox', { name: 'Müəllif adını göstər' })
    expect(box).toBeChecked()

    await userEvent.click(box)
    await waitFor(() => expect(screen.queryByText(/Müəllif:/)).not.toBeInTheDocument())
    expect(adminApi.printSubmissions).toHaveBeenLastCalledWith(expect.anything(), false, expect.anything(), false)
    expect(screen.getByTestId('where')).toHaveTextContent('author=0')
  })

  it('shows a PDF by its content page, keeps the package compact, and adds every page only when asked', async () => {
    // A four-page PDF (the card shows page 2), a one-page PDF (page 1), and a picture.
    const four = item(1, {
      author: 'Leyla Quliyeva',
      image: { fileId: 10, large: true, width: null, height: null, page: 2 },
      pdfPages: 4,
      material: [1, 2, 3, 4].map((page) => ({ fileId: 10, kind: 'pdf' as const, page, pages: 4 })),
    })
    const one = item(2, {
      image: { fileId: 20, large: true, width: 1600, height: 1200, page: 1 },
      pdfPages: 1,
      material: [{ fileId: 20, kind: 'pdf', page: 1, pages: 1 }],
    })
    const picture = item(3, { material: [{ fileId: 30, kind: 'image', page: 1, pages: 1 }] })
    adminApi.printSubmissions.mockResolvedValue(print([four, one, picture]))
    const { container } = show('/admin/submissions/print?competitionId=7&finalists=1')
    await screen.findByRole('region', { name: 'Səhifə 1' })

    // Compact by default: one sheet of cards, no appendix.
    const box = screen.getByRole('checkbox', { name: 'PDF-lərin bütün səhifələrini əlavə et' })
    expect(box).not.toBeChecked()
    expect(sheets(container)).toHaveLength(1)
    const card = screen.getByRole('article', { name: 'SUB-2026-00001' })
    expect(within(card).getByText('PDF · 4 səhifə')).toBeInTheDocument()
    expect(within(screen.getByRole('article', { name: 'SUB-2026-00002' })).getByText('PDF · 1 səhifə')).toBeInTheDocument()
    // The card's picture: page 2 of the four-page PDF; page 1 (the safe copy) of the one-page PDF.
    await waitFor(() => expect(adminApi.documentPageBlob).toHaveBeenCalledWith(1, 10, 2, expect.anything()))
    expect(adminApi.publicAssetBlob).toHaveBeenCalledWith(2, 20, expect.anything())
    expect(adminApi.documentPageBlob).toHaveBeenCalledTimes(1)
    // The whole material is one click away on screen, for a PDF only, and never on paper.
    const link = within(card).getByRole('link', { name: 'Tam materiala bax' })
    expect(link).toHaveAttribute('href', '/admin/submissions/1')
    expect(link).toHaveClass('pr-screen-only')
    expect(within(screen.getByRole('article', { name: 'SUB-2026-00003' })).queryByRole('link', { name: 'Tam materiala bax' })).toBeNull()

    // Asked for: every page of every work after the cards, numbered on.
    await userEvent.click(box)
    expect(screen.getByTestId('where')).toHaveTextContent('material=1')
    expect(sheets(container)).toHaveLength(1 + 4 + 1 + 1)
    const last = screen.getByRole('region', { name: 'Səhifə 7: SUB-2026-00003, material 1 / 1' })
    expect(last).toHaveTextContent('Səhifə 7 / 7')
    expect(screen.getByRole('region', { name: 'Səhifə 5: SUB-2026-00001, material 4 / 4' })).toHaveTextContent('PDF, səhifə 4 / 4')
    await waitFor(() => expect(adminApi.documentPageBlob).toHaveBeenCalledWith(1, 10, 4, expect.anything()))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Çap et' })).toBeEnabled())
  })

  it('shows the page an admin chose for a PDF, says which on screen only, and still appends every page', async () => {
    // A four-page PDF whose admin chose page 1: the card is its own safe copy, no page is rendered for it.
    const chosen = item(1, {
      image: { fileId: 10, large: true, width: 1600, height: 1131, page: 1, pages: 4 },
      pdfPages: 4,
      material: [1, 2, 3, 4].map((page) => ({ fileId: 10, kind: 'pdf' as const, page, pages: 4 })),
    })
    const automatic = item(2, {
      image: { fileId: 20, large: true, width: null, height: null, page: 2, pages: 4 },
      pdfPages: 4,
      material: [1, 2, 3, 4].map((page) => ({ fileId: 20, kind: 'pdf' as const, page, pages: 4 })),
    })
    adminApi.printSubmissions.mockResolvedValue(print([chosen, automatic]))
    show('/admin/submissions/print?competitionId=7&finalists=1')
    await screen.findByRole('region', { name: 'Səhifə 1' })

    await waitFor(() => expect(adminApi.publicAssetBlob).toHaveBeenCalledWith(1, 10, expect.anything()))
    expect(adminApi.documentPageBlob).toHaveBeenCalledWith(2, 20, 2, expect.anything())
    expect(adminApi.documentPageBlob).not.toHaveBeenCalledWith(1, 10, expect.anything(), expect.anything())
    // "Önizləmə: 1/4" for the admin on screen — a technical note that never reaches paper.
    const note = within(screen.getByRole('article', { name: 'SUB-2026-00001' })).getByText('Önizləmə: 1/4')
    expect(note).toHaveClass('pr-screen-only')
    expect(within(screen.getByRole('article', { name: 'SUB-2026-00002' })).getByText('Önizləmə: 2/4')).toHaveClass('pr-screen-only')

    // The appendix is the whole PDF whatever the choice: pages 1–4 of each.
    await userEvent.click(screen.getByRole('checkbox', { name: 'PDF-lərin bütün səhifələrini əlavə et' }))
    expect(screen.getByRole('region', { name: /SUB-2026-00001, material 1 \/ 4/ })).toBeInTheDocument()
    expect(screen.getByRole('region', { name: /SUB-2026-00001, material 4 \/ 4/ })).toBeInTheDocument()
    await waitFor(() => expect(adminApi.documentPageBlob).toHaveBeenCalledWith(1, 10, 4, expect.anything()))
  })

  it('prints once the pictures are in, and goes back to the list with its filters', async () => {
    adminApi.printSubmissions.mockResolvedValue(print([item(1), item(2, { image: null })]))
    show('/admin/submissions/print?competitionId=7&status=Approved&layout=4')
    const button = await screen.findByRole('button', { name: 'Çap et' })
    await waitFor(() => expect(button).toBeEnabled())
    expect(screen.getByText('Hazırdır. Bütün şəkillər yükləndi.')).toBeInTheDocument()
    // The large copy for a work that has one; a work without a public picture says so instead.
    expect(adminApi.publicAssetBlob).toHaveBeenCalledWith(1, 10, expect.anything())
    expect(screen.getByText('Şəkil yoxdur')).toBeInTheDocument()

    await userEvent.click(button)
    expect(window.print).toHaveBeenCalledTimes(1)

    await userEvent.click(screen.getByRole('link', { name: 'Geri' }))
    expect(screen.getByTestId('where')).toHaveTextContent('/admin/submissions?competitionId=7&status=Approved')
  })

  it('says when the selection is larger than one print, and when nothing matches', async () => {
    adminApi.printSubmissions.mockResolvedValueOnce(print([item(1)], { total: 340 }))
    const { unmount } = show()
    expect(await screen.findByRole('alert')).toHaveTextContent('Seçimdə 340 iş var; ilk 1 iş göstərilir')
    unmount()

    adminApi.printSubmissions.mockResolvedValueOnce(print([]))
    show()
    expect(await screen.findByText('Bu seçimdə çap ediləcək iş yoxdur.')).toBeInTheDocument()
  })

  it('shows nothing to somebody who is not an admin', async () => {
    auth.roles = []
    show()
    expect(await screen.findByText('Bu bölmə yalnız administratorlar üçündür')).toBeInTheDocument()
    expect(adminApi.printSubmissions).not.toHaveBeenCalled()
  })
})

describe('print helpers', () => {
  it('carries the list filters to the print view, never the page number', () => {
    expect(printHref(new URLSearchParams('competitionId=7&status=Approved&page=3&q=SUB'))).toBe(
      '/admin/submissions/print?competitionId=7&status=Approved&q=SUB',
    )
    expect(printHref(new URLSearchParams(''))).toBe('/admin/submissions/print')
  })

  it('cuts a long description at a word, with an ellipsis', () => {
    const long = 'Bu iş Əmircanın tarixi irsini, qalalarını və dəniz sahilini müasir qrafik dillə təqdim edir. '.repeat(6)
    const cut = excerpt(long, 120)
    expect(cut.length).toBeLessThanOrEqual(121)
    expect(cut.endsWith('…')).toBe(true)
    expect(excerpt('Qısa mətn')).toBe('Qısa mətn')
  })
})
