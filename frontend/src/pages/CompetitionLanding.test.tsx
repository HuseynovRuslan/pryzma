import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { CompetitionDetail } from '../api/types'
import { registerAnalytics, type TrackedEvent } from '../lib/analytics'
import CompetitionDetailPage from './CompetitionDetailPage'

const api = vi.hoisted(() => ({ competition: vi.fn() }))
vi.mock('../api/endpoints', () => ({ catalog: { competition: api.competition } }))
vi.mock('../auth/AuthContext', () => ({ useAuth: () => ({ status: 'anonymous', user: null }) }))

const NBSP = String.fromCharCode(0xa0)

/** Əmircan as the API describes it today. */
const emircan = (over: Partial<CompetitionDetail> = {}): CompetitionDetail =>
  ({
    id: 3,
    slug: 'emircanin-simvolu',
    title: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi',
    shortDescription: 'Əmircanın zəngin tarixi, memarlığı və yaradıcı irsini ifadə edən simvollar üçün açıq müsabiqə',
    description: 'Əmircanın kimliyini müasir dizayn dili ilə yenidən təqdim etməyə dəvət edirik.',
    status: 'Published',
    phase: 'Open',
    isAcceptingSubmissions: true,
    startDate: '2026-09-23T05:00:00+00:00',
    endDate: '2026-10-03T19:59:00+00:00',
    resultsAnnouncedOn: null,
    exhibitionOn: null,
    resultsPublishedAt: null,
    coverImage: '/api/media/covers/2026/09/poster.webp',
    prize: '1-ci yer — 250 AZN + Sertifikat\n2-ci yer — 150 AZN + Sertifikat\n3-cü yer — 100 AZN + Sertifikat',
    nominations: 'Simvol və Loqotip Dizaynı\nSimvol və loqotip konseptləri.',
    rules: 'Təqdim olunan iş iştirakçının öz yaradıcı işi olmalıdır.',
    requirements: 'Süni intellekt vasitəsilə hazırlanmış təqdimatlar da qəbul edilir.',
    judgingCriteria: 'Orijinallıq və yaradıcı yanaşma',
    usageTerms: 'Müəllif göstərilməklə nümayiş etdirilə bilər.',
    contactNote: null,
    maxSubmissionsPerUser: 1,
    hasCategories: false,
    categoryLabel: null,
    categories: [],
    project: { slug: 'abseronun-simvollari', name: 'Abşeronun simvolları', accentColor: '#8C6A3F' },
    ...over,
  }) as CompetitionDetail

function Where() {
  const l = useLocation()
  return <output data-testid="where">{l.pathname + l.search}</output>
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/emircan" element={<CompetitionDetailPage slug="emircanin-simvolu" />} />
        <Route path="/competitions/:slug" element={<CompetitionDetailPage />} />
        <Route path="*" element={<Where />} />
      </Routes>
    </MemoryRouter>,
  )
}

const events: TrackedEvent[] = []
let stop = () => {}

beforeAll(() => {
  // Five Baku days before Əmircan closes; only Date is faked, so the page's own timers still run.
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date('2026-09-28T08:00:00Z'))
  globalThis.ResizeObserver = class {
    observe() {}
    disconnect() {}
    unobserve() {}
  } as unknown as typeof ResizeObserver
})
afterAll(() => vi.useRealTimers())

beforeEach(() => {
  vi.clearAllMocks()
  localStorage.clear()
  events.length = 0
  stop = registerAnalytics((e) => events.push(e))
})
afterEach(() => stop())

describe('the ad landing (/emircan)', () => {
  it('answers what, what for, how much, until when and where — before anything else on the page', async () => {
    api.competition.mockResolvedValue(emircan())
    renderAt('/emircan')

    expect(await screen.findByRole('heading', { level: 1, name: 'Əmircanın simvolunu sən yarat' })).toBeInTheDocument()
    // The competition's own name stays on the page, above the ad's headline.
    expect(document.querySelector('.comp__kicker')).toHaveTextContent('“Əmircanın simvolu” yaradıcılıq müsabiqəsi')
    expect(screen.getByText('Əmircan üçün simvol və loqo ideyanı təqdim et')).toBeInTheDocument()

    // Figures come from the competition, not from the ad: the fund is added up from the places.
    // Testing Library reads the no-break space as a space; the exact text shows it is really there.
    expect([...(screen.getByText('500 AZN').textContent ?? '')].map((ch) => ch.charCodeAt(0).toString(16))).toEqual(
      [...`500${NBSP}AZN`].map((ch) => ch.charCodeAt(0).toString(16)),
    )
    expect(screen.getByText('3 oktyabr, 23:59')).toBeInTheDocument()
    expect(screen.getByText('5 gün qalıb')).toBeInTheDocument()

    // The hero's button comes first in the page; the closing invitation and the desktop panel repeat it.
    expect(screen.getAllByRole('button', { name: 'İdeyanı təqdim et' })[0]).toHaveClass('comp__cta')
    expect(screen.getByText('Dizayner olmaq şərt deyil')).toBeInTheDocument()
    expect(screen.getByText('AI alətlərindən istifadə etmək olar')).toBeInTheDocument()

    // A visitor from an ad has no list of competitions behind them: no phone "back" link above the headline.
    expect(document.querySelector('.comp__back')).toBeNull()

    // The places, straight after the hero, and the three steps.
    const prizes = screen.getByRole('region', { name: 'Mükafatlar' })
    expect(within(prizes).getByText('250 AZN')).toBeInTheDocument()
    expect(within(prizes).getByText('3-cü yer')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'İdeyanı hazırla' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'İşini təqdim et' })).toBeInTheDocument()

    // The view is counted in an effect, which React runs just after the page has been drawn.
    await waitFor(() =>
      expect(events).toEqual([expect.objectContaining({ name: 'landing_viewed', slug: 'emircanin-simvolu', via: '/emircan' })]),
    )
  })

  it('takes the main button into the existing entry flow, through sign-in', async () => {
    api.competition.mockResolvedValue(emircan())
    renderAt('/emircan')

    await userEvent.click((await screen.findAllByRole('button', { name: 'İdeyanı təqdim et' }))[0])

    expect(screen.getByTestId('where')).toHaveTextContent('/login?next=%2Fcompetitions%2Femircanin-simvolu%2Fsubmit')
    expect(events.map((e) => e.name)).toEqual(['landing_viewed', 'cta_clicked'])
    expect(events[1]).toMatchObject({ slug: 'emircanin-simvolu', placement: 'hero' })
  })

  it('is the same page at the competition’s own address, with the way back to the list', async () => {
    api.competition.mockResolvedValue(emircan())
    renderAt('/competitions/emircanin-simvolu')

    expect(await screen.findByRole('heading', { level: 1, name: 'Əmircanın simvolunu sən yarat' })).toBeInTheDocument()
    expect(document.querySelector('.comp__back')).toHaveAttribute('href', '/competitions')
    await waitFor(() => expect(events[0]).toMatchObject({ name: 'landing_viewed', via: '/competitions/emircanin-simvolu' }))
  })

  it('drops the ad’s words once entries close: nobody is told to make a symbol they can no longer send', async () => {
    api.competition.mockResolvedValue(emircan({ phase: 'Closed', isAcceptingSubmissions: false }))
    renderAt('/emircan')

    expect(await screen.findByRole('heading', { level: 1, name: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi' })).toBeInTheDocument()
    expect(screen.queryByText('Əmircanın simvolunu sən yarat')).not.toBeInTheDocument()
    expect(screen.queryAllByRole('button', { name: 'İdeyanı təqdim et' })).toHaveLength(0)
    expect(screen.queryByText('Dizayner olmaq şərt deyil')).not.toBeInTheDocument()
    expect(screen.queryByText(/gün qalıb/)).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Qəbul başa çatıb' })).toBeInTheDocument()
  })

  it('states no fund it cannot add up, and lists the prizes instead', async () => {
    api.competition.mockResolvedValue(emircan({ prize: '1-ci yer — 250 AZN\n2-ci yer — Sərgidə iştirak' }))
    renderAt('/emircan')

    await screen.findByRole('heading', { level: 1 })
    expect(screen.queryByText('Mükafat fondu')).not.toBeInTheDocument()
    expect(screen.getByText('250 AZN · Sərgidə iştirak')).toBeInTheDocument()
  })
})
