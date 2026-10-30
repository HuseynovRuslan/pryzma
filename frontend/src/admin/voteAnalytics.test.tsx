import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { PlatformContext } from '../platform/context'
import { platformDefaults } from '../platform/defaults'
import type { JuryPrint } from './juryApi'
import { breakdownText, orderWorks, type VoteCatalog, type VoteCatalogWork } from './voteCatalogApi'

const voteCatalogApi = vi.hoisted(() => ({ catalog: vi.fn() }))
const juryAdminApi = vi.hoisted(() => ({ print: vi.fn(), ranking: vi.fn() }))
vi.mock('./voteCatalogApi', async (original) => ({ ...(await original<typeof import('./voteCatalogApi')>()), voteCatalogApi }))
vi.mock('./juryApi', async (original) => ({ ...(await original<typeof import('./juryApi')>()), juryAdminApi }))
vi.mock('./api', () => ({
  adminApi: { publicAssetBlob: vi.fn(() => Promise.resolve(new Blob())), documentPageBlob: vi.fn(() => Promise.resolve(new Blob())) },
}))
vi.mock('../auth/AuthContext', () => ({ useAuth: () => ({ status: 'authenticated', user: { roles: ['Admin'] } }) }))
vi.mock('../api/endpoints', () => ({ submissionsApi: { thumbBlob: vi.fn(() => Promise.resolve(new Blob())) } }))

const JuryPrintPage = (await import('./pages/JuryPrintPage')).default

beforeAll(() => {
  URL.createObjectURL = vi.fn(() => 'blob:x')
  URL.revokeObjectURL = vi.fn()
})
beforeEach(() => vi.clearAllMocks())

const none = { duplicatePhone: 0, coordinatedAccounts: 0, disposableEmail: 0 }

/** 12 works by valid votes; #3 (İş №10) lost 162 to D14 and 9 to the phone rule, #7 lost 4 to R2; two tie on 50. */
const catalog = (): VoteCatalog => {
  const valid = [200, 183, 173, 114, 109, 76, 50, 50, 35, 25, 11, 0]
  const works: VoteCatalogWork[] = valid.map((v, i) => ({
    position: valid.indexOf(v) + 1,
    tieSize: valid.filter((x) => x === v).length,
    submissionId: 100 + i,
    reference: `SUB-2026-${String(12 - i).padStart(5, '0')}`,
    title: `Əsər ${i + 1}`,
    image: { fileId: 900 + i, large: true, width: 1, height: 1 },
    pdfPages: 0,
    validVotes: v,
    invalidatedVotes: i === 2 ? 171 : i === 6 ? 4 : 0,
    invalidated: i === 2 ? { duplicatePhone: 9, coordinatedAccounts: 0, disposableEmail: 162 } : i === 6 ? { ...none, coordinatedAccounts: 4 } : none,
  }))
  return {
    competitionTitle: 'Əmircanın simvolu',
    validVotes: valid.reduce((a, b) => a + b, 0),
    invalidatedVotes: 175,
    invalidated: { duplicatePhone: 9, coordinatedAccounts: 4, disposableEmail: 162 },
    worksWithInvalidations: 2,
    pendingReview: null,
    outsideCatalog: 0,
    works,
    generatedAt: '2026-10-08T13:00:00Z',
  }
}

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

const refs = () => [...document.querySelectorAll('.va-card__ref')].map((x) => x.textContent)

describe('the admin vote analytics catalog', () => {
  it('orders every work by valid votes across all pages, nine to an A4 page, from the server’s numbers', async () => {
    voteCatalogApi.catalog.mockResolvedValue(catalog())
    at('?doc=analytics')
    await screen.findByText(/12 iş · 2 səhifə/)
    expect(juryAdminApi.print).not.toHaveBeenCalled()
    const sheets = document.querySelectorAll('.va-sheet')
    expect(sheets).toHaveLength(2)
    expect(sheets[0].querySelectorAll('.va-card')).toHaveLength(9)
    expect(sheets[1].querySelectorAll('.va-card')).toHaveLength(3)
    expect(refs().slice(0, 3)).toEqual(['İş №12', 'İş №11', 'İş №10'])
    // Positions, a shared one marked.
    expect([...document.querySelectorAll('.va-card__pos')].map((x) => x.textContent)).toEqual(
      ['1', '2', '3', '4', '5', '6', '7=', '7=', '9', '10', '11', '12'],
    )
    // Every page says it is the admin's, not the jury's.
    expect(within(sheets[1] as HTMLElement).getByText('Admin analitikası — münsiflərə verilmir')).toBeInTheDocument()
  })

  it('draws the bar from valid and set-aside votes and names the rules — no yellow, no risk label', async () => {
    voteCatalogApi.catalog.mockResolvedValue(catalog())
    at('?doc=analytics')
    await screen.findByText(/12 iş/)
    const third = document.querySelectorAll('.va-card')[2] as HTMLElement
    expect(within(third).getByText('173')).toBeInTheDocument()
    expect(within(third).getByText(/171 etibarsız/)).toBeInTheDocument()
    expect(within(third).getByText('telefon (D12) 9 · D14 162')).toBeInTheDocument()
    expect((third.querySelector('.va-bar__valid') as HTMLElement).style.flexGrow).toBe('173')
    expect((third.querySelector('.va-bar__invalid') as HTMLElement).style.flexGrow).toBe('171')
    const clean = document.querySelectorAll('.va-card')[0] as HTMLElement
    expect(clean.querySelector('.va-bar__invalid')).toBeNull()
    expect(within(clean).getByText('Etibarsız sayılmış səs yoxdur')).toBeInTheDocument()
    // The summary on the first page: totals by rule, and pending review said to be not recorded.
    expect(screen.getByText(/175/, { selector: '.va-summary__numbers strong' })).toBeInTheDocument()
    expect(screen.getByText(/baxış gözləyən vəziyyət qeydə alınmır/)).toBeInTheDocument()
    expect(document.querySelector('.va-bar__pending')).toBeNull()
    expect(document.body.textContent).not.toMatch(/şübhəli|risk|fırıldaq/i)
  })

  it('filters to the works with set-aside votes and returns to submission-number order', async () => {
    voteCatalogApi.catalog.mockResolvedValue(catalog())
    at('?doc=analytics')
    await screen.findByText(/12 iş/)
    await userEvent.click(screen.getByRole('checkbox', { name: /Yalnız etibarsız sayılmış səsi olanlar \(2\)/ }))
    expect(refs()).toEqual(['İş №10', 'İş №6'])
    await userEvent.click(screen.getByRole('checkbox', { name: /Yalnız etibarsız/ }))
    await userEvent.click(screen.getByRole('radio', { name: 'Nömrəyə görə' }))
    expect(refs().slice(0, 3)).toEqual(['İş №1', 'İş №2', 'İş №3'])
    expect(document.querySelector('.va-head__title')?.textContent).toContain('nömrəyə görə')
  })
})

describe('the jury’s own catalog', () => {
  it('carries each work’s public vote as counts only — no rule, no voter, no analytics call', async () => {
    const print: JuryPrint = {
      competitionTitle: 'Əmircanın simvolu',
      works: [1, 2, 3].map((n) => ({
        submissionId: n,
        reference: `SUB-2026-0000${n}`,
        title: `Əsər ${n}`,
        image: null,
        pdfPages: 0,
        material: [],
        vote: { position: n, tieSize: 1, validVotes: 10 - n, invalidatedVotes: n === 1 ? 171 : 0 },
      })),
      jurors: [{ id: 1, label: 'Münsif A', submitted: false }],
    }
    juryAdminApi.print.mockResolvedValue(print)
    at('?doc=catalog')
    await screen.findByText(/3 iş · 1 səhifə/)
    expect(voteCatalogApi.catalog).not.toHaveBeenCalled()
    const pages = document.querySelector('.pr-pages')!.textContent ?? ''
    expect(pages).toContain('171 etibarsız sayılıb')
    expect(pages).not.toMatch(/SUB-|D12|D14|\bR2\b|telefon|koordinasiya/)
    expect([...document.querySelectorAll('.jp-card__no')].map((x) => x.textContent)).toEqual(['İş №1', 'İş №2', 'İş №3'])
  })
})

describe('helpers', () => {
  it('names only the rules that apply, in the audit’s order', () => {
    expect(breakdownText({ duplicatePhone: 0, coordinatedAccounts: 3, disposableEmail: 2 })).toBe('R2 3 · D14 2')
    expect(breakdownText(none)).toBe('')
  })

  it('keeps the server’s vote order or sorts by reference, after filtering', () => {
    const w = catalog().works
    expect(orderWorks(w, 'votes', false).map((x) => x.validVotes).slice(0, 2)).toEqual([200, 183])
    expect(orderWorks(w, 'reference', false)[0].reference).toBe('SUB-2026-00001')
    expect(orderWorks(w, 'votes', true).map((x) => x.reference)).toEqual(['SUB-2026-00010', 'SUB-2026-00006'])
    // The server's list is not changed in place.
    expect(w[0].reference).toBe('SUB-2026-00012')
  })
})
