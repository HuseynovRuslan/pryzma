import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { CompetitionSummary } from '../api/types'
import { competitionCardStatus } from '../lib/competitionCard'
import { CompetitionCard } from './CompetitionCard'

const competition = (over: Partial<CompetitionSummary> = {}): CompetitionSummary => ({
  id: 7,
  slug: 'emircanin-simvolu',
  title: '“Əmircanın simvolu” yaradıcılıq müsabiqəsi',
  shortDescription:
    'Əmircanın zəngin tarixi, memarlığı və yaradıcı irsini müasir vizual dillə ifadə edən açıq müsabiqə',
  coverImage: '/api/media/covers/emircan.webp',
  status: 'Published',
  phase: 'Open',
  startDate: '2026-09-23T05:00:00Z',
  endDate: '2026-10-03T19:59:00Z',
  hasPrize: true,
  hasCategories: false,
  categoryLabel: null,
  categoryCount: 0,
  project: { slug: 'abseronun-simvollari', name: 'Abşeronun simvolları', accentColor: '#8C6A3F' },
  resultsPublished: false,
  voting: 'NotConfigured',
  ...over,
})

describe('CompetitionCard', () => {
  it('shows only real summary fields and a truthful boolean prize signal', () => {
    const c = competition()
    const { container } = render(
      <MemoryRouter>
        <CompetitionCard competition={c} />
      </MemoryRouter>,
    )

    expect(screen.getByRole('heading', { name: c.title })).toBeInTheDocument()
    expect(screen.getByText(c.shortDescription)).toBeInTheDocument()
    expect(screen.getByText(c.project.name)).toBeInTheDocument()
    expect(screen.getByText('Qəbul açıqdır')).toBeInTheDocument()
    expect(screen.getByText('Mükafatlı müsabiqə')).toBeInTheDocument()
    expect(screen.getByText('3 oktyabr 2026')).toBeInTheDocument()
    expect(screen.getByRole('link')).toHaveAttribute('href', '/competitions/emircanin-simvolu')

    const text = container.textContent ?? ''
    for (const invented of ['250 AZN', 'Nizaməddin Qülləsi', 'EMR-01', 'Müəllif:', 'Studio', 'vote count']) {
      expect(text).not.toContain(invented)
    }
  })

  it('derives each public badge from the API status fields', () => {
    expect(competitionCardStatus(competition()).label).toBe('Qəbul açıqdır')
    expect(competitionCardStatus(competition({ voting: 'Upcoming' })).label).toBe('Səsvermə tezliklə')
    expect(competitionCardStatus(competition({ voting: 'Open' })).label).toBe('Səsvermə açıqdır')
    expect(competitionCardStatus(competition({ voting: 'Closed' })).label).toBe('Səsvermə başa çatıb')
    expect(competitionCardStatus(competition({ resultsPublished: true, voting: 'Closed' })).label).toBe('Nəticələr')
  })

  it('omits optional deadline and prize UI when the API does not provide them', () => {
    render(
      <MemoryRouter>
        <CompetitionCard competition={competition({ endDate: null, hasPrize: false })} />
      </MemoryRouter>,
    )
    expect(screen.queryByText('Son tarix')).not.toBeInTheDocument()
    expect(screen.queryByText('Mükafatlı müsabiqə')).not.toBeInTheDocument()
  })
})
