import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { CompetitionSummary } from '../../api/types'
import type { Lifecycle } from '../../lib/competitionLifecycle'
import { FeaturedContext, type Featured, type FeaturedSlide } from '../../lib/featuredCompetitionContext'
import { VotingAnnouncement } from './VotingAnnouncement'

const slide = (slug: string, title: string, stage: Lifecycle['stage']): FeaturedSlide => ({
  competition: { slug, title } as CompetitionSummary,
  voting: null,
  lifecycle: { stage } as Lifecycle,
})

const show = (slides: FeaturedSlide[], at = '/') => {
  const value: Featured = { competition: null, voting: null, lifecycle: null, slides, ready: true, published: [] }
  return render(
    <MemoryRouter initialEntries={[at]}>
      <FeaturedContext.Provider value={value}>
        <VotingAnnouncement />
      </FeaturedContext.Provider>
    </MemoryRouter>,
  )
}

describe('the "voting has started" strip', () => {
  it('names every open vote with a link to its own ballot', () => {
    show([
      slide('novxaninin-simvolu', '“Novxanının simvolu” yaradıcılıq müsabiqəsi', 'votingLive'),
      slide('mastaganin-simvolu', '“Maştağanın simvolu” yaradıcılıq müsabiqəsi', 'votingLive'),
      slide('kohne', '“Köhnə” müsabiqə', 'votingClosed'),
    ])
    expect(screen.getByRole('complementary', { name: 'Səsvermə elanı' })).toHaveTextContent('Səsvermə başladı!')
    expect(screen.getByRole('link', { name: 'Novxanının simvolu — səs ver' })).toHaveAttribute('href', '/competitions/novxaninin-simvolu/voting')
    expect(screen.getByRole('link', { name: 'Maştağanın simvolu — səs ver' })).toHaveAttribute('href', '/competitions/mastaganin-simvolu/voting')
    expect(screen.queryByRole('link', { name: /Köhnə/ })).not.toBeInTheDocument()
  })

  it('marks the ballot the reader is on', () => {
    show([slide('novxaninin-simvolu', '“Novxanının simvolu”', 'votingLive')], '/competitions/novxaninin-simvolu/voting')
    expect(screen.getByRole('link', { name: 'Novxanının simvolu — səs ver' })).toHaveAttribute('aria-current', 'page')
  })

  it('is not there while no vote is open', () => {
    show([slide('novxaninin-simvolu', '“Novxanının simvolu”', 'review')])
    expect(screen.queryByRole('complementary')).not.toBeInTheDocument()
  })
})
