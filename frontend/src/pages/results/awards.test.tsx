import { render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { CompetitionResult, GalleryItem, GalleryWork, PublicAward } from '../../api/types'
import { PlatformContext } from '../../platform/context'
import { platformDefaults } from '../../platform/defaults'
import { AWARDS_LEDE } from './AwardsResults'
import CompetitionResultsPage from './CompetitionResultsPage'
import GalleryWorkPage from './GalleryWorkPage'

const api = vi.hoisted(() => ({ result: vi.fn(), gallery: vi.fn(), work: vi.fn(), list: vi.fn() }))
vi.mock('../../api/endpoints', () => ({ resultsApi: { result: api.result, gallery: api.gallery, work: api.work, list: api.list } }))

beforeEach(() => vi.clearAllMocks())

const competition = {
  slug: 'emircanin-simvolu',
  title: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi',
  shortDescription: 'x',
  categoryLabel: null,
  hasCategories: false,
  project: { slug: 'abseronun-simvollari', name: 'Abşeronun simvolları', accentColor: '#8C6A3F' },
}

const work = (n: number, title: string, name: string): GalleryItem => {
  const reference = `SUB-2026-${String(n).padStart(5, '0')}`
  const url = `/api/competitions/emircanin-simvolu/gallery/${reference}/files/${n}`
  return {
    reference, title, category: null, isWinner: true, winnerName: name,
    preview: { url, thumbUrl: `${url}/thumb`, width: 1200, height: 1200 }, previewState: 'Ready', place: null,
  }
}

const award = (place: number, points: number, name: string, n: number, title: string): PublicAward => ({
  place, points, participantName: name, work: work(n, title, name),
})

/** The owner's announcement (16 October 2026): the jury's paper vote and the public's valid votes, apart. */
const result: CompetitionResult = {
  competition,
  publishedAt: '2026-10-17T10:00:00Z',
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
      award(3, 16, 'Nərgiz Əsədova', 111, 'Əmircan simvolu'),
    ],
    public: [
      award(1, 200, 'Şəbnəm Ələkbərli', 90, 'Əmircan loqosu'),
      award(2, 183, 'Məryəm Əhmədova', 61, 'Əmircan — daşda qalan tarix'),
      award(3, 173, 'Səid Məmmədov', 96, 'Əmircanın Vizual Konsepsiyası'),
    ],
  },
}

const at = (path: string, element: React.ReactNode) =>
  render(
    <PlatformContext.Provider value={platformDefaults}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/competitions/:slug/results" element={element} />
          <Route path="/competitions/:slug/gallery/:reference" element={element} />
        </Routes>
      </MemoryRouter>
    </PlatformContext.Provider>,
  )

const places = (section: HTMLElement) =>
  within(section)
    .getAllByRole('listitem')
    .map((li) => [
      within(li).getByRole('heading', { level: 3 }).textContent,
      li.querySelector('.aw-place__name')?.textContent,
      li.querySelector('.aw-place__points')?.textContent,
      li.querySelector('.aw-place__rank')?.textContent,
    ])

describe('the results page of a two-choice announcement', () => {
  it('shows the jury’s choice and the public’s choice apart, three places each, with their own numbers', async () => {
    api.result.mockResolvedValue(result)
    at('/competitions/emircanin-simvolu/results', <CompetitionResultsPage />)

    expect(await screen.findByRole('heading', { level: 1, name: 'Müsabiqənin nəticələri' })).toBeInTheDocument()
    expect(screen.getByText('Əmircanın simvolu')).toBeInTheDocument()
    expect(screen.getByText(AWARDS_LEDE)).toBeInTheDocument()

    const jury = screen.getByRole('region', { name: 'Münsiflərin seçimi' })
    const pub = screen.getByRole('region', { name: 'Xalqın seçimi' })
    expect(places(jury)).toEqual([
      ['Qədim Əmircan loqotip konsepti', 'Kamal Məmmədli', '30 bal', '11-ci yer'],
      ['Əmircan — daşda qalan tarix', 'Məryəm Əhmədova', '26 bal', '22-ci yer'],
      ['Əmircan simvolu', 'Nərgiz Əsədova', '16 bal', '33-cü yer'],
    ])
    expect(places(pub)).toEqual([
      ['Əmircan loqosu', 'Şəbnəm Ələkbərli', '200 səs', '11-ci yer'],
      ['Əmircan — daşda qalan tarix', 'Məryəm Əhmədova', '183 səs', '22-ci yer'],
      ['Əmircanın Vizual Konsepsiyası', 'Səid Məmmədov', '173 səs', '33-cü yer'],
    ])

    // The same participant in both, each with that section's own number; every place links to its work.
    expect(within(jury).getByRole('link', { name: 'Münsiflərin seçimi, 2-ci yer: Əmircan — daşda qalan tarix — işə bax' }))
      .toHaveAttribute('href', '/competitions/emircanin-simvolu/gallery/SUB-2026-00061')
    expect(within(pub).getByRole('link', { name: /Xalqın seçimi, 2-ci yer/ })).toHaveAttribute('href', '/competitions/emircanin-simvolu/gallery/SUB-2026-00061')
    // The work is shown, described for screen readers.
    expect(within(jury).getByRole('img', { name: 'Münsiflərin seçimi, 1-ci yer: Qədim Əmircan loqotip konsepti — Kamal Məmmədli' })).toHaveAttribute(
      'src', '/api/competitions/emircanin-simvolu/gallery/SUB-2026-00039/files/39',
    )

    // Never merged: no overall winner, no formula, no sum, no prize.
    const text = document.body.textContent ?? ''
    expect(text).not.toMatch(/Qalib\b|Ümumi qalib|yekun xal|50\/50|AZN|mükafat|xal\b/i)
    expect(screen.getAllByRole('heading', { level: 3 })).toHaveLength(6)
  })

  it('links the two sections from the top of the page', async () => {
    api.result.mockResolvedValue(result)
    at('/competitions/emircanin-simvolu/results', <CompetitionResultsPage />)
    const nav = await screen.findByRole('navigation', { name: 'Bölmələr' })
    expect(within(nav).getByRole('link', { name: /Münsiflərin seçimi/ })).toHaveAttribute('href', '#munsiflerin-secimi')
    expect(within(nav).getByRole('link', { name: /Xalqın seçimi/ })).toHaveAttribute('href', '#xalqin-secimi')
  })

  it('keeps the classic page for a result without the two choices', async () => {
    api.result.mockResolvedValue({ ...result, awards: null, places: [{ ...work(90, 'Əmircan loqosu', 'Şəbnəm Ələkbərli'), place: 1 }] })
    at('/competitions/emircanin-simvolu/results', <CompetitionResultsPage />)
    expect(await screen.findByRole('heading', { level: 1, name: 'Nəticələr' })).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Münsiflərin seçimi' })).not.toBeInTheDocument()
  })
})

describe('the page of a work on a podium', () => {
  it('names each choice it stands in, with its number', async () => {
    const w: GalleryWork = {
      ...work(61, 'Əmircan — daşda qalan tarix', 'Məryəm Əhmədova'),
      awards: [
        { category: 'Jury', place: 2, points: 26 },
        { category: 'Public', place: 2, points: 183 },
      ],
      answers: [],
      files: [],
    }
    api.work.mockResolvedValue(w)
    api.gallery.mockResolvedValue({ competition, publishedAt: result.publishedAt, categories: [], items: [], total: 99, winnerCount: 5 })
    at('/competitions/emircanin-simvolu/gallery/SUB-2026-00061', <GalleryWorkPage />)
    expect(await screen.findByRole('heading', { level: 1, name: 'Münsiflərin və xalqın seçimi' })).toBeInTheDocument()
    const list = screen.getByRole('list', { name: 'Nəticədə tutduğu yerlər' })
    expect(within(list).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'Münsiflərin seçimi — 2-ci yer · 26 bal',
      'Xalqın seçimi — 2-ci yer · 183 səs',
    ])
    expect(screen.getByText('Məryəm Əhmədova')).toBeInTheDocument()
  })
})
