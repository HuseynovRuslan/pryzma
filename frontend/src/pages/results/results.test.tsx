import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../api/client'
import type { CompetitionDetail, CompetitionResult, Gallery, GalleryItem, MySubmission, ResultListItem } from '../../api/types'
import MySubmissionsPage from '../account/MySubmissionsPage'
import CompetitionDetailPage from '../CompetitionDetailPage'
import ResultsPage from '../ResultsPage'
import CompetitionResultsPage from './CompetitionResultsPage'
import GalleryPage from './GalleryPage'
import GalleryWorkPage from './GalleryWorkPage'

const api = vi.hoisted(() => ({ result: vi.fn(), gallery: vi.fn(), work: vi.fn(), list: vi.fn(), competition: vi.fn(), mine: vi.fn() }))
vi.mock('../../api/endpoints', () => ({
  resultsApi: { result: api.result, gallery: api.gallery, work: api.work, list: api.list },
  catalog: { competition: api.competition },
  meApi: { submissions: api.mine },
}))
vi.mock('../../auth/AuthContext', () => ({
  useAuth: () => ({ status: 'authenticated', user: { firstName: 'Günel', lastName: 'Şükürova', roles: [] } }),
}))

beforeAll(() => {
  window.scrollTo = vi.fn()
  Element.prototype.scrollIntoView = vi.fn()
  globalThis.ResizeObserver = class {
    observe() {}
    disconnect() {}
    unobserve() {}
  } as unknown as typeof ResizeObserver
})
beforeEach(() => vi.clearAllMocks())

function Where() {
  const l = useLocation()
  return <output data-testid="where">{l.pathname + l.search}</output>
}

const project = { slug: 'baki-kendleri', name: 'Bakı kəndləri', accentColor: '#56652A' }
const competition = {
  slug: 'sevron',
  title: 'Bakı kəndləri üçün şevron dizayn müsabiqəsi',
  shortDescription: 'x',
  categoryLabel: 'Qəsəbələr',
  hasCategories: true,
  project,
}

const settlements = ['Maştağa', 'Şüvəlan', 'Əmircan', 'Nardaran', 'Bilgəh', 'Buzovna', 'Mərdəkan', 'Kürdəxanı', 'Zirə', 'Türkan']
const slugOf = (n: string) =>
  ({ Maştağa: 'mastaga', Şüvəlan: 'suvelan', Əmircan: 'emircan', Kürdəxanı: 'kurdexani', Mərdəkan: 'merdekan', Türkan: 'turkan', Zirə: 'zire', Bilgəh: 'bilgeh' })[n] ??
  n.toLowerCase()
const ref = (i: number) => `SUB-2026-${String(i + 1).padStart(5, '0')}`
const url = (i: number) => `/api/competitions/sevron/gallery/${ref(i)}/files/${i + 1}`
const winners: Record<number, string> = { 0: 'Leyla Quliyeva', 2: 'Kamran Babayev' }

const item = (n: string, i: number): GalleryItem => ({
  reference: ref(i),
  title: `${n} işi`,
  category: { slug: slugOf(n), name: n },
  isWinner: i in winners,
  winnerName: winners[i] ?? null,
  preview: i === 3 ? null : { url: url(i), thumbUrl: `${url(i)}/thumb`, width: 1254, height: 1254 },
  previewState: i === 3 ? 'Processing' : 'Ready',
  place: null,
})

const gallery: Gallery = {
  competition,
  publishedAt: '2026-09-18T12:00:00Z',
  total: settlements.length,
  winnerCount: 2,
  categories: settlements.map((n) => ({ slug: slugOf(n), name: n, count: 1 })),
  items: settlements.map(item),
}

const result: CompetitionResult = {
  competition,
  publishedAt: '2026-09-18T12:00:00Z',
  announcement: 'Təbrik edirik!',
  coverImage: '/api/media/covers/2026/09/urra.webp',
  entryCount: 10,
  winnerCount: 2,
  categories: settlements.map((n, i) => ({ slug: slugOf(n), name: n, entryCount: i === 9 ? 0 : 1, winner: i in winners ? item(n, i) : null })),
  overallWinner: null,
  places: [],
}

const renderAt = (path: string, route: string, element: React.ReactNode) =>
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route
          path={route}
          element={
            <>
              {element}
              <Where />
            </>
          }
        />
      </Routes>
    </MemoryRouter>,
  )

const cards = (container: HTMLElement) => [...container.querySelectorAll<HTMLElement>('.wcard')]

describe('public results page (a winner per settlement)', () => {
  it("shows each settlement's winner: the work whole, the winner's name, the work and its reference", async () => {
    api.result.mockResolvedValue(result)
    const { container } = renderAt('/competitions/sevron/results', '/competitions/:slug/results', <CompetitionResultsPage />)
    expect(await screen.findByRole('heading', { name: 'Qəsəbələr üzrə qaliblər' })).toBeInTheDocument()
    expect(cards(container)).toHaveLength(2)
    const mastaga = cards(container)[0]
    expect(within(mastaga).getByText('Qalib')).toBeInTheDocument()            // the badge is text, not colour alone
    expect(within(mastaga).getByText('Maştağa')).toBeInTheDocument()
    expect(within(mastaga).getByText('Maştağa işi')).toBeInTheDocument()
    expect(within(mastaga).getByText('Leyla Quliyeva')).toBeInTheDocument()
    expect(within(mastaga).getByText('SUB-2026-00001')).toBeInTheDocument()
    expect(within(mastaga).getByRole('img', { name: 'Maştağa üzrə qalib iş — Maştağa işi' })).toHaveAttribute('src', `${url(0)}/thumb`)
    expect(within(mastaga).getByRole('link', { name: /Maştağa üzrə qalib iş: Maştağa işi/ })).toHaveAttribute('href', '/competitions/sevron/gallery/SUB-2026-00001')
    // No single "the winner" of the whole competition.
    expect(screen.queryByRole('heading', { name: 'Qalib', level: 2 })).not.toBeInTheDocument()
    expect(screen.getByText('Təbrik edirik!')).toBeInTheDocument()
    expect(screen.getByRole('img', { name: `${competition.title} — nəticələr` })).toHaveAttribute('src', result.coverImage)
    expect(screen.getByRole('link', { name: 'İşlərə bax' })).toHaveAttribute('href', '/competitions/sevron/gallery')
  })

  it('finds a settlement with a tolerant search and lists the ones without a winner neutrally', async () => {
    api.result.mockResolvedValue(result)
    const { container } = renderAt('/competitions/sevron/results', '/competitions/:slug/results', <CompetitionResultsPage />)
    const search = await screen.findByLabelText('Qəsəbə axtar')
    await userEvent.type(search, 'emircan')
    expect(cards(container)).toHaveLength(1)
    expect(within(cards(container)[0]).getByText('Əmircan')).toBeInTheDocument()
    await userEvent.clear(search)

    await userEvent.click(screen.getByRole('radio', { name: /Qalibi olmayanlar/ }))
    expect(cards(container)).toHaveLength(0)
    const open = screen.getByRole('heading', { name: /Qalib hələ müəyyən edilməyib/ }).closest('div')!
    expect(within(open).getByText('Şüvəlan')).toBeInTheDocument()
    expect(within(open).getByRole('link', { name: 'Şüvəlan: 1 iş' })).toHaveAttribute('href', '/competitions/sevron/gallery?category=suvelan')
    expect(within(open).getByText('iş yoxdur')).toBeInTheDocument()            // Türkan: nothing submitted
    expect(within(open).queryByText('Maştağa')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('radio', { name: /Qalibi olanlar/ }))
    expect(cards(container)).toHaveLength(2)
    expect(screen.queryByRole('heading', { name: /Qalib hələ müəyyən edilməyib/ })).not.toBeInTheDocument()
  })

  it('a competition without categories shows its one winner', async () => {
    api.result.mockResolvedValue({ ...result, competition: { ...competition, hasCategories: false, categoryLabel: null }, categories: [], overallWinner: { ...item('Laçın', 0), category: null } })
    renderAt('/competitions/sevron/results', '/competitions/:slug/results', <CompetitionResultsPage />)
    expect(await screen.findByRole('heading', { name: 'Qalib', level: 2 })).toBeInTheDocument()
    expect(screen.getByText('Leyla Quliyeva')).toBeInTheDocument()
    expect(screen.queryByLabelText('Qəsəbə axtar')).not.toBeInTheDocument()
  })

  it('says the results are not out yet while the API refuses (404)', async () => {
    api.result.mockRejectedValue(new ApiError(404, 'Nəticələr hələ açıqlanmayıb.'))
    renderAt('/competitions/sevron/results', '/competitions/:slug/results', <CompetitionResultsPage />)
    expect(await screen.findByRole('heading', { name: 'Nəticələr hələ açıqlanmayıb' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Müsabiqəyə qayıt' })).toHaveAttribute('href', '/competitions/sevron')
  })
})

describe('results list', () => {
  const entry: ResultListItem = {
    competition,
    publishedAt: '2026-09-19T08:00:00Z',
    coverImage: '/api/media/covers/2026/09/urra.webp',
    coverKind: 'Result',
    categoryCount: 59,
    winnerCount: 17,
    entryCount: 120,
  }

  it('shows the results banner whole and the numbers, never one winner as "the" winner', async () => {
    api.list.mockResolvedValue([entry])
    const { container } = renderAt('/results', '/results', <ResultsPage />)
    const item = (await screen.findByRole('heading', { name: competition.title })).closest('li')!
    expect(item.querySelector('.rlist__visual--poster img')).toHaveAttribute('src', entry.coverImage)
    for (const [n, label] of [['59', 'qəsəbə'], ['17', 'qalib'], ['120', 'təqdim edilmiş iş']]) {
      const fact = within(item).getByText(label).closest('div')!
      expect(fact).toHaveTextContent(n)
    }
    expect(within(item).queryByText('Qalib')).not.toBeInTheDocument()
    expect(within(item).getByRole('link', { name: 'Nəticələrə bax' })).toHaveAttribute('href', '/competitions/sevron/results')
    expect(within(item).getByRole('link', { name: 'İşlərə bax' })).toHaveAttribute('href', '/competitions/sevron/gallery')
    expect(container.querySelector('.motif')).toBeNull()
  })

  it('without any cover falls back to the project pattern', async () => {
    api.list.mockResolvedValue([{ ...entry, coverImage: null, coverKind: null }])
    const { container } = renderAt('/results', '/results', <ResultsPage />)
    await screen.findByRole('heading', { name: competition.title })
    expect(container.querySelector('.rlist__visual .motif')).not.toBeNull()
  })
})

describe('public gallery', () => {
  it('is locked before the result is published', async () => {
    api.gallery.mockRejectedValue(new ApiError(404, 'Nəticələr hələ açıqlanmayıb.'))
    renderAt('/competitions/sevron/gallery', '/competitions/:slug/gallery', <GalleryPage />)
    expect(await screen.findByRole('heading', { name: 'Nəticələr hələ açıqlanmayıb' })).toBeInTheDocument()
    expect(screen.queryByRole('list')).not.toBeInTheDocument()
  })

  it('shows every work the same way, the real previews, winners marked and named — nobody else', async () => {
    api.gallery.mockResolvedValue(gallery)
    const { container } = renderAt('/competitions/sevron/gallery', '/competitions/:slug/gallery', <GalleryPage />)
    expect(await screen.findByText('Maştağa işi')).toBeInTheDocument()
    const all = [...container.querySelectorAll<HTMLElement>('.gcard')]
    expect(all).toHaveLength(10)
    const won = [...container.querySelectorAll<HTMLElement>('.gcard--winner')]
    expect(won).toHaveLength(2)
    expect(within(won[0]).getByText('Qalib')).toBeInTheDocument()
    expect(within(won[0]).getByText('Leyla Quliyeva')).toBeInTheDocument()
    expect(within(won[0]).getByRole('link')).toHaveAttribute('href', '/competitions/sevron/gallery/SUB-2026-00001')
    // The real work, card-size copy — not a decorative placeholder.
    expect(all[1].querySelector('img')).toHaveAttribute('src', `${url(1)}/thumb`)
    expect(all[1].querySelector('.motif, .art-state')).toBeNull()
    expect(within(all[1]).queryByText(/Quliyeva|Babayev/)).not.toBeInTheDocument()
    // A preview still being made says so.
    expect(within(all[3]).getByText('Önizləmə hazırlanır')).toBeInTheDocument()
    expect(api.gallery).toHaveBeenCalledTimes(1)
  })

  it('filters by category with the tolerant search and the label from the competition', async () => {
    api.gallery.mockResolvedValue(gallery)
    const { container } = renderAt('/competitions/sevron/gallery', '/competitions/:slug/gallery', <GalleryPage />)
    const search = await screen.findByLabelText('Qəsəbələr arasında axtar')
    expect(screen.getByRole('link', { name: /Bütün qəsəbələr/ })).toHaveAttribute('aria-current', 'true')

    for (const [q, name] of [['mastaga', 'Maştağa'], ['suvelan', 'Şüvəlan'], ['emircan', 'Əmircan'], ['kurdexani', 'Kürdəxanı']]) {
      await userEvent.clear(search)
      await userEvent.type(search, q)
      const chips = container.querySelectorAll('.gchip')
      // "Bütün qəsəbələr" + exactly the one matching settlement.
      expect(chips).toHaveLength(2)
      expect(chips[1]).toHaveTextContent(name)
    }

    await userEvent.clear(search)
    await userEvent.type(search, 'mastaga')
    await userEvent.click(screen.getByRole('link', { name: /^Maştağa/ }))
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('category=mastaga'))
    expect(container.querySelectorAll('.gcard')).toHaveLength(1)
    expect(screen.getByRole('link', { name: /^Maştağa/ })).toHaveAttribute('aria-current', 'true')
    expect(api.gallery).toHaveBeenCalledTimes(1) // filtering does not refetch or lose the search text
    expect(search).toHaveValue('mastaga')
  })

  it('uses the competition label, never a hardcoded "qəsəbə"', async () => {
    api.gallery.mockResolvedValue({
      ...gallery,
      competition: { ...competition, categoryLabel: 'Nominasiyalar' },
      categories: gallery.categories.slice(0, 2),
    })
    renderAt('/competitions/sevron/gallery', '/competitions/:slug/gallery', <GalleryPage />)
    expect(await screen.findByRole('link', { name: /Bütün nominasiyalar/ })).toBeInTheDocument()
    expect(screen.queryByText(/qəsəbə/i)).not.toBeInTheDocument()
  })
})

describe('competition page after entries close', () => {
  const detail = (over: Partial<CompetitionDetail>): CompetitionDetail => ({
    id: 1,
    slug: 'sevron',
    title: competition.title,
    shortDescription: 'x',
    description: null,
    rules: null,
    requirements: null,
    nominations: null,
    judgingCriteria: null,
    usageTerms: null,
    contactNote: null,
    coverImage: null,
    status: 'Closed',
    phase: 'Closed',
    isAcceptingSubmissions: false,
    startDate: null,
    endDate: null,
    resultsAnnouncedOn: null,
    exhibitionOn: null,
    prize: null,
    hasCategories: false,
    categoryLabel: null,
    maxSubmissionsPerUser: null,
    categories: [],
    project,
    resultsPublishedAt: null,
    voting: 'NotConfigured',
    ...over,
  })

  it('open: offers to submit', async () => {
    api.competition.mockResolvedValue(detail({ status: 'Published', phase: 'Open', isAcceptingSubmissions: true }))
    renderAt('/competitions/sevron', '/competitions/:slug', <CompetitionDetailPage />)
    expect((await screen.findAllByRole('button', { name: 'İştirak et' })).length).toBeGreaterThan(0)
    expect(screen.queryByText('Nəticələr açıqlanıb')).not.toBeInTheDocument()
  })

  it('closed, result private: "Qəbul başa çatıb" and "Nəticələr hazırlanır", no links', async () => {
    api.competition.mockResolvedValue(detail({}))
    renderAt('/competitions/sevron', '/competitions/:slug', <CompetitionDetailPage />)
    expect(await screen.findByRole('heading', { name: 'Qəbul başa çatıb' })).toBeInTheDocument()
    expect(screen.getAllByText(/Nəticələr hazırlanır/).length).toBeGreaterThan(0)
    expect(screen.queryByRole('link', { name: 'Nəticələrə bax' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'İştirak et' })).not.toBeInTheDocument()
  })

  it('results published: "Nəticələr açıqlanıb" with links to results and works', async () => {
    api.competition.mockResolvedValue(detail({ resultsPublishedAt: '2026-09-18T12:00:00Z' }))
    renderAt('/competitions/sevron', '/competitions/:slug', <CompetitionDetailPage />)
    expect(await screen.findByRole('heading', { name: 'Nəticələr açıqlanıb' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Nəticələrə bax' })).toHaveAttribute('href', '/competitions/sevron/results')
    expect(screen.getByRole('link', { name: 'İşlərə bax' })).toHaveAttribute('href', '/competitions/sevron/gallery')
  })

  it('speaks of winners (plural) when every category has its own, of one winner otherwise', async () => {
    api.competition.mockResolvedValue(detail({ resultsPublishedAt: '2026-09-18T12:00:00Z', hasCategories: true, categoryLabel: 'Qəsəbələr' }))
    const { unmount } = renderAt('/competitions/sevron', '/competitions/:slug', <CompetitionDetailPage />)
    expect(await screen.findByText(/^Qaliblər 18 sentyabr 2026 tarixində elan olunub\./)).toBeInTheDocument()
    unmount()

    api.competition.mockResolvedValue(detail({ resultsPublishedAt: '2026-09-18T12:00:00Z' }))
    renderAt('/competitions/sevron', '/competitions/:slug', <CompetitionDetailPage />)
    expect(await screen.findByText(/^Qalib 18 sentyabr 2026 tarixində elan olunub\./)).toBeInTheDocument()
  })

  it('while the result is private: "qaliblər" for a competition with categories', async () => {
    api.competition.mockResolvedValue(detail({ hasCategories: true, categoryLabel: 'Qəsəbələr' }))
    renderAt('/competitions/sevron', '/competitions/:slug', <CompetitionDetailPage />)
    expect(await screen.findByText(/Açıqlananda qaliblər və işlər bu səhifədən görünəcək\./)).toBeInTheDocument()
  })
})

describe('competition poster', () => {
  const base: CompetitionDetail = {
    id: 3,
    slug: 'xezerin-nefesi-poster',
    title: 'Xəzərin nəfəsi — poster müsabiqəsi',
    shortDescription: 'x',
    description: null,
    rules: null,
    requirements: null,
    nominations: null,
    judgingCriteria: null,
    usageTerms: null,
    contactNote: null,
    coverImage: null,
    status: 'Published',
    phase: 'Open',
    isAcceptingSubmissions: true,
    startDate: null,
    endDate: null,
    resultsAnnouncedOn: null,
    exhibitionOn: null,
    prize: null,
    hasCategories: false,
    categoryLabel: null,
    maxSubmissionsPerUser: null,
    categories: [],
    project: { slug: 'xezer-sahili', name: 'Xəzər sahili', accentColor: '#1F5F7A' },
    resultsPublishedAt: null,
    voting: 'NotConfigured',
  }

  it('shows the poster whole at the top when the competition has one (decorative: the title says it)', async () => {
    api.competition.mockResolvedValue({ ...base, coverImage: '/api/media/covers/2026/09/poster.webp' })
    const { container } = renderAt('/competitions/xezerin-nefesi-poster', '/competitions/:slug', <CompetitionDetailPage />)
    await screen.findByRole('heading', { level: 1, name: base.title })
    const poster = container.querySelector('.comp__head .comp__poster img')
    expect(poster).toHaveAttribute('src', '/api/media/covers/2026/09/poster.webp')
    expect(poster).toHaveAttribute('alt', '')
    expect(container.querySelector('.comp__head')).toHaveClass('comp__head--poster')
  })

  it('keeps the plain header without one', async () => {
    api.competition.mockResolvedValue(base)
    const { container } = renderAt('/competitions/xezerin-nefesi-poster', '/competitions/:slug', <CompetitionDetailPage />)
    await screen.findByRole('heading', { level: 1, name: base.title })
    expect(container.querySelector('.comp__poster')).toBeNull()
    expect(container.querySelector('.comp__head')).not.toHaveClass('comp__head--poster')
  })
})

describe('competition page content (nominations, calendar, contact)', () => {
  const base: CompetitionDetail = {
    id: 9,
    slug: 'novxani-ve-emircanin-simvollari',
    title: '“Novxanı və Əmircanın simvolları” yaradıcılıq müsabiqəsi',
    shortDescription: 'Qısa təsvir',
    description: 'Müsabiqə haqqında mətn.',
    rules: null,
    requirements: 'Rəssamlar və dizaynerlər müraciət edə bilər.',
    nominations: null,
    judgingCriteria: null,
    usageTerms: null,
    contactNote: null,
    coverImage: null,
    status: 'Published',
    phase: 'Open',
    isAcceptingSubmissions: true,
    startDate: null,
    endDate: null,
    resultsAnnouncedOn: null,
    exhibitionOn: null,
    prize: null,
    hasCategories: false,
    categoryLabel: null,
    maxSubmissionsPerUser: null,
    categories: [],
    project: { slug: 'abseronun-simvollari', name: 'Abşeronun simvolları', accentColor: '#8C6A3F' },
    resultsPublishedAt: null,
    voting: 'NotConfigured',
  }
  const render9 = () => renderAt('/competitions/novxani-ve-emircanin-simvollari', '/competitions/:slug', <CompetitionDetailPage />)

  it('gives every nomination its own name and explanation', async () => {
    api.competition.mockResolvedValue({
      ...base,
      nominations: 'Simvol və Loqotip Dizaynı\nSimvol konseptləri.\n\nPromo Məhsul Tətbiqləri\nSuvenir məhsulları.\n\n\n',
      judgingCriteria: null,
      usageTerms: null,
    })
    render9()

    expect(await screen.findByRole('heading', { name: 'Nominasiyalar' })).toBeInTheDocument()
    const items = screen.getAllByRole('listitem').filter((li) => li.className.includes('nominations__item'))
    expect(items).toHaveLength(2) // the trailing blank block adds nothing
    expect(within(items[0]).getByRole('heading', { name: 'Simvol və Loqotip Dizaynı' })).toBeInTheDocument()
    expect(within(items[0]).getByText('Simvol konseptləri.')).toBeInTheDocument()
    expect(within(items[1]).getByRole('heading', { name: 'Promo Məhsul Tətbiqləri' })).toBeInTheDocument()
  })

  it('shows only the dates that exist, and no calendar at all without any', async () => {
    api.competition.mockResolvedValue(base)
    const { unmount } = render9()
    await screen.findByRole('heading', { level: 1 })
    expect(screen.queryByRole('heading', { name: 'Təqvim' })).not.toBeInTheDocument()
    unmount()

    api.competition.mockResolvedValue({ ...base, endDate: '2026-11-15T19:59:00+00:00', resultsAnnouncedOn: '2026-11-25T08:00:00+00:00' })
    render9()

    expect(await screen.findByRole('heading', { name: 'Təqvim' })).toBeInTheDocument()
    expect(screen.getByText('Son təqdim tarixi')).toBeInTheDocument()
    expect(screen.getByText('Nəticələrin elanı')).toBeInTheDocument()
    expect(screen.queryByText('Sərgi')).not.toBeInTheDocument()
    expect(screen.queryByText(/Tarix qeyd edin/)).not.toBeInTheDocument()
  })

  it('shows the contact line only when the admin wrote one', async () => {
    api.competition.mockResolvedValue(base)
    const { unmount } = render9()
    await screen.findByRole('heading', { level: 1 })
    expect(screen.queryByRole('heading', { name: 'Əlaqə' })).not.toBeInTheDocument()
    unmount()

    api.competition.mockResolvedValue({ ...base, contactNote: 'Suallar üçün: 012 000 00 00' })
    render9()

    expect(await screen.findByRole('heading', { name: 'Əlaqə' })).toBeInTheDocument()
    expect(screen.getByText('Suallar üçün: 012 000 00 00')).toBeInTheDocument()
  })

  it('sets each prize place as its own entry, the amount first', async () => {
    api.competition.mockResolvedValue({
      ...base,
      prize: '1-ci yer — 500 AZN + Sertifikat\n2-ci yer — 300 AZN\n\nTətbiq imkanı\nİstifadə oluna bilər.',
    })
    const { container } = render9()

    expect(await screen.findByRole('heading', { name: 'Mükafatlar' })).toBeInTheDocument()
    const items = [...container.querySelectorAll('.prizes__item')].map((li) => li.textContent)
    expect(items).toEqual(['500 AZN1-ci yer+ Sertifikat', '300 AZN2-ci yer'])
    // What is not a place stays the note it was written as.
    expect(container.querySelector('.prizes__note')).toHaveTextContent('Tətbiq imkanı')
  })

  it('shows prize text that names no places exactly as it was written', async () => {
    api.competition.mockResolvedValue({ ...base, prize: 'Mükafat fondu sonra elan olunacaq.' })
    const { container } = render9()

    expect(await screen.findByRole('heading', { name: 'Mükafatlar' })).toBeInTheDocument()
    expect(screen.getByText('Mükafat fondu sonra elan olunacaq.')).toBeInTheDocument()
    expect(container.querySelector('.prizes__item')).toBeNull()
  })
})

describe('İşlərim result indicator', () => {
  const row = (over: Partial<MySubmission>): MySubmission => ({
    id: 1,
    referenceNumber: 'SUB-2026-00001',
    title: 'Maştağa işi',
    status: 'Submitted',
    isEditable: false,
    submittedAt: '2026-09-18T10:00:00Z',
    createdAt: '2026-09-18T10:00:00Z',
    updatedAt: '2026-09-18T10:00:00Z',
    projectName: 'Bakı kəndləri',
    projectSlug: 'baki-kendleri',
    competitionTitle: competition.title,
    competitionSlug: 'sevron',
    categoryLabel: 'Qəsəbələr',
    categoryName: 'Maştağa',
    resultsPublished: false,
    isWinner: false,
    place: null,
    ...over,
  })

  it('shows "Qalib" to the winner, a neutral link to others, nothing before publication', async () => {
    api.mine.mockResolvedValue([
      row({ id: 1, resultsPublished: true, isWinner: true }),
      row({ id: 2, referenceNumber: 'SUB-2026-00002', title: 'Şüvəlan işi', resultsPublished: true }),
      row({ id: 3, referenceNumber: 'SUB-2026-00003', title: 'Başqa müsabiqə', competitionSlug: 'diger' }),
    ])
    renderAt('/my/submissions', '/my/submissions', <MySubmissionsPage />)
    const items = await screen.findAllByRole('listitem')
    const [winner, other, pending] = items
    expect(within(winner).getByText('Qalib')).toBeInTheDocument()
    expect(within(winner).getByRole('link', { name: 'Nəticələr açıqlanıb — bax' })).toHaveAttribute('href', '/competitions/sevron/results')
    expect(within(other).queryByText('Qalib')).not.toBeInTheDocument()
    expect(within(other).getByRole('link', { name: 'Nəticələr açıqlanıb — bax' })).toBeInTheDocument()
    expect(within(other).queryByText(/qalib olmadınız/i)).not.toBeInTheDocument()
    expect(within(pending).queryByText(/Nəticələr açıqlanıb/)).not.toBeInTheDocument()
  })
})

describe('public work page', () => {
  const file = (id: number, kind: 'image' | 'document-preview') => ({
    id,
    kind,
    url: `/api/competitions/sevron/gallery/SUB-2026-00001/files/${id}`,
    thumbUrl: `/api/competitions/sevron/gallery/SUB-2026-00001/files/${id}/thumb`,
    width: 1254,
    height: 1254,
    name: `Fayl ${id}`,
  })

  it("a winner's page says whose win it is first, then shows the artwork whole", async () => {
    api.work.mockResolvedValue({ ...gallery.items[0], answers: [{ label: 'Konseptin izahı', fieldType: 'LongText', value: 'Nar və külək.' }], files: [file(1, 'image')] })
    api.gallery.mockResolvedValue(gallery)
    renderAt('/competitions/sevron/gallery/SUB-2026-00001', '/competitions/:slug/gallery/:reference', <GalleryWorkPage />)
    expect(await screen.findByRole('heading', { name: 'Maştağa üzrə qalib', level: 1 })).toBeInTheDocument()
    const facts = screen.getByText('İştirakçı').closest('dl')!
    expect(within(facts).getByText('Leyla Quliyeva')).toBeInTheDocument()
    expect(within(facts).getByText('Maştağa işi')).toBeInTheDocument()
    expect(within(facts).getByText('SUB-2026-00001')).toBeInTheDocument()
    const hero = screen.getByRole('img', { name: 'Maştağa üzrə qalib iş — Maştağa işi' })
    expect(hero).toHaveAttribute('src', file(1, 'image').url)                    // the full copy, not the card thumbnail
    expect(hero.closest('.art-frame')).not.toBeNull()
    expect(screen.getByText('Nar və külək.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Bütün qaliblər' })).toHaveAttribute('href', '/competitions/sevron/results')
    expect(screen.getByRole('link', { name: 'Bütün işlər' })).toHaveAttribute('href', '/competitions/sevron/gallery')
  })

  it('any other work stays anonymous', async () => {
    api.work.mockResolvedValue({ ...gallery.items[1], answers: [], files: [file(2, 'image')] })
    api.gallery.mockResolvedValue(gallery)
    renderAt('/competitions/sevron/gallery/SUB-2026-00002', '/competitions/:slug/gallery/:reference', <GalleryWorkPage />)
    expect(await screen.findByRole('heading', { name: 'Şüvəlan işi', level: 1 })).toBeInTheDocument()
    expect(screen.queryByText('İştirakçı')).not.toBeInTheDocument()
    expect(screen.queryByText('Qalib')).not.toBeInTheDocument()
  })

  it('shows only server-made images: a PDF appears as its first page, never as a download', async () => {
    api.work.mockResolvedValue({ ...gallery.items[0], answers: [], files: [file(1, 'image'), file(2, 'document-preview')] })
    api.gallery.mockResolvedValue(gallery)
    renderAt('/competitions/sevron/gallery/SUB-2026-00001', '/competitions/:slug/gallery/:reference', <GalleryWorkPage />)
    expect(await screen.findByRole('img', { name: 'Maştağa işi — sənədin birinci səhifəsi' })).toBeInTheDocument()
    expect(screen.getByText(/Sənədin birinci səhifəsi/)).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /PDF/ })).not.toBeInTheDocument()
    expect(document.querySelector('a[download]')).toBeNull()
  })
})

describe('public results page (first, second and third place)', () => {
  const plain = { ...competition, slug: 'emircan', categoryLabel: null, hasCategories: false }
  const placed = (title: string, place: number, name: string, i: number): GalleryItem => ({
    ...item(title, i),
    title,
    category: null,
    isWinner: true,
    winnerName: name,
    place,
  })
  const podium = [placed('Qartal', 1, 'Elvin Nəsirli', 0), placed('Çay', 2, 'Günel Nəsirli', 1), placed('Dağ', 3, 'Fidan Nəsirli', 2)]

  it('shows the three places in order, each saying its place, with the names of their authors', async () => {
    api.result.mockResolvedValue({ ...result, competition: plain, categories: [], winnerCount: 3, overallWinner: podium[0], places: podium })
    const { container } = renderAt('/competitions/emircan/results', '/competitions/:slug/results', <CompetitionResultsPage />)
    expect(await screen.findByRole('heading', { name: 'Qaliblər' })).toBeInTheDocument()
    expect(cards(container).map((c) => c.querySelector('.wcard__place')?.textContent)).toEqual(['1-ci yer', '2-ci yer', '3-cü yer'])
    const second = cards(container)[1]
    expect(within(second).getByText('Günel Nəsirli')).toBeInTheDocument()
    expect(within(second).getByRole('link', { name: '2-ci yer — iş: Çay — bax' })).toHaveTextContent('İşə bax')
    expect(within(cards(container)[0]).getByRole('link', { name: /1-ci yer — iş: Qartal/ })).toHaveTextContent('Qalib işə bax')
  })

  it('keeps the single winner layout when only first place is awarded', async () => {
    api.result.mockResolvedValue({ ...result, competition: plain, categories: [], winnerCount: 1, overallWinner: podium[0], places: [podium[0]] })
    renderAt('/competitions/emircan/results', '/competitions/:slug/results', <CompetitionResultsPage />)
    expect(await screen.findByRole('heading', { name: 'Qalib', level: 2 })).toBeInTheDocument()
  })

  it('marks a placed work in the gallery with its place', async () => {
    api.gallery.mockResolvedValue({ ...gallery, competition: plain, categories: [], items: [...podium, { ...item('Bulaq', 3), category: null }] })
    const { container } = renderAt('/competitions/emircan/gallery', '/competitions/:slug/gallery', <GalleryPage />)
    await screen.findByText('Bulaq işi')
    const badges = [...container.querySelectorAll('.gcard__badge')].map((b) => b.textContent)
    expect(badges).toEqual(['1-ci yer', '2-ci yer', '3-cü yer'])
  })
})
