import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { CompetitionSummary } from '../api/types'
import { CompetitionRow } from './CompetitionRow'

const summary = (over: Partial<CompetitionSummary> = {}): CompetitionSummary => ({
  id: 3,
  slug: 'xezerin-nefesi-poster',
  title: 'Xəzərin nəfəsi — poster müsabiqəsi',
  shortDescription: 'Xəzərin şəhərlə əlaqəsini poster dili ilə ifadə et.',
  coverImage: null,
  status: 'Published',
  phase: 'Open',
  startDate: null,
  endDate: null,
  hasPrize: false,
  hasCategories: false,
  categoryLabel: null,
  categoryCount: 0,
  project: { slug: 'xezer-sahili', name: 'Xəzər sahili', accentColor: '#1F5F7A' },
  resultsPublished: false,
  voting: 'NotConfigured',
  ...over,
})

const renderRow = (c: CompetitionSummary) =>
  render(
    <MemoryRouter>
      <CompetitionRow competition={c} />
    </MemoryRouter>,
  )

describe('CompetitionRow', () => {
  it('shows the competition poster small beside the title, as decoration of the same link', () => {
    const { container } = renderRow(summary({ coverImage: '/api/media/covers/2026/09/poster.webp' }))
    const poster = container.querySelector('img.competition-row__poster')
    expect(poster).toHaveAttribute('src', '/api/media/covers/2026/09/poster.webp')
    expect(poster).toHaveAttribute('alt', '')
    expect(poster).toHaveAttribute('loading', 'lazy')
    expect(container.querySelector('.competition-row')).toHaveClass('competition-row--poster')
    // One link for the whole row, named by its text — the image adds nothing to it.
    expect(screen.getByRole('link')).toHaveAttribute('href', '/competitions/xezerin-nefesi-poster')
  })

  it('keeps the plain row without a poster', () => {
    const { container } = renderRow(summary())
    expect(container.querySelector('img')).toBeNull()
    expect(container.querySelector('.competition-row')).not.toHaveClass('competition-row--poster')
  })
})
