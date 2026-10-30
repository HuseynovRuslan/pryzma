import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../api/client'
import type { Voting, VotingWork, VotingWorkDetail, VotingWorks } from '../../api/types'
import { voteCopy } from './lib'
import VotingListPage from './VotingListPage'
import VotingWorkPage from './VotingWorkPage'

const api = vi.hoisted(() => ({
  summary: vi.fn(),
  works: vi.fn(),
  work: vi.fn(),
  vote: vi.fn(),
  leaderboard: vi.fn(),
  audit: vi.fn(),
}))
vi.mock('../../api/endpoints', () => ({
  votingApi: {
    summary: api.summary,
    works: api.works,
    work: api.work,
    vote: api.vote,
    leaderboard: api.leaderboard,
    audit: api.audit,
  },
}))
const auth = vi.hoisted(() => ({ status: 'authenticated' as string }))
vi.mock('../../auth/AuthContext', () => ({
  useAuth: () => ({ status: auth.status, user: { firstName: 'Günel', lastName: 'Şükürova', roles: [] } }),
}))

beforeAll(() => {
  window.scrollTo = vi.fn()
  globalThis.ResizeObserver = class {
    observe() {}
    disconnect() {}
    unobserve() {}
  } as unknown as typeof ResizeObserver
  globalThis.IntersectionObserver = class {
    observe() {}
    disconnect() {}
    unobserve() {}
    takeRecords() {
      return []
    }
    root = null
    rootMargin = ''
    thresholds = []
  } as unknown as typeof IntersectionObserver
})
beforeEach(() => {
  vi.clearAllMocks()
  auth.status = 'authenticated'
})

function Where() {
  const l = useLocation()
  return <output data-testid="where">{l.pathname + l.search}</output>
}

const project = { slug: 'abseron', name: 'Abşeronun simvolları', accentColor: '#56652A' }
const competition = {
  slug: 'emircan',
  title: 'Əmircanın simvolu müsabiqəsi',
  shortDescription: 'x',
  categoryLabel: 'Nominasiyalar',
  hasCategories: false,
  project,
}

// The open window sits around the real clock, so the ballot is open whenever the suite runs: a fixed end date
// (20 October 2026, 21:00 UTC) closed it under every test the next morning.
const DAY = 86_400_000
// A vote that closes while a test watches: long enough for a slow CI machine to draw the open ballot first.
const WINDOW = 3_000
const opened = new Date(Date.now() - 20 * DAY).toISOString()
const closes = new Date(Date.now() + 20 * DAY).toISOString()

const nominations = [
  { slug: 'simvol-loqotip', name: 'Simvol və Loqotip Dizaynı', workCount: 2, voteCount: 5 },
  { slug: 'promo-mehsul', name: 'Promo Məhsul Tətbiqləri', workCount: 1, voteCount: 1 },
]

const work = (over: Partial<VotingWork> = {}): VotingWork => ({
  reference: 'SUB-2026-00001',
  title: 'Əmircanın Ruhu',
  nomination: { slug: 'simvol-loqotip', name: 'Simvol və Loqotip Dizaynı' },
  preview: null,
  previewState: 'None',
  voteCount: 3,
  eligibility: 'Allowed',
  publicConceptDescription: null,
  ...over,
})

const summary = (over: Partial<Voting> = {}): Voting => ({
  competition,
  state: 'Open',
  startsAt: opened,
  endsAt: closes,
  nominations,
  workCount: 3,
  voteCount: 6,
  viewer: { mayVote: true, reason: 'Allowed', votedNominations: [], votedWorks: [] },
  voterCount: 6,
  lastVoteAt: null,
  ...over,
})

const works = (items: VotingWork[], over: Partial<VotingWorks> = {}): VotingWorks => ({
  state: 'Open',
  items,
  total: items.length,
  page: 1,
  pageSize: 24,
  viewer: { mayVote: true, reason: 'Allowed', votedNominations: [], votedWorks: [] },
  ...over,
})

const detail = (over: Partial<VotingWorkDetail> = {}): VotingWorkDetail => ({
  work: work(),
  answers: [],
  files: [],
  state: 'Open',
  endsAt: closes,
  viewer: { mayVote: true, reason: 'Allowed', votedNominations: [], votedWorks: [] },
  ...over,
})

const renderList = (at = '/competitions/emircan/voting') =>
  render(
    <MemoryRouter initialEntries={[at]}>
      <Where />
      <Routes>
        <Route path="/competitions/:slug/voting" element={<VotingListPage />} />
      </Routes>
    </MemoryRouter>,
  )

const renderWork = () =>
  render(
    <MemoryRouter initialEntries={['/competitions/emircan/voting/SUB-2026-00001']}>
      <Routes>
        <Route path="/competitions/:slug/voting/:reference" element={<VotingWorkPage />} />
      </Routes>
    </MemoryRouter>,
  )

describe('the ballot', () => {
  it('shows each work with its nomination and count, and never an author', async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(
      works([
        work(),
        work({ reference: 'SUB-2026-00002', title: 'Tarixi Körpü', voteCount: 9 }),
        work({
          reference: 'SUB-2026-00003',
          title: 'Yaşıl Əmircan',
          nomination: { slug: 'promo-mehsul', name: 'Promo Məhsul Tətbiqləri' },
          voteCount: 1,
        }),
      ]),
    )
    renderList()

    // The header arrives with the summary; the grid follows with the works, so wait for a card before counting.
    await screen.findByRole('heading', { name: 'Əmircanın Ruhu' })
    const head = screen.getByRole('heading', { name: 'Səsvermə siyahısı', level: 1 }).closest('header')!
    expect(within(head).getByText('Əmircanın simvolu müsabiqəsi')).toBeInTheDocument()
    const cards = screen.getAllByRole('listitem').filter((li) => li.classList.contains('vcard'))
    expect(cards).toHaveLength(3)
    expect(within(cards[0]).getByRole('heading', { name: 'Əmircanın Ruhu' })).toBeInTheDocument()
    expect(within(cards[0]).getByText('Simvol və Loqotip Dizaynı')).toBeInTheDocument()
    // The work's number, in its short form and read from its own reference — nothing is invented to look like one.
    expect(within(cards[0]).getByText('İş #1')).toHaveAttribute('title', 'SUB-2026-00001')
    expect(within(cards[0]).getByRole('link', { name: 'Əmircanın Ruhu — işə bax' })).toHaveTextContent('İşə bax →')
    // The running total is live while the window is open — the organiser's decision, and what the hero says.
    expect(within(cards[0]).getByText(/\d+ səs/)).toBeInTheDocument()
    expect(within(cards[0]).getByRole('button', { name: 'Səs ver' })).toBeInTheDocument()

    // The author line of the design is gone, and so is any search over authors.
    expect(screen.queryByText(/Müəllif/)).not.toBeInTheDocument()
    expect(screen.getByLabelText('İşin adı üzrə axtar')).toBeInTheDocument()
    expect(screen.queryByText(/müəllif üzrə/i)).not.toBeInTheDocument()
  })

  it('shows the public concept on a card, and never a private answer', async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(
      works([
        work({ publicConceptDescription: 'Qala silüeti və zeytun budağı motivləri.' }),
        work({ reference: 'SUB-2026-00002', title: 'Tarixi Körpü', publicConceptDescription: null }),
      ]),
    )
    renderList()

    // Wait for a card, not just for any list item — the header's own lists resolve first.
    await screen.findByRole('heading', { name: 'Əmircanın Ruhu' })
    const cards = screen.getAllByRole('listitem').filter((li) => li.classList.contains('vcard'))
    expect(within(cards[0]).getByText('Qala silüeti və zeytun budağı motivləri.')).toBeInTheDocument()
    // A work whose field is not public simply has no line — never an empty box, never a placeholder.
    expect(cards[1].querySelector('.vcard__desc')).toBeNull()
    // Nothing a participant kept to themselves is in the payload the card renders from.
    const body = document.body.textContent ?? ''
    for (const priv of ['Simvollar', 'Şəxsi qeyd', 'Müəllif']) expect(body).not.toContain(priv)
  })

  it('publishes the running total while the window is open, and after it shuts', async () => {
    // The organiser's decision of 4 October 2026: counts are live. The hero says so, and the cards show them.
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(works([work({ voteCount: 12 })]))
    const open = renderList()
    expect(await screen.findByRole('heading', { name: 'Əmircanın Ruhu' })).toBeInTheDocument()
    expect(screen.getByText('12 səs')).toBeInTheDocument()
    open.unmount()

    api.summary.mockResolvedValue(summary({ state: 'Closed' }))
    api.works.mockResolvedValue(works([work({ voteCount: 12, eligibility: 'VotingNotOpen' })], { state: 'Closed' }))
    renderList()
    expect(await screen.findByText('12 səs')).toBeInTheDocument()
  })

  it('says when voting closes, and when it has closed', async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(works([work()]))
    const open = renderList()
    expect(await screen.findByText(/tarixinədək davam edir/)).toBeInTheDocument()
    open.unmount()

    api.summary.mockResolvedValue(summary({ state: 'Closed' }))
    api.works.mockResolvedValue(works([work({ eligibility: 'VotingNotOpen' })], { state: 'Closed' }))
    renderList()
    // After the window and before the result: the review stage heads the page; the works stay, unpressable.
    expect(await screen.findByRole('heading', { level: 1, name: 'Səslər yoxlanılır' })).toBeInTheDocument()
    expect(screen.getByText('İlkin nəticələr')).toBeInTheDocument()
    // The owner's three sentences (9 October 2026), word for word, in their order.
    for (const sentence of ['Səsvermə başa çatıb.', 'Göstərilən sıralama etibarlı səslərə əsaslanan ilkin nəticədir.', 'Yekun nəticələr ayrıca elan ediləcək.'])
      expect(screen.getByText(sentence)).toBeInTheDocument()
    expect(await screen.findByText('Bu müsabiqədə səsvermə yekunlaşıb.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Səs ver' })).not.toBeInTheDocument()
  })

  it('keeps the provisional ranking after the vote, with the real totals and the public audit lines', async () => {
    api.summary.mockResolvedValue(summary({ state: 'Closed' }))
    api.works.mockResolvedValue(works([work({ eligibility: 'VotingNotOpen' })], { state: 'Closed' }))
    api.leaderboard.mockResolvedValue({
      state: 'Closed',
      updatedAt: '2026-10-07T08:00:00Z',
      totalVotes: 300,
      myVoteReference: null,
      entries: [
        { rank: 1, reference: 'SUB-2026-00111', title: 'Birinci', voteCount: 200, preview: null },
        { rank: 2, reference: 'SUB-2026-00061', title: 'İkinci', voteCount: 100, preview: null },
      ],
    })
    api.audit.mockResolvedValue({
      state: 'Closed',
      validVotes: 1370,
      invalidatedVotes: 256,
      totalVotes: 1626,
      updatedAt: '2026-10-07T08:00:00Z',
      entries: [
        { at: '2026-10-06T07:17:33Z', votes: 123, reason: 'CoordinatedAccounts' },
        { at: '2026-10-05T18:20:17Z', votes: 95, reason: 'DuplicatePhone' },
      ],
    })
    renderList()

    expect(await screen.findByRole('heading', { level: 1, name: 'Səslər yoxlanılır' })).toBeInTheDocument()
    // The ranking is not hidden: it stays, labelled provisional, on valid votes.
    expect(await screen.findByRole('heading', { name: 'İlkin səs sıralaması' })).toBeInTheDocument()
    expect(screen.getByText('Bu sıralama audit davam etdiyi üçün dəyişə bilər.')).toBeInTheDocument()
    expect(screen.queryByText(/Yekun reytinq/)).not.toBeInTheDocument()

    // Real totals, from the server.
    const totals = await screen.findByLabelText('Səs sayları')
    expect(within(totals).getByText('Etibarlı səslər').nextSibling).toHaveTextContent('1370')
    expect(within(totals).getByText('Etibarsız sayılmış səslər').nextSibling).toHaveTextContent('256')
    expect(within(totals).getByText('Ümumi səslər').nextSibling).toHaveTextContent('1626')

    // One public line per decision, newest first, in Baku time and public words only.
    const updates = screen.getByRole('region', { name: 'Audit yeniləmələri' })
    const lines = within(updates).getAllByRole('listitem').map((li) => li.textContent)
    expect(lines[0]).toContain('6 oktyabr 2026, 11:17')
    expect(lines[0]).toContain('123 səs koordinasiyalı hesab klasteri qaydasına əsasən etibarsız sayıldı.')
    expect(lines[1]).toContain('5 oktyabr 2026, 22:20')
    expect(lines[1]).toContain('95 səs təkrarlanan telefon məlumatlarına görə etibarsız sayıldı.')
  })

  it('turns into the review stage at the end of the window, without a reload', async () => {
    const endsAt = new Date(Date.now() + WINDOW).toISOString()
    api.summary
      .mockResolvedValueOnce(summary({ endsAt }))
      .mockResolvedValue(summary({ state: 'Closed', endsAt }))
    api.works
      .mockResolvedValueOnce(works([work()]))
      .mockResolvedValue(works([work({ eligibility: 'VotingNotOpen' })], { state: 'Closed' }))
    renderList()
    expect(await screen.findByRole('button', { name: 'Səs ver' })).toBeInTheDocument()
    expect(screen.queryByText('Səslər yoxlanılır')).not.toBeInTheDocument()

    expect(await screen.findByRole('heading', { name: 'Səslər yoxlanılır' }, { timeout: WINDOW + 4_000 })).toBeInTheDocument()
    // The server was asked again at the boundary, and the cards with it.
    await waitFor(() => expect(api.summary).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(api.works).toHaveBeenCalledTimes(2))
    expect(screen.queryByRole('button', { name: 'Səs ver' })).not.toBeInTheDocument()
    expect(screen.getByTestId('where')).toHaveTextContent('/competitions/emircan/voting')
  })

  it('a lost answer at the end does not keep the vote open, nor claim a result', async () => {
    const endsAt = new Date(Date.now() + WINDOW).toISOString()
    api.summary.mockResolvedValueOnce(summary({ endsAt })).mockRejectedValue(new ApiError(0, 'Şəbəkə xətası'))
    api.works.mockResolvedValue(works([work()]))
    renderList()
    expect(await screen.findByRole('button', { name: 'Səs ver' })).toBeInTheDocument()

    // The server's own end has passed: the review stage, on the strength of the window alone.
    expect(await screen.findByRole('heading', { name: 'Səslər yoxlanılır' }, { timeout: WINDOW + 4_000 })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Səs ver' })).not.toBeInTheDocument()
    expect(screen.getAllByText('Səsvermə başa çatıb').length).toBeGreaterThan(0)
    // Nothing about a result, and no error page for a failed background ask.
    expect(screen.getByTestId('where')).toHaveTextContent('/competitions/emircan/voting')
    expect(screen.queryByText(/Nəticələr açıqlanıb/)).not.toBeInTheDocument()
    expect(screen.queryByText('Şəbəkə xətası')).not.toBeInTheDocument()
  })

  it('goes to the result once it is officially published, and not before', async () => {
    api.summary.mockResolvedValue(summary({ state: 'Closed', resultsPublished: true }))
    api.works.mockResolvedValue(works([work({ eligibility: 'VotingNotOpen' })], { state: 'Closed' }))
    renderList()
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/competitions/emircan/results'))
    expect(screen.queryByText('Səslər yoxlanılır')).not.toBeInTheDocument()
  })

  it('refuses nothing itself — every button says what the server said', async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(
      works([
        work({ reference: 'SUB-1', eligibility: 'OwnWork' }),
        work({ reference: 'SUB-2', eligibility: 'AlreadyVotedInNomination' }),
        work({ reference: 'SUB-4', eligibility: 'AlreadyVotedForThisWork' }),
      ]),
    )
    renderList()

    expect(await screen.findByText('Öz işinizə səs verə bilməzsiniz.')).toBeInTheDocument()
    expect(screen.getByText('Səsinizi artıq vermisiniz')).toBeInTheDocument()
    expect(screen.getByText('Müsabiqədə yalnız bir işə səs verə bilərsiniz.')).toBeInTheDocument()
    expect(screen.getByText('Səsiniz qeydə alındı ✓')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Səs ver' })).not.toBeInTheDocument()
    // Nobody is refused for when their account was opened (D11 was lifted).
    expect(screen.queryByText(/hesablar səs verə bilmir/)).not.toBeInTheDocument()
  })

  it('sends a signed-out visitor to sign in, and remembers where they were', async () => {
    api.summary.mockResolvedValue(summary({ viewer: null }))
    api.works.mockResolvedValue(works([work({ eligibility: 'NotSignedIn' })], { viewer: null }))
    renderList('/competitions/emircan/voting?nomination=simvol-loqotip')

    const link = await screen.findByRole('link', { name: 'Səs vermək üçün daxil olun' })
    expect(link).toHaveAttribute(
      'href',
      '/login?next=' + encodeURIComponent('/competitions/emircan/voting?nomination=simvol-loqotip'),
    )
  })

  it('filters by nomination and keeps the search term on the address', async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(works([work()]))
    renderList('/competitions/emircan/voting?q=ruh')

    const chip = await screen.findByRole('link', { name: /Promo Məhsul Tətbiqləri/ })
    await userEvent.click(chip)
    await waitFor(() =>
      expect(screen.getByTestId('where')).toHaveTextContent('/competitions/emircan/voting?q=ruh&nomination=promo-mehsul'),
    )
    await waitFor(() =>
      expect(api.works).toHaveBeenCalledWith('emircan', expect.objectContaining({ nomination: 'promo-mehsul', q: 'ruh' }), expect.anything()),
    )
  })

  it("asks for the works in this visitor's own shuffle, and keeps it across visits", async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(works([work()]))
    const { unmount } = renderList()
    await waitFor(() => expect(api.works).toHaveBeenCalled())
    const seed = api.works.mock.calls[0][1].seed
    expect(Number.isInteger(seed) && seed > 0).toBe(true)
    expect(window.localStorage.getItem('pryzma.ballot-seed')).toBe(String(seed))
    unmount()

    api.works.mockClear()
    renderList('/competitions/emircan/voting?q=ruh')
    await waitFor(() => expect(api.works).toHaveBeenCalledWith('emircan', expect.objectContaining({ q: 'ruh', seed }), expect.anything()))
  })

  it('offers a returning voter the invitation, folded away, and shows the live count once it means something', async () => {
    const voted = { mayVote: true, reason: 'Allowed' as const, votedNominations: ['simvol-loqotip'], votedWorks: ['SUB-2026-00001'] }
    api.summary.mockResolvedValue(summary({ viewer: voted, voterCount: 25, lastVoteAt: new Date(Date.now() - 3 * 60_000).toISOString() }))
    api.works.mockResolvedValue(works([work({ eligibility: 'AlreadyVotedForThisWork' })], { viewer: voted }))
    renderList()

    expect(await screen.findByText(/nəfər artıq səs verib/)).toHaveTextContent('25 nəfər artıq səs verib. Sonuncu səs 3 dəqiqə əvvəl verildi.')
    const invite = screen.getByText('Səsiniz qeydə alınıb. Dostlarınızı da səs verməyə çağırın').closest('details')!
    expect(invite).not.toHaveAttribute('open')
    await userEvent.click(screen.getByText('Səsiniz qeydə alınıb. Dostlarınızı da səs verməyə çağırın'))
    expect(invite).toHaveAttribute('open')
    expect(within(invite).getByRole('link', { name: 'WhatsApp-da göndər' })).toBeInTheDocument()
  })

  it('says nothing about the count while it is small, and invites nobody who has not voted', async () => {
    api.summary.mockResolvedValue(summary({ voterCount: 4 }))
    api.works.mockResolvedValue(works([work()]))
    renderList()
    await screen.findAllByRole('button', { name: 'Səs ver' })
    expect(screen.queryByText(/nəfər artıq səs verib/)).not.toBeInTheDocument()
    expect(screen.queryByText(/Dostlarınızı da səs verməyə çağırın/)).not.toBeInTheDocument()
  })

  it('has an empty state of its own, and an error that can be retried', async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(works([]))
    const empty = renderList()
    expect(await screen.findByText('Nəticə tapılmadı')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Bütün işlərə bax' })).toBeInTheDocument()
    empty.unmount()

    api.works.mockRejectedValue(new ApiError(500, 'Server cavab vermədi.'))
    renderList()
    expect(await screen.findByRole('heading', { name: 'Səhifə yüklənmədi' })).toBeInTheDocument()
    // The header survives the grid failing: the window and the filters are still there.
    expect(screen.getByRole('heading', { name: 'Səsvermə siyahısı', level: 1 })).toBeInTheDocument()
  })

  it('answers a competition with no vote with its own page, not a blank grid', async () => {
    api.summary.mockRejectedValue(new ApiError(404, 'Səsvermə hələ başlamayıb.'))
    api.works.mockRejectedValue(new ApiError(404, 'Səsvermə hələ başlamayıb.'))
    renderList()

    expect(await screen.findByRole('heading', { name: 'Səsvermə açıq deyil' })).toBeInTheDocument()
    expect(screen.getByText('Səsvermə hələ başlamayıb.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Müsabiqəyə qayıt' })).toHaveAttribute('href', '/competitions/emircan')
  })

  it('asks for nothing until it knows who is asking', async () => {
    // A page opened straight from its address is anonymous for as long as the session takes to come back.
    // Fetching then would tell a signed-in person to sign in, and nothing on the page would correct it.
    auth.status = 'loading'
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(works([work()]))
    const { rerender } = renderList()

    expect(api.summary).not.toHaveBeenCalled()
    expect(api.works).not.toHaveBeenCalled()
    expect(screen.getByText(/Səsvermə yüklənir/)).toBeInTheDocument()

    auth.status = 'authenticated'
    rerender(
      <MemoryRouter initialEntries={['/competitions/emircan/voting']}>
        <Where />
        <Routes>
          <Route path="/competitions/:slug/voting" element={<VotingListPage />} />
        </Routes>
      </MemoryRouter>,
    )
    expect(await screen.findByRole('heading', { name: 'Əmircanın Ruhu' })).toBeInTheDocument()
    expect(api.works).toHaveBeenCalledTimes(1)
  })
})

describe('casting a vote', () => {
  it('confirms first, says the vote is final, and only then sends it', async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(works([work(), work({ reference: 'SUB-2026-00002', title: 'Tarixi Körpü' })]))
    api.vote.mockResolvedValue({
      reference: 'SUB-2026-00001',
      nominationSlug: 'simvol-loqotip',
      voteCount: 4,
      castAt: '2026-10-05T10:00:00Z',
    })
    renderList()

    await userEvent.click((await screen.findAllByRole('button', { name: 'Səs ver' }))[0])
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('heading', { name: 'Səs vermək istəyirsiniz?' })).toBeInTheDocument()
    expect(within(dialog).getByText('Əmircanın Ruhu')).toBeInTheDocument()
    // The dialog names no category: choosing one is not a vote of its own.
    expect(within(dialog).queryByText(/Nominasiya:/)).not.toBeInTheDocument()
    expect(within(dialog).getByText('Müsabiqədə yalnız bir işə səs verə bilərsiniz. Səs bir dəfə verilir və geri alınmır.')).toBeInTheDocument()
    expect(api.vote).not.toHaveBeenCalled()

    await userEvent.click(within(dialog).getByRole('button', { name: 'Səsi təsdiqlə' }))
    expect(api.vote).toHaveBeenCalledWith('emircan', 'SUB-2026-00001')

    // The dialog becomes the receipt, and offers the invitation — which never says which work was chosen.
    expect(await within(dialog).findByRole('heading', { name: 'Səsiniz qeydə alındı' })).toHaveFocus()
    const message = dialog.querySelector('.vshare__message')!
    expect(message).toHaveTextContent(/«Əmircanın simvolu» müsabiqəsində səs verdim\. Sən də bəyəndiyin işə səs ver\./)
    expect(message).toHaveTextContent('/competitions/emircan/voting')
    expect(message).not.toHaveTextContent('Ruhu')
    expect(message).not.toHaveTextContent('SUB-2026-00001')
    const whatsapp = within(dialog).getByRole('link', { name: 'WhatsApp-da göndər' })
    expect(decodeURIComponent(whatsapp.getAttribute('href')!)).toMatch(/^https:\/\/wa\.me\/\?text=.+müsabiqəsində səs verdim.+\/competitions\/emircan\/voting$/)
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Linki kopyala' }))
    expect(writeText).toHaveBeenCalledWith(`${window.location.origin}/competitions/emircan/voting`)
    expect(await within(dialog).findByRole('button', { name: 'Link kopyalandı' })).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bağla' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    // The receipt appears on the work it names, with the count the server returned after the insert; the
    // other works close: one vote per competition.
    const cards = screen.getAllByRole('listitem').filter((li) => li.classList.contains('vcard'))
    expect(cards).toHaveLength(2)
    expect(within(cards[0]).getByText('Səsiniz qeydə alındı ✓')).toBeInTheDocument()
    expect(within(cards[0]).getByText(/\d+ səs/)).toBeInTheDocument()
    expect(within(cards[1]).getByText('Səsinizi artıq vermisiniz')).toBeInTheDocument()
  })

  it('cancels without sending anything', async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(works([work()]))
    renderList()

    await userEvent.click(await screen.findByRole('button', { name: 'Səs ver' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Ləğv et' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(api.vote).not.toHaveBeenCalled()
  })

  it('shows the server’s refusal in the dialog and asks the page again', async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(works([work()]))
    api.vote.mockRejectedValue(new ApiError(409, 'Bu nominasiyada artıq səs vermisiniz.'))
    renderList()

    await userEvent.click(await screen.findByRole('button', { name: 'Səs ver' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Səsi təsdiqlə' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Bu nominasiyada artıq səs vermisiniz.')
    await waitFor(() => expect(api.works).toHaveBeenCalledTimes(2))
  })
})

describe('one work', () => {
  it('shows a document whole — every page in order, as pictures — not only its cover', async () => {
    const base = '/api/competitions/emircan/voting/works/SUB-2026-00001/files/7'
    const page = (p: number) => ({
      id: 7, kind: 'document-page' as const, url: `${base}/pages/${p}`, thumbUrl: `${base}/pages/${p}/thumb`,
      width: null, height: null, name: `Fayl 1, ${p}. səhifə`, page: p,
    })
    api.work.mockResolvedValue(
      detail({
        files: [
          { id: 7, kind: 'document-preview', url: base, thumbUrl: `${base}/thumb`, width: 1131, height: 1600, name: 'Fayl 1' },
          page(2),
          page(3),
        ],
      }),
    )
    api.summary.mockResolvedValue(summary())
    renderWork()

    expect(await screen.findByText(/Sənədin 3 səhifəsinin hamısı aşağıdadır/)).toBeInTheDocument()
    expect(screen.getByRole('img', { name: 'Əmircanın Ruhu — sənədin birinci səhifəsi' })).toHaveAttribute('src', base)
    expect(screen.getByRole('img', { name: 'Əmircanın Ruhu — sənədin 2. səhifəsi' })).toHaveAttribute('src', `${base}/pages/2`)
    expect(screen.getByRole('img', { name: 'Əmircanın Ruhu — sənədin 3. səhifəsi' })).toHaveAttribute('src', `${base}/pages/3`)
    expect(screen.getAllByText(/^\d\. səhifə$/).map((e) => e.textContent)).toEqual(['2. səhifə', '3. səhifə'])
    expect(screen.queryByText(/Sənədin birinci səhifəsi \(önizləmə\)/)).not.toBeInTheDocument()
  })

  it('shows the work, its live count and the public answers — and no author', async () => {
    api.work.mockResolvedValue(
      detail({
        answers: [{ label: 'Konsept təsviri', fieldType: 'LongText', value: 'Qala silüeti və zeytun budağı.' }],
        files: [
          {
            id: 7,
            kind: 'image',
            url: '/api/competitions/emircan/voting/works/SUB-2026-00001/files/7',
            thumbUrl: '/api/competitions/emircan/voting/works/SUB-2026-00001/files/7/thumb',
            width: 1200,
            height: 800,
            name: 'Fayl 1',
          },
        ],
      }),
    )
    api.summary.mockResolvedValue(summary())
    renderWork()

    // The panel: the sticky bar repeats the name, the nomination and the count on purpose, so this looks at one.
    const panel = (await screen.findByRole('heading', { name: 'Əmircanın Ruhu', level: 1 })).closest<HTMLElement>('.vwork__panel')!
    expect(within(panel).getByText('Simvol və Loqotip Dizaynı')).toBeInTheDocument()
    expect(within(panel).getByText('SUB-2026-00001')).toBeInTheDocument()
    // Counts are live while the window is open — the same rule as the ballot and the home page.
    expect(within(panel).getByText('Səs sayı')).toBeInTheDocument()
    expect(within(panel).getByText('3 səs')).toBeInTheDocument()
    expect(screen.getByText('Konsept təsviri')).toBeInTheDocument()
    expect(screen.getByText('Qala silüeti və zeytun budağı.')).toBeInTheDocument()
    expect(screen.getByRole('img')).toHaveAttribute('src', '/api/competitions/emircan/voting/works/SUB-2026-00001/files/7')
    expect(screen.queryByText(/Müəllif|İştirakçı:/)).not.toBeInTheDocument()
  })

  it('records a vote in place and says so', async () => {
    api.work.mockResolvedValue(detail())
    api.summary.mockResolvedValue(summary())
    api.vote.mockResolvedValue({
      reference: 'SUB-2026-00001',
      nominationSlug: 'simvol-loqotip',
      voteCount: 4,
      castAt: '2026-10-05T10:00:00Z',
    })
    renderWork()

    await userEvent.click((await screen.findAllByRole('button', { name: 'Səs ver' }))[0])
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Səsi təsdiqlə' }))

    await screen.findByRole('heading', { name: 'Səsiniz qeydə alındı' })
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    // The number the server returned after the insert, so it includes this vote.
    expect(within(document.querySelector<HTMLElement>('.vwork__panel')!).getByText('4 səs')).toBeInTheDocument()
    expect(screen.getByText('Səsiniz qeydə alındı ✓')).toBeInTheDocument()
    expect(screen.getByText('Səs verdiniz')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Səs ver' })).not.toBeInTheDocument()
  })

  it('keeps what the server says after a reload — the state is never the browser’s own', async () => {
    api.work.mockResolvedValue(
      detail({
        work: work({ eligibility: 'AlreadyVotedForThisWork', voteCount: 4 }),
        viewer: { mayVote: true, reason: 'Allowed', votedNominations: ['simvol-loqotip'], votedWorks: ['SUB-2026-00001'] },
      }),
    )
    api.summary.mockResolvedValue(summary())
    renderWork()

    expect(await screen.findByText('Səsiniz qeydə alındı ✓')).toBeInTheDocument()
    expect(screen.getByText('Səs verdiniz')).toBeInTheDocument()
    expect(within(document.querySelector<HTMLElement>('.vwork__panel')!).getByText('4 səs')).toBeInTheDocument()
    expect(api.vote).not.toHaveBeenCalled()
  })

  it('shows the count once the window has shut', async () => {
    api.work.mockResolvedValue(detail({ work: work({ eligibility: 'VotingNotOpen', voteCount: 3 }) }))
    api.summary.mockResolvedValue(summary({ state: 'Closed' }))
    renderWork()

    const panel = (await screen.findByRole('heading', { name: 'Əmircanın Ruhu', level: 1 })).closest<HTMLElement>('.vwork__panel')!
    expect(within(panel).getByText('Səs sayı')).toBeInTheDocument()
    expect(within(panel).getByText('3 səs')).toBeInTheDocument()
  })
})

describe('one vote for the whole competition, whatever the category', () => {
  const loqo = { slug: 'simvol-loqotip', name: 'Simvol və Loqotip Dizaynı' }
  const promo = { slug: 'promo-mehsul', name: 'Promo Məhsul Tətbiqləri' }
  const voted = { mayVote: false, reason: 'AlreadyVotedInNomination' as const, votedNominations: [loqo.slug], votedWorks: ['SUB-2026-00001'] }

  it('closes every other work — in every category — the moment the vote is in', async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(
      works([
        work({ nomination: loqo }),
        work({ reference: 'SUB-2026-00002', title: 'Tarixi Körpü', nomination: loqo }),
        work({ reference: 'SUB-2026-00003', title: 'Yaşıl Əmircan', nomination: promo }),
      ]),
    )
    api.vote.mockResolvedValue({ reference: 'SUB-2026-00001', nominationSlug: loqo.slug, voteCount: 4, castAt: '2026-10-05T10:00:00Z' })
    renderList()

    await userEvent.click((await screen.findAllByRole('button', { name: 'Səs ver' }))[0])
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Səsi təsdiqlə' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Bağla' }))

    const cards = screen.getAllByRole('listitem').filter((li) => li.classList.contains('vcard'))
    expect(within(cards[0]).getByText('Səsiniz qeydə alındı ✓')).toBeInTheDocument()
    // The same category and another category alike: no second vote is offered anywhere.
    expect(within(cards[1]).getByText('Səsinizi artıq vermisiniz')).toBeInTheDocument()
    expect(within(cards[2]).getByText('Səsinizi artıq vermisiniz')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Səs ver' })).not.toBeInTheDocument()
  })

  it('keeps the vote spent when the category filter changes — the server says so for every category', async () => {
    api.summary.mockResolvedValue(summary({ viewer: voted }))
    api.works.mockImplementation(async (_slug: string, q: { nomination?: string | null }) =>
      q.nomination === promo.slug
        ? works([work({ reference: 'SUB-2026-00003', title: 'Yaşıl Əmircan', nomination: promo, eligibility: 'AlreadyVotedInNomination' })], { viewer: voted })
        : works([work({ nomination: loqo, eligibility: 'AlreadyVotedForThisWork' })], { viewer: voted }),
    )
    renderList()
    expect(await screen.findByText('Səsiniz qeydə alındı ✓')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('link', { name: /Promo Məhsul Tətbiqləri/ }))
    expect(await screen.findByRole('heading', { name: 'Yaşıl Əmircan' })).toBeInTheDocument()
    expect(api.works).toHaveBeenLastCalledWith('emircan', expect.objectContaining({ nomination: promo.slug }), expect.anything())
    expect(screen.getByText('Səsinizi artıq vermisiniz')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Səs ver' })).not.toBeInTheDocument()
    // The filter is a filter: "all" is named as such, and nothing in it speaks of a vote per category.
    expect(screen.getByRole('link', { name: /Hamısı/ })).toBeInTheDocument()
  })

  it('reads the spent vote back from the server after a reload — not from the browser', async () => {
    window.localStorage.clear()
    api.summary.mockResolvedValue(summary({ viewer: voted }))
    api.works.mockResolvedValue(
      works(
        [
          work({ nomination: loqo, eligibility: 'AlreadyVotedForThisWork' }),
          work({ reference: 'SUB-2026-00003', title: 'Yaşıl Əmircan', nomination: promo, eligibility: 'AlreadyVotedInNomination' }),
        ],
        { viewer: voted },
      ),
    )
    renderList()
    expect(await screen.findByText('Səsiniz qeydə alındı ✓')).toBeInTheDocument()
    expect(screen.getByText('Səsinizi artıq vermisiniz')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Səs ver' })).not.toBeInTheDocument()
  })

  it('never speaks of a vote per nomination or per category, anywhere on the ballot', async () => {
    api.summary.mockResolvedValue(summary())
    api.works.mockResolvedValue(works([work({ nomination: loqo }), work({ reference: 'SUB-2026-00003', title: 'Yaşıl Əmircan', nomination: promo })]))
    const { container } = renderList()
    await userEvent.click((await screen.findAllByRole('button', { name: 'Səs ver' }))[0])
    await screen.findByRole('dialog')

    const text = `${container.textContent} ${document.querySelector('[role="dialog"]')?.textContent}`
    expect(text).not.toMatch(/nominasiyada|hər nominasiya|Nominasiya:|layihələrə səs verin/i)
    expect(text).toMatch(/bəyəndiyiniz bir işi seçib səs verin/)
    expect(Object.values(voteCopy).map((c) => `${c.label} ${c.note ?? ''}`).join(' ')).not.toMatch(/nominasiya/i)
  })
})

describe('a competition voted on as one list', () => {
  const onePool = [{ slug: 'umumi', name: 'Ümumi', workCount: 3, voteCount: 0 }]

  it('speaks of one vote, not of nominations, on the ballot', async () => {
    api.summary.mockResolvedValue(summary({ nominations: onePool }))
    api.works.mockResolvedValue(works([work({ nomination: { slug: 'umumi', name: 'Ümumi' } })]))
    renderList()

    expect(await screen.findByText(/Müsabiqədə yalnız bir işə səs verə bilərsiniz\./)).toBeInTheDocument()
    expect(screen.queryByText(/Hər nominasiyada/)).not.toBeInTheDocument()
    // A filter with one chip only repeats the total; the card names no nomination it shares with every other.
    expect(screen.queryByRole('navigation', { name: /üzrə süz/ })).not.toBeInTheDocument()
    await screen.findByRole('heading', { name: 'Əmircanın Ruhu' })
    expect(screen.queryByText('Ümumi')).not.toBeInTheDocument()
  })

  it('says before the vote is cast that it is the only one', async () => {
    api.work.mockResolvedValue(detail({ work: work({ nomination: { slug: 'umumi', name: 'Ümumi' } }) }))
    api.summary.mockResolvedValue(summary({ nominations: onePool }))
    renderWork()

    expect(await screen.findByText('Müsabiqədə yalnız bir işə səs verə bilərsiniz.')).toBeInTheDocument()
    await userEvent.click((await screen.findAllByRole('button', { name: 'Səs ver' }))[0])
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/Müsabiqədə yalnız bir işə səs verə bilərsiniz/)).toBeInTheDocument()
    expect(within(dialog).queryByText(/Nominasiya:/)).not.toBeInTheDocument()
  })

  it('tells someone who has voted that their vote is spent, not a nomination', async () => {
    api.work.mockResolvedValue(
      detail({ work: work({ reference: 'SUB-2026-00002', eligibility: 'AlreadyVotedInNomination', nomination: { slug: 'umumi', name: 'Ümumi' } }) }),
    )
    api.summary.mockResolvedValue(summary({ nominations: onePool }))
    renderWork()

    expect(await screen.findByText('Səsinizi artıq vermisiniz')).toBeInTheDocument()
    expect(screen.queryByText(/Bu nominasiyada/)).not.toBeInTheDocument()
  })
})
