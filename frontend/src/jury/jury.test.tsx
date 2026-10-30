import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import { PlatformContext } from '../platform/context'
import { platformDefaults } from '../platform/defaults'
import { juryImageUrl, tokenFromHash, type JurorDashboard, type JurorSession, type JurorWork } from './api'

const juryApi = vi.hoisted(() => ({
  signIn: vi.fn(),
  signOut: vi.fn(),
  session: vi.fn(),
  confirm: vi.fn(),
  dashboard: vi.fn(),
  work: vi.fn(),
  score: vi.fn(),
  submit: vi.fn(),
}))
vi.mock('./api', async (original) => ({ ...(await original<typeof import('./api')>()), juryApi }))

const { JuryStartPage } = await import('./JuryShell')
const { JuryEvaluatePage } = await import('./JuryEvaluate')

const session = (over: Partial<JurorSession> = {}): JurorSession => ({
  jurorName: 'Aysel Məmmədova',
  competitionId: 7,
  competitionTitle: 'Əmircanın simvolu',
  confirmed: true,
  locked: false,
  evaluationStatus: 'Open',
  ...over,
})

const work = (over: Partial<JurorWork> = {}): JurorWork => ({
  submissionId: 12,
  reference: 'SUB-2026-00012',
  title: 'Günəşli qapı',
  category: null,
  facts: [],
  conceptLabel: 'Konsepsiya',
  concept: 'Kəndin qapısı və günəşi.',
  image: { fileId: 5, large: true, width: 1600, height: 1200, page: 2, pages: 4 },
  pdfPages: 4,
  material: [
    { fileId: 5, kind: 'pdf', page: 1, pages: 4 },
    { fileId: 5, kind: 'pdf', page: 2, pages: 4 },
  ],
  vote: { position: 7, tieSize: 2, validVotes: 50, invalidatedVotes: 4 },
  index: 12,
  total: 99,
  previousId: 11,
  nextId: 13,
  score: null,
  comment: null,
  version: null,
  locked: false,
  ...over,
})

const dashboard = (over: Partial<JurorDashboard> = {}): JurorDashboard => ({
  jurorName: 'Aysel Məmmədova',
  competitionId: 7,
  competitionTitle: 'Əmircanın simvolu',
  total: 3,
  scored: 1,
  remaining: 2,
  locked: false,
  submittedAt: null,
  open: true,
  works: [
    // Most valid votes first, as the server orders them: #3 leads, #1 lost 162 votes to an audit decision.
    { submissionId: 3, reference: 'SUB-2026-00003', title: 'Üç', image: null, vote: { position: 1, tieSize: 1, validVotes: 200, invalidatedVotes: 0 }, score: null, hasComment: false },
    { submissionId: 1, reference: 'SUB-2026-00001', title: 'Bir', image: null, vote: { position: 2, tieSize: 1, validVotes: 173, invalidatedVotes: 162 }, score: 8, hasComment: false },
    { submissionId: 2, reference: 'SUB-2026-00002', title: 'İki', image: null, vote: { position: 3, tieSize: 1, validVotes: 11, invalidatedVotes: 0 }, score: null, hasComment: false },
  ],
  ...over,
})

function renderAt(path: string) {
  return render(
    <PlatformContext.Provider value={platformDefaults}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/jury/start" element={<JuryStartPage />} />
          <Route path="/jury/evaluate/:submissionId?" element={<JuryEvaluatePage />} />
        </Routes>
      </MemoryRouter>
    </PlatformContext.Provider>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  sessionStorage.clear()
  window.history.replaceState(null, '', '/')
})

describe('the invitation', () => {
  it('reads the secret from the fragment only', () => {
    expect(tokenFromHash('#t=abc-DEF_123')).toBe('abc-DEF_123')
    expect(tokenFromHash('#x=1')).toBeNull()
    expect(tokenFromHash('')).toBeNull()
  })

  it('takes the secret out of the address bar, asks for the code, then for the name to be confirmed', async () => {
    window.history.replaceState(null, '', '/jury/start#t=secret-from-link')
    juryApi.session.mockRejectedValue(new ApiError(401, 'Münsif kimi daxil olun.'))
    juryApi.signIn.mockResolvedValue(session({ confirmed: false }))
    juryApi.confirm.mockResolvedValue(session())
    juryApi.dashboard.mockResolvedValue(dashboard())
    renderAt('/jury/start')

    const code = await screen.findByLabelText('Giriş kodu')
    // The secret is no longer in the address and the history entry was replaced.
    expect(window.location.hash).toBe('')
    expect(window.location.href).not.toContain('secret-from-link')

    const go = screen.getByRole('button', { name: 'Daxil ol' })
    expect(go).toBeDisabled()
    await userEvent.type(code, '123 456')
    await userEvent.click(go)
    expect(juryApi.signIn).toHaveBeenCalledWith('secret-from-link', '123 456')

    expect(await screen.findByText('Aysel Məmmədova')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Adınızı təsdiqləyin' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Bəli, bu mənəm' }))
    expect(juryApi.confirm).toHaveBeenCalled()
    // The secret is not kept once used.
    expect(sessionStorage.length).toBe(0)
  })

  it('a wrong code is said plainly; "not me" closes the session without scoring anything', async () => {
    window.history.replaceState(null, '', '/jury/start#t=s')
    juryApi.session.mockRejectedValue(new ApiError(401, 'x'))
    juryApi.signIn.mockRejectedValueOnce(new ApiError(401, 'Giriş kodu səhvdir.'))
    renderAt('/jury/start')
    await userEvent.type(await screen.findByLabelText('Giriş kodu'), '000000')
    await userEvent.click(screen.getByRole('button', { name: 'Daxil ol' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Giriş kodu səhvdir.')

    juryApi.signIn.mockResolvedValueOnce(session({ confirmed: false }))
    juryApi.signOut.mockResolvedValue(undefined)
    await userEvent.click(screen.getByRole('button', { name: 'Daxil ol' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Xeyr, bu mən deyiləm' }))
    expect(juryApi.signOut).toHaveBeenCalled()
    expect(await screen.findByText(/Heç bir bal verilmədi/)).toBeInTheDocument()
  })

  it('without a link there is nothing to open', async () => {
    juryApi.session.mockRejectedValue(new ApiError(401, 'x'))
    renderAt('/jury/start')
    expect(await screen.findByRole('heading', { name: 'Dəvət linki lazımdır' })).toBeInTheDocument()
  })
})

describe('the list of works', () => {
  it('shows progress, each work’s public vote in the server’s order and the juror’s own scores — nothing more', async () => {
    juryApi.session.mockResolvedValue(session())
    juryApi.dashboard.mockResolvedValue(dashboard())
    renderAt('/jury/evaluate')
    expect(await screen.findByText('/ 3 iş qiymətləndirilib')).toBeInTheDocument()
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '1')
    expect(screen.getByRole('link', { name: 'Növbəti qiymətləndirilməmiş iş' })).toHaveAttribute('href', '/jury/evaluate/3')
    expect(screen.getByText(/İşlər xalq səsverməsinin etibarlı səslərinə görə sıralanıb/)).toBeInTheDocument()
    // Informed: place, short number, valid and set-aside counts, the bar — in the server's order.
    const cards = [...document.querySelectorAll('.jr-card')]
    expect(cards.map((c) => c.querySelector('.jr-card__pos')?.textContent)).toEqual(['1-ci yer', '2-ci yer', '3-cü yer'])
    expect(cards.map((c) => c.querySelector('.jr-card__ref')?.textContent)).toEqual(['İş №3', 'İş №1', 'İş №2'])
    expect(cards[1].querySelector('.jr-card__votes')?.textContent).toBe('173 etibarlı · 162 etibarsız')
    expect((cards[1].querySelector('.vbar__invalid') as HTMLElement).style.flexGrow).toBe('162')
    expect(cards[0].querySelector('.vbar__invalid')).toBeNull()
    // No long reference, rule, voter, average or ranking of the jury.
    const text = document.body.textContent ?? ''
    expect(text).not.toMatch(/SUB-|D12|D14|\bR2\b|telefon|reytinq|orta bal|xal\b/i)
    await userEvent.click(screen.getByRole('button', { name: 'Qiymətləndirilməyib (2)' }))
    expect(screen.queryByText('Bir')).not.toBeInTheDocument()
    expect(screen.getByText('İki')).toBeInTheDocument()
  })

  it('finishing asks first, then locks', async () => {
    juryApi.session.mockResolvedValue(session())
    const all = dashboard({ scored: 3, remaining: 0, works: dashboard().works.map((w) => ({ ...w, score: 5 })) })
    juryApi.dashboard.mockResolvedValue(all)
    juryApi.submit.mockResolvedValue({ ...all, locked: true, submittedAt: '2026-10-09T10:00:00Z' })
    renderAt('/jury/evaluate')
    await userEvent.click(await screen.findByRole('button', { name: 'Qiymətləndirməni yekunlaşdır' }))
    expect(screen.getByRole('alertdialog')).toHaveTextContent('ballar kilidlənir')
    await userEvent.click(screen.getByRole('button', { name: 'Bəli, yekunlaşdır' }))
    expect(await screen.findByText(/yekunlaşdırılıb və kilidlənib/)).toBeInTheDocument()
  })

  it('an unconfirmed juror is sent back to confirm', async () => {
    juryApi.session.mockResolvedValue(session({ confirmed: false }))
    juryApi.signIn.mockResolvedValue(session())
    render(
      <PlatformContext.Provider value={platformDefaults}>
        <MemoryRouter initialEntries={['/jury/evaluate']}>
          <Routes>
            <Route path="/jury/start" element={<p>start</p>} />
            <Route path="/jury/evaluate/:submissionId?" element={<JuryEvaluatePage />} />
          </Routes>
        </MemoryRouter>
      </PlatformContext.Provider>,
    )
    expect(await screen.findByText('start')).toBeInTheDocument()
  })
})

describe('scoring one work', () => {
  it('saves a click at once on the stored version, and keys 1–9 and 0 (= 10) score', async () => {
    juryApi.session.mockResolvedValue(session())
    juryApi.work.mockResolvedValue(work({ score: 6, version: 41 }))
    juryApi.score.mockImplementation((_id: number, score: number) =>
      Promise.resolve({ submissionId: 12, score, comment: null, version: 42 + score, scored: 10, total: 99 }),
    )
    renderAt('/jury/evaluate/12')
    const seven = await screen.findByRole('radio', { name: '7' })
    expect(screen.getByRole('radio', { name: '6' })).toHaveAttribute('aria-checked', 'true')
    await userEvent.click(seven)
    await waitFor(() => expect(juryApi.score).toHaveBeenCalledWith(12, 7, null, 41))
    expect(await screen.findByText(/Yadda saxlanıldı/)).toBeInTheDocument()

    // The next save goes on the version the first one returned.
    fireEvent.keyDown(window, { key: '0' })
    await waitFor(() => expect(juryApi.score).toHaveBeenLastCalledWith(12, 10, null, 49))
    expect(screen.getByRole('radio', { name: '10' })).toHaveAttribute('aria-checked', 'true')
  })

  it('a save another device overtook is refused and offered for reload — never retried over it', async () => {
    juryApi.session.mockResolvedValue(session())
    juryApi.work.mockResolvedValue(work({ score: 6, version: 41 }))
    juryApi.score.mockRejectedValue(new ApiError(409, 'Bu iş üçün bal başqa yerdən dəyişdirilib. Səhifəni yeniləyin.'))
    renderAt('/jury/evaluate/12')
    await userEvent.click(await screen.findByRole('radio', { name: '9' }))
    expect(await screen.findByText(/başqa yerdən dəyişdirilib/)).toBeInTheDocument()
    expect(juryApi.score).toHaveBeenCalledTimes(1)
    juryApi.work.mockResolvedValue(work({ score: 8, version: 50 }))
    await userEvent.click(screen.getByRole('button', { name: 'Ən son balı yüklə' }))
    await waitFor(() => expect(screen.getByRole('radio', { name: '8' })).toHaveAttribute('aria-checked', 'true'))
  })

  it('shows every page of the material and the work’s public vote — nothing of the author or the jury’s standing', async () => {
    juryApi.session.mockResolvedValue(session())
    juryApi.work.mockResolvedValue(work())
    renderAt('/jury/evaluate/12')
    expect(await screen.findByRole('heading', { name: 'Günəşli qapı' })).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /PDF səhifə/ })).toHaveLength(2)
    expect(document.querySelector('.jr-viewer__stage img')?.getAttribute('src')).toBe(juryImageUrl(12, 5, 1))
    expect(juryImageUrl(12, 5, 2, true)).toBe('/api/jury/works/12/files/5/pages/2?thumb=true')
    const vote = screen.getByRole('region', { name: 'Xalq səsverməsi' })
    expect(vote).toHaveTextContent('7-ci yer · bərabər (2 iş)')
    expect(vote).toHaveTextContent('50 etibarlı səs · 4 audit qərarı ilə etibarsız sayılıb')
    expect(screen.getByText('İş №12')).toBeInTheDocument()
    expect(screen.getByText(/12 \/ 99/)).toBeInTheDocument()
    const text = document.body.textContent ?? ''
    expect(text).not.toMatch(/SUB-|Müəllif|reytinq|D12|D14|\bR2\b|telefon/i)
  })

  it('a locked evaluation takes no score', async () => {
    juryApi.session.mockResolvedValue(session({ locked: true }))
    juryApi.work.mockResolvedValue(work({ score: 4, version: 3, locked: true }))
    renderAt('/jury/evaluate/12')
    expect(await screen.findByText('Qiymətləndirmə kilidlidir — bal dəyişmir.')).toBeInTheDocument()
    await act(async () => {
      fireEvent.keyDown(window, { key: '9' })
    })
    expect(juryApi.score).not.toHaveBeenCalled()
  })
})
