import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Dashboard } from './api'
import DashboardPage from './pages/DashboardPage'

const adminApi = vi.hoisted(() => ({ dashboard: vi.fn() }))
vi.mock('./api', () => ({ adminApi }))

const competitions: Dashboard['competitions'] = [
  { id: 6, title: '“Novxanının simvolu” yaradıcılıq müsabiqəsi', status: 'Published', phase: 'Open', isOpen: true },
  { id: 7, title: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi', status: 'Published', phase: 'Closed', isOpen: false },
]

const data = (over: Partial<Dashboard> = {}): Dashboard => ({
  publishedProjects: 1,
  openCompetitions: 1,
  totalSubmissions: 113,
  draftSubmissions: 58,
  submittedSubmissions: 90,
  underReviewSubmissions: 3,
  approvedSubmissions: 6,
  rejectedSubmissions: 14,
  recentSubmissions: [],
  competitionId: null,
  competitions,
  ...over,
})

function Where() {
  const location = useLocation()
  return <span data-testid="where">{location.search}</span>
}

const show = (url: string) =>
  render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route
          path="/admin"
          element={
            <>
              <DashboardPage />
              <Where />
            </>
          }
        />
      </Routes>
    </MemoryRouter>,
  )

beforeEach(() => {
  vi.clearAllMocks()
  adminApi.dashboard.mockImplementation((_signal: AbortSignal, competitionId?: number) =>
    Promise.resolve(
      competitionId === 6
        ? data({ competitionId: 6, totalSubmissions: 5, submittedSubmissions: 4, underReviewSubmissions: 1, approvedSubmissions: 0, rejectedSubmissions: 0, draftSubmissions: 2 })
        : data(),
    ),
  )
})

const stat = (label: string) => screen.getByText(label).closest('.a-stat')

describe('the overview, competition by competition', () => {
  it('opens on the competition being run, with its own figures and links', async () => {
    show('/admin')
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('?competitionId=6'))
    expect(await screen.findByText('Təsdiqlənib')).toBeInTheDocument()
    expect(adminApi.dashboard).toHaveBeenLastCalledWith(expect.anything(), 6)

    expect(screen.getByRole('combobox', { name: 'Müsabiqə' })).toHaveValue('6')
    expect(stat('Ümumi təkliflər')).toHaveTextContent('5')
    expect(stat('Ümumi təkliflər')).toHaveAttribute('href', '/admin/submissions?competitionId=6')
    expect(stat('Baxılmamış (Göndərilib)')).toHaveAttribute('href', '/admin/submissions?competitionId=6&status=Submitted')
    expect(stat('Rədd edilib')).toHaveAttribute('href', '/admin/submissions?competitionId=6&status=Rejected')
    // The platform's own counts mean nothing for one competition.
    expect(screen.queryByText('Aktiv layihələr')).not.toBeInTheDocument()
    expect(screen.queryByText('Qəbulu açıq müsabiqələr')).not.toBeInTheDocument()
    // The 113 of all competitions never shows while one is chosen.
    expect(screen.queryByText('113')).not.toBeInTheDocument()
  })

  it('names where each competition stands in the choice', async () => {
    show('/admin?competitionId=6')
    const choice = await screen.findByRole('combobox', { name: 'Müsabiqə' })
    expect([...choice.querySelectorAll('option')].map((o) => o.textContent)).toEqual([
      'Bütün müsabiqələr',
      'Novxanının simvolu (açıq)',
      'Əmircanın simvolu (bağlı)',
    ])
  })

  it('shows the whole platform as before when all are chosen', async () => {
    show('/admin?competitionId=all')
    expect(await screen.findByText('Aktiv layihələr')).toBeInTheDocument()
    expect(adminApi.dashboard).toHaveBeenCalledTimes(1)
    expect(adminApi.dashboard).toHaveBeenCalledWith(expect.anything(), undefined)
    expect(stat('Ümumi təkliflər')).toHaveTextContent('113')
    expect(stat('Ümumi təkliflər')).toHaveAttribute('href', '/admin/submissions')
    expect(screen.queryByText('Təsdiqlənib')).not.toBeInTheDocument()
    expect(screen.getByTestId('where')).toHaveTextContent('?competitionId=all')
  })

  it('follows the choice', async () => {
    show('/admin?competitionId=all')
    const choice = await screen.findByRole('combobox', { name: 'Müsabiqə' })
    fireEvent.change(choice, { target: { value: '6' } })
    await waitFor(() => expect(adminApi.dashboard).toHaveBeenLastCalledWith(expect.anything(), 6))
    expect(await screen.findByText('Təsdiqlənib')).toBeInTheDocument()
    expect(screen.getByTestId('where')).toHaveTextContent('?competitionId=6')
  })

  it('stays on all of them when nothing is open', async () => {
    adminApi.dashboard.mockResolvedValue(data({ competitions: competitions.map((c) => ({ ...c, isOpen: false, phase: 'Closed' as const })) }))
    show('/admin')
    expect(await screen.findByText('Aktiv layihələr')).toBeInTheDocument()
    expect(adminApi.dashboard).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('combobox', { name: 'Müsabiqə' })).toHaveValue('all')
  })
})
