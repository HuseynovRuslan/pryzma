import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { CompetitionResult, CompetitionSummary, GalleryItem, PublicAward, Voting } from '../api/types'
import { lifecycleOf } from '../lib/competitionLifecycle'
import { FeaturedContext, type Featured } from '../lib/featuredCompetitionContext'

const catalog = vi.hoisted(() => ({ settings: vi.fn(), home: vi.fn(), competitions: vi.fn() }))
const votingApi = vi.hoisted(() => ({ leaderboard: vi.fn(), audit: vi.fn(), works: vi.fn() }))
const resultsApi = vi.hoisted(() => ({ list: vi.fn(), result: vi.fn() }))
vi.mock('../api/endpoints', () => ({ catalog, submissionsApi: {}, votingApi, resultsApi }))
vi.mock('../auth/AuthContext', () => ({ useAuth: () => ({ status: 'anonymous', user: null }) }))

const HomePage = (await import('./HomePage')).default

const NOW = Date.parse('2026-10-04T10:00:00+04:00')
const DAY = 24 * 60 * 60 * 1000

const competition = (over: Partial<CompetitionSummary> = {}): CompetitionSummary => ({
  id: 7,
  slug: 'emircanin-simvolu',
  title: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi',
  shortDescription: 'Əmircanın tarixi irsini müasir dillə əks etdirən açıq müsabiqə.',
  coverImage: null,
  status: 'Published',
  phase: 'Closed',
  startDate: new Date(NOW - 11 * DAY).toISOString(),
  endDate: new Date(NOW - DAY).toISOString(),
  hasPrize: true,
  hasCategories: false,
  categoryLabel: null,
  categoryCount: 0,
  project: { slug: 'abseron', name: 'Abşeronun simvolları', accentColor: '#8C6A3F' },
  resultsPublished: false,
  voting: 'NotConfigured',
  ...over,
})

const featured = (over: Partial<Featured> = {}): Featured => ({
  competition: null,
  voting: null,
  lifecycle: null,
  slides: [],
  ready: true,
  published: [],
  ...over,
})

const leadingWith = (c: CompetitionSummary): Featured => {
  const lifecycle = lifecycleOf(c, null, NOW)
  return featured({ competition: c, lifecycle, slides: [{ competition: c, voting: null, lifecycle }] })
}

const show = (value: Featured) =>
  render(
    <MemoryRouter>
      <FeaturedContext.Provider value={value}>
        <HomePage />
      </FeaturedContext.Provider>
    </MemoryRouter>,
  )

const EMPTY = 'Hazırda qəbulu açıq müsabiqə yoxdur'

describe('the open-calls section on the home page', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    catalog.home.mockResolvedValue({ projects: [], openCompetitions: [] })
  })

  it('steps aside when nothing is open and the hero is leading with a competition', async () => {
    // The morning after a deadline, and through the vote: the hero says what is happening, and an empty
    // "nothing is open" box between it and the works only pushes the works down.
    show(leadingWith(competition()))
    expect(await screen.findByRole('heading', { level: 1, name: 'Əmircanın simvolu' })).toBeInTheDocument()
    await screen.findByText(/nədir\?/)
    expect(screen.queryByText(EMPTY)).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Hazırda açıq müsabiqələr' })).not.toBeInTheDocument()
  })

  it('says nothing is open when nothing else is happening either', async () => {
    show(featured())
    expect(await screen.findByText(EMPTY)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Bitmiş müsabiqələrə bax' })).toBeInTheDocument()
  })

  it('does not flash the empty box before it knows whether the hero leads', async () => {
    show(featured({ ready: false }))
    await waitFor(() => expect(catalog.home).toHaveBeenCalled())
    expect(screen.queryByText(EMPTY)).not.toBeInTheDocument()
    // Nor anything else under the lead: it all appears at once when known, so nothing is pushed down the page.
    expect(screen.queryByText(/nədir\?/)).not.toBeInTheDocument()
    expect(document.querySelector('.home-pending')).not.toBeNull()
  })

  it('still lists open calls while the hero leads with something else', async () => {
    const call = competition({ id: 8, slug: 'susa', title: 'Şuşa çağırışı', phase: 'Open', endDate: new Date(NOW + 9 * DAY).toISOString() })
    catalog.home.mockResolvedValue({ projects: [], openCompetitions: [call] })
    show(leadingWith(competition()))
    expect(await screen.findByRole('heading', { name: 'Hazırda açıq müsabiqələr' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Şuşa çağırışı' })).toBeInTheDocument()
  })

  it("a card follows the hero's live stage, not the list read when the page opened", async () => {
    // The list was read while the vote was open; the window has since ended by the server's clock.
    const listed = competition({ voting: 'Open', phase: 'Open', endDate: new Date(NOW + 9 * DAY).toISOString() })
    catalog.home.mockResolvedValue({ projects: [], openCompetitions: [listed] })
    const voting = {
      competition: listed,
      state: 'Open',
      startsAt: new Date(NOW - 3 * DAY).toISOString(),
      endsAt: new Date(NOW - 1_000).toISOString(),
      nominations: [],
      workCount: 0,
      voteCount: 0,
      viewer: null,
      voterCount: 0,
      lastVoteAt: null,
    } as unknown as Voting
    const lifecycle = lifecycleOf(listed, voting, NOW)
    expect(lifecycle.stage).toBe('votingClosed')
    show(featured({ competition: listed, voting, lifecycle, slides: [{ competition: listed, voting, lifecycle }] }))
    expect(await screen.findByRole('heading', { name: 'Hazırda açıq müsabiqələr' })).toBeInTheDocument()
    expect(screen.getAllByText('Səsvermə başa çatıb').length).toBeGreaterThan(0)
    expect(screen.queryByText('Səsvermə açıqdır')).not.toBeInTheDocument()
  })

  it('keeps the section when the only competition led with is over', async () => {
    // A finished competition is not led with; with nothing open, the page needs the box to say where to look.
    const over = competition({ resultsPublished: true })
    const lifecycle = lifecycleOf(over, null, NOW)
    show(featured({ competition: over, lifecycle, slides: [{ competition: over, voting: null, lifecycle }] }))
    expect(await screen.findByText(EMPTY)).toBeInTheDocument()
  })
})

describe('two votes open at once', () => {
  const open = (slug: string, title: string) => {
    const c = competition({ slug, title, voting: 'Open', phase: 'Closed' })
    const voting = {
      competition: c,
      state: 'Open',
      startsAt: new Date(NOW - DAY).toISOString(),
      endsAt: new Date(NOW + 5 * DAY).toISOString(),
      nominations: [{ slug: 'butun-isler', name: 'Bütün işlər', workCount: 3, voteCount: 9 }],
      workCount: 3,
      voteCount: 9,
      viewer: null,
      voterCount: 9,
      lastVoteAt: null,
    } as unknown as Voting
    return { competition: c, voting, lifecycle: lifecycleOf(c, voting, NOW) }
  }
  const novxani = open('novxaninin-simvolu', '“Novxanının simvolu” yaradıcılıq müsabiqəsi')
  const mastaga = open('mastaganin-simvolu', '“Maştağanın simvolu” yaradıcılıq müsabiqəsi')

  beforeEach(() => {
    vi.clearAllMocks()
    catalog.home.mockResolvedValue({ projects: [], openCompetitions: [] })
    votingApi.works.mockResolvedValue({ state: 'Open', items: [], total: 0, page: 1, pageSize: 6, viewer: null })
    // Each ballot answers with its own works: a row of one can never be counted in the other's table.
    votingApi.leaderboard.mockImplementation((slug: string) =>
      Promise.resolve({
        state: 'Open',
        updatedAt: new Date(NOW).toISOString(),
        totalVotes: 9,
        myVoteReference: null,
        entries: [1, 2, 3].map((n) => ({
          rank: n,
          reference: `SUB-2026-${slug === novxani.competition.slug ? '001' : '002'}0${n}`,
          title: `${slug} ${n}`,
          voteCount: 5 - n,
          preview: null,
        })),
      }),
    )
  })

  it('shows each competition its own live ranking, named, with its own works below — never one table', async () => {
    expect(novxani.lifecycle.stage).toBe('votingLive')
    show(featured({ ...novxani, slides: [novxani, mastaga] }))

    const boards = await screen.findAllByRole('region', { name: 'Canlı reytinq' })
    expect(boards).toHaveLength(2)
    expect(within(boards[0]).getByText('«Novxanının simvolu» müsabiqəsi üzrə sıralama')).toBeInTheDocument()
    expect(within(boards[1]).getByText('«Maştağanın simvolu» müsabiqəsi üzrə sıralama')).toBeInTheDocument()
    // Each table's rows are its own works and open its own ballot.
    const opens = (board: HTMLElement) =>
      within(board).getAllByRole('link').map((a) => a.getAttribute('href')).filter((h) => h?.includes('/voting/'))
    await waitFor(() => expect(opens(boards[0])).toHaveLength(3))
    expect(opens(boards[0])).toEqual(['01', '02', '03'].map((n) => `/competitions/novxaninin-simvolu/voting/SUB-2026-001${n}`))
    expect(opens(boards[1])).toEqual(['01', '02', '03'].map((n) => `/competitions/mastaganin-simvolu/voting/SUB-2026-002${n}`))
    expect(within(boards[0]).getByRole('link', { name: 'İşlərə bax və səsini ver' })).toHaveAttribute('href', '#finalists-novxaninin-simvolu')
    expect(within(boards[1]).getByRole('link', { name: 'İşlərə bax və səsini ver' })).toHaveAttribute('href', '#finalists-mastaganin-simvolu')
    expect(votingApi.leaderboard).toHaveBeenCalledWith('novxaninin-simvolu', expect.anything())
    expect(votingApi.leaderboard).toHaveBeenCalledWith('mastaganin-simvolu', expect.anything())
  })
})

describe('the home page after the vote, before the result', () => {
  const closed = competition({ voting: 'Closed', phase: 'Closed' })
  const voting = {
    competition: closed,
    state: 'Closed',
    startsAt: new Date(NOW - 3 * DAY).toISOString(),
    endsAt: new Date(NOW - DAY).toISOString(),
    nominations: [],
    workCount: 12,
    voteCount: 1370,
    viewer: null,
    voterCount: 1370,
    lastVoteAt: null,
    resultsPublished: false,
  } as unknown as Voting

  beforeEach(() => {
    vi.clearAllMocks()
    catalog.home.mockResolvedValue({ projects: [], openCompetitions: [] })
    votingApi.leaderboard.mockResolvedValue({
      state: 'Closed',
      updatedAt: new Date(NOW).toISOString(),
      totalVotes: 1370,
      myVoteReference: null,
      entries: Array.from({ length: 12 }, (_, i) => ({
        rank: i + 1,
        reference: `SUB-2026-${String(100 + i).padStart(5, '0')}`,
        title: i === 0 ? 'Əmircanın ruhu' : null,
        voteCount: 300 - i * 20,
        preview: null,
      })),
    })
    votingApi.audit.mockResolvedValue({
      state: 'Closed',
      validVotes: 1370,
      invalidatedVotes: 256,
      totalVotes: 1626,
      entries: [],
      updatedAt: new Date(NOW).toISOString(),
    })
  })

  it('shows the review status and the provisional top ten on the page itself', async () => {
    const lifecycle = lifecycleOf(closed, voting, NOW)
    expect(lifecycle.stage).toBe('votingClosed')
    show(featured({ competition: closed, voting, lifecycle, slides: [{ competition: closed, voting, lifecycle }] }))

    expect(await screen.findByRole('heading', { name: 'Səslər yoxlanılır' })).toBeInTheDocument()
    expect(screen.getByText('Göstərilən sıralama etibarlı səslərə əsaslanan ilkin nəticədir.')).toBeInTheDocument()
    expect((await screen.findByText('Etibarlı səslər')).nextSibling).toHaveTextContent('1370')
    expect(screen.getByText('Etibarsız sayılmış səslər').nextSibling).toHaveTextContent('256')
    expect(screen.getByText('Ümumi səslər').nextSibling).toHaveTextContent('1626')

    const ranking = screen.getByRole('region', { name: 'İlkin reytinq' })
    expect(within(ranking).getByText('Bu sıralama audit davam etdiyi üçün dəyişə bilər.')).toBeInTheDocument()
    // That it is not the final result is said once, by the status right above — not again here.
    expect(ranking.textContent).not.toContain('Yekun nəticələr')
    // Ten rows, open — no fold, no "show all" toggle in the way.
    const rows = await within(ranking).findAllByRole('listitem')
    expect(rows).toHaveLength(10)
    expect(within(ranking).queryByRole('button')).not.toBeInTheDocument()
    expect(rows[0]).toHaveTextContent('1')
    expect(rows[0]).toHaveTextContent('SUB-2026-00100')
    expect(rows[0]).toHaveTextContent('Əmircanın ruhu')
    expect(rows[0]).toHaveTextContent('300 səs')
    expect(rows[9]).toHaveTextContent('SUB-2026-00109')
    expect(within(ranking).queryByText('SUB-2026-00110')).not.toBeInTheDocument()
    // The rest is one optional step away.
    expect(within(ranking).getByRole('link', { name: 'Bütün işlərə bax' })).toHaveAttribute(
      'href',
      '/competitions/emircanin-simvolu/voting#works',
    )
    // Nothing here calls anything a result.
    expect(screen.queryByText(/Yekun reytinq|Qalib:/)).not.toBeInTheDocument()
  })

  it('says the review status once: summary, then status, then ranking', async () => {
    const lifecycle = lifecycleOf(closed, voting, NOW)
    show(featured({ competition: closed, voting, lifecycle, slides: [{ competition: closed, voting, lifecycle }] }))
    const status = await screen.findByRole('heading', { name: 'Səslər yoxlanılır' })
    await screen.findAllByRole('listitem')

    const text = document.body.textContent ?? ''
    const times = (s: string) => text.split(s).length - 1
    expect(times('Səslər yoxlanılır')).toBe(1)
    expect(times('Göstərilən sıralama etibarlı səslərə əsaslanan ilkin nəticədir.')).toBe(1)
    expect(times('Yekun nəticələr ayrıca elan ediləcək.')).toBe(1)
    // D16: the jury's evaluation is internal — the public page neither names it nor ties the result to it.
    expect(text).not.toMatch(/münsif|dəyərləndir/i)
    expect(times('İlkin nəticələr')).toBe(1)

    // The hero keeps the stage, the name, the steps, the dates and one quiet way to the works.
    const hero = document.querySelector('.stage')!
    expect(within(hero as HTMLElement).getByText('Səsvermə mərhələsi başa çatdı')).toBeInTheDocument()
    expect(hero.querySelector('.stage__rail')!.children).toHaveLength(4)
    expect(within(hero as HTMLElement).getByText('Əsas tarixlər:')).toBeInTheDocument()
    expect(within(hero as HTMLElement).getByRole('link', { name: 'İşlərə bax' })).toHaveAttribute(
      'href',
      '/competitions/emircanin-simvolu/voting',
    )
    expect(hero.querySelector('.stage__panel')).toBeNull()

    const ranking = screen.getByRole('region', { name: 'İlkin reytinq' })
    const follows = (a: Node, b: Node) => Boolean(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING)
    expect(follows(hero, status)).toBe(true)
    expect(follows(status, ranking)).toBe(true)
  })

  it('is not shown once the official result is published', async () => {
    const published = competition({ voting: 'Closed', phase: 'Closed', resultsPublished: true })
    const lifecycle = lifecycleOf(published, { ...voting, resultsPublished: true } as Voting, NOW)
    expect(lifecycle.stage).toBe('results')
    show(featured({ competition: published, voting, lifecycle, slides: [{ competition: published, voting, lifecycle }] }))
    await screen.findByText(/nədir\?/)
    expect(screen.queryByRole('region', { name: 'İlkin reytinq' })).not.toBeInTheDocument()
    expect(votingApi.leaderboard).not.toHaveBeenCalled()
  })
})

describe('the winners on the home page', () => {
  const work = (n: number, title: string): GalleryItem => {
    const reference = `SUB-2026-${String(n).padStart(5, '0')}`
    const url = `/api/competitions/emircanin-simvolu/gallery/${reference}/files/${n}`
    return {
      reference, title, category: null, isWinner: true, winnerName: null,
      preview: { url, thumbUrl: `${url}/thumb`, width: 1200, height: 1200 }, previewState: 'Ready', place: null,
    }
  }
  const award = (place: number, points: number, name: string, n: number, title: string): PublicAward => ({
    place, points, participantName: name, work: work(n, title),
  })
  // Whatever the API says: the page holds no names or numbers of its own.
  const result = (over: Partial<CompetitionResult> = {}): CompetitionResult => ({
    competition: {
      slug: 'emircanin-simvolu',
      title: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi',
      shortDescription: 'x',
      categoryLabel: null,
      hasCategories: false,
      project: { slug: 'abseron', name: 'Abşeronun simvolları', accentColor: '#8C6A3F' },
    },
    publishedAt: '2026-10-16T13:41:16Z',
    announcement: null,
    coverImage: null,
    entryCount: 99,
    winnerCount: 5,
    categories: [],
    overallWinner: null,
    places: [],
    final: null,
    awards: {
      jury: [
        award(1, 30, 'Kamal Məmmədli', 39, 'Qədim Əmircan loqotip konsepti'),
        award(2, 26, 'Məryəm Əhmədova', 61, 'Əmircan — daşda qalan tarix'),
        award(3, 16, 'Nərgiz Əsədova', 111, 'XİLƏDƏN ƏMİRCANA'),
      ],
      public: [
        award(3, 173, 'Səid Məmmədov', 96, 'Əmircanın Vizual Konsepsiyası'),
        award(1, 200, 'Şəbnəm Ələkbərli', 90, 'Əmircan loqosu'),
        award(2, 183, 'Məryəm Əhmədova', 61, 'Əmircan — daşda qalan tarix'),
      ],
    },
    ...over,
  })

  beforeEach(() => {
    vi.clearAllMocks()
    catalog.home.mockResolvedValue({ projects: [], openCompetitions: [] })
  })

  const places = (column: HTMLElement) =>
    within(column)
      .getAllByRole('listitem')
      .map((li) => [
        li.querySelector('.hw-place__name')?.textContent,
        li.querySelector('.hw-place__points')?.textContent,
        li.querySelector('.hw-place__ref')?.textContent,
        within(li).getByRole('link').getAttribute('href'),
      ])

  /** Settled: everything under the lead has appeared, the open-calls box with it. */
  const settled = async () => {
    await screen.findByText(EMPTY)
    expect(document.querySelector('.home-pending')).toBeNull()
  }

  it('shows both choices apart, every place, each linking to its work', async () => {
    resultsApi.result.mockResolvedValue(result())
    show(featured({ published: ['emircanin-simvolu'] }))

    const section = await screen.findByRole('region', { name: 'Qaliblər elan olundu!' })
    expect(within(section).getByText('Əmircanın simvolu')).toBeInTheDocument()
    const jury = within(section).getByRole('region', { name: 'Münsiflərin seçimi' })
    const pub = within(section).getByRole('region', { name: 'Xalqın seçimi' })
    expect(places(jury)).toEqual([
      ['Kamal Məmmədli', '30 bal', '#039', '/competitions/emircanin-simvolu/gallery/SUB-2026-00039'],
      ['Məryəm Əhmədova', '26 bal', '#061', '/competitions/emircanin-simvolu/gallery/SUB-2026-00061'],
      ['Nərgiz Əsədova', '16 bal', '#111', '/competitions/emircanin-simvolu/gallery/SUB-2026-00111'],
    ])
    // In place order, whatever order the API lists them in.
    expect(places(pub)).toEqual([
      ['Şəbnəm Ələkbərli', '200 səs', '#090', '/competitions/emircanin-simvolu/gallery/SUB-2026-00090'],
      ['Məryəm Əhmədova', '183 səs', '#061', '/competitions/emircanin-simvolu/gallery/SUB-2026-00061'],
      ['Səid Məmmədov', '173 səs', '#096', '/competitions/emircanin-simvolu/gallery/SUB-2026-00096'],
    ])
    // One link per place, named for a screen reader; the first place shows its work large, the others a thumbnail.
    expect(
      within(jury).getByRole('link', { name: 'Münsiflərin seçimi, 1-ci yer: Kamal Məmmədli — 30 bal, Qədim Əmircan loqotip konsepti' }),
    ).toBeInTheDocument()
    const firstPicture = jury.querySelector('.hw-place--1 img')
    expect(firstPicture).toHaveAttribute('src', '/api/competitions/emircanin-simvolu/gallery/SUB-2026-00039/files/39')
    expect(firstPicture).toHaveAttribute('loading', 'lazy')
    expect(jury.querySelector('.hw-place--2 img')).toHaveAttribute(
      'src',
      '/api/competitions/emircanin-simvolu/gallery/SUB-2026-00061/files/61/thumb',
    )
    expect(within(section).getByRole('link', { name: /Bütün nəticələr/ })).toHaveAttribute(
      'href',
      '/competitions/emircanin-simvolu/results',
    )

    // Never one ranking: no overall winner, no sum, no prize.
    expect(section.textContent).not.toMatch(/Ümumi qalib|50\/50|AZN|mükafat/i)
    // Only the one public result was asked for.
    expect(resultsApi.result).toHaveBeenCalledWith('emircanin-simvolu', expect.anything())
    expect(resultsApi.list).not.toHaveBeenCalled()
  })

  it('asks nothing on a day without a public result', async () => {
    show(featured())
    await settled()
    expect(screen.queryByRole('region', { name: 'Qaliblər elan olundu!' })).not.toBeInTheDocument()
    expect(resultsApi.result).not.toHaveBeenCalled()
    expect(resultsApi.list).not.toHaveBeenCalled()
  })

  it('waits for the competition list before asking', async () => {
    show(featured({ ready: false, published: ['emircanin-simvolu'] }))
    await waitFor(() => expect(catalog.home).toHaveBeenCalled())
    expect(resultsApi.result).not.toHaveBeenCalled()
  })

  it('shows the winners and the bands under them in one go, the winners first', async () => {
    let answer: (r: CompetitionResult) => void = () => {}
    resultsApi.result.mockReturnValue(new Promise<CompetitionResult>((resolve) => (answer = resolve)))
    show(featured({ published: ['emircanin-simvolu'] }))
    await waitFor(() => expect(resultsApi.result).toHaveBeenCalled())
    // While the result is read, the open calls and the static bands wait too.
    expect(screen.queryByText(/nədir\?/)).not.toBeInTheDocument()
    expect(screen.queryByText(EMPTY)).not.toBeInTheDocument()
    answer(result())
    const section = await screen.findByRole('region', { name: 'Qaliblər elan olundu!' })
    const box = screen.getByText(EMPTY)
    expect(Boolean(section.compareDocumentPosition(box) & Node.DOCUMENT_POSITION_FOLLOWING)).toBe(true)
  })

  it('with several public results, shows the one published last', async () => {
    resultsApi.list.mockResolvedValue([{ competition: { slug: 'newer' } }, { competition: { slug: 'emircanin-simvolu' } }])
    resultsApi.result.mockResolvedValue(result())
    show(featured({ published: ['emircanin-simvolu', 'newer'] }))
    await screen.findByRole('region', { name: 'Qaliblər elan olundu!' })
    expect(resultsApi.result).toHaveBeenCalledTimes(1)
    expect(resultsApi.result).toHaveBeenCalledWith('newer', expect.anything())
  })

  it('shows nothing for a result without the two choices', async () => {
    resultsApi.result.mockResolvedValue(result({ awards: null }))
    show(featured({ published: ['emircanin-simvolu'] }))
    await settled()
    expect(resultsApi.result).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('region', { name: 'Qaliblər elan olundu!' })).not.toBeInTheDocument()
  })

  it('leaves the page as it is when the result cannot be read', async () => {
    resultsApi.result.mockRejectedValue(new Error('offline'))
    show(featured({ published: ['emircanin-simvolu'] }))
    await settled()
    expect(screen.queryByRole('region', { name: 'Qaliblər elan olundu!' })).not.toBeInTheDocument()
    expect(screen.getByText(/nədir\?/)).toBeInTheDocument()
  })

  it('shrinks to one line while another competition is open, so the open calls come first', async () => {
    const call = competition({ id: 8, slug: 'mastaganin-simvolu', title: '“Maştağanın simvolu” yaradıcılıq müsabiqəsi', phase: 'Open', endDate: new Date(NOW + 7 * DAY).toISOString() })
    catalog.home.mockResolvedValue({ projects: [], openCompetitions: [call] })
    resultsApi.result.mockResolvedValue(result())
    show(featured({ published: ['emircanin-simvolu'] }))

    const strip = await screen.findByRole('region', { name: 'Əmircanın simvolu: qaliblər elan olundu' })
    expect(within(strip).getByRole('link', { name: /Nəticələrə bax/ })).toHaveAttribute('href', '/competitions/emircanin-simvolu/results')
    // Not the full podiums…
    expect(screen.queryByRole('region', { name: 'Qaliblər elan olundu!' })).not.toBeInTheDocument()
    expect(screen.queryByText('Kamal Məmmədli')).not.toBeInTheDocument()
    // …and the open calls right after it.
    const open = screen.getByRole('heading', { name: 'Hazırda açıq müsabiqələr' })
    expect(Boolean(strip.compareDocumentPosition(open) & Node.DOCUMENT_POSITION_FOLLOWING)).toBe(true)
  })

  it('shrinks to one line while the lead shows a competition being run', async () => {
    resultsApi.result.mockResolvedValue(result())
    const live = competition({ id: 8, slug: 'mastaganin-simvolu', voting: 'Open', phase: 'Closed' })
    const lifecycle = lifecycleOf(live, null, NOW)
    show(featured({ published: ['emircanin-simvolu'], competition: live, lifecycle, slides: [{ competition: live, voting: null, lifecycle }] }))
    expect(await screen.findByRole('region', { name: 'Əmircanın simvolu: qaliblər elan olundu' })).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Qaliblər elan olundu!' })).not.toBeInTheDocument()
  })
})
