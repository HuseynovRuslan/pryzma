import { describe, expect, it } from 'vitest'
import type { CompetitionSummary, Voting } from '../../api/types'
import type { Lifecycle } from '../competitionLifecycle'
import type { FeaturedSlide } from '../featuredCompetitionContext'
import { finalPhaseOf, finalTargetFor, isMusicPhase, LAST_MINUTE_MS, LAST_TEN_MS, MUSIC_WINDOW_MS } from './schedule'

const NOW = Date.parse('2026-11-03T18:00:00Z')

const slide = (slug: string, state: Voting['state'], endsAt: string | null): FeaturedSlide => ({
  competition: { slug } as CompetitionSummary,
  voting: { state, endsAt } as Voting,
  lifecycle: {} as Lifecycle,
})

describe('the final phase', () => {
  it('is silent until three hours before the close, then counts down to it', () => {
    const at = (msLeft: number) => finalPhaseOf(NOW + msLeft, NOW)
    expect(at(MUSIC_WINDOW_MS + 1)).toBe('idle')
    expect(at(MUSIC_WINDOW_MS)).toBe('hours')
    expect(at(LAST_TEN_MS + 1)).toBe('hours')
    expect(at(LAST_TEN_MS)).toBe('lastTen')
    expect(at(LAST_MINUTE_MS)).toBe('lastMinute')
    expect(at(1)).toBe('lastMinute')
    expect(at(0)).toBe('over')
    expect(at(-5000)).toBe('over')
    expect(finalPhaseOf(null, NOW)).toBe('idle')
  })

  it('plays only in the three phases before the close', () => {
    expect(['idle', 'hours', 'lastTen', 'lastMinute', 'over'].filter((p) => isMusicPhase(p as never))).toEqual([
      'hours',
      'lastTen',
      'lastMinute',
    ])
  })
})

describe('the pages that carry the music', () => {
  const slides = [
    slide('mastaganin-simvolu', 'Open', '2026-11-03T19:59:00Z'),
    slide('novxaninin-simvolu', 'Open', '2026-11-03T19:30:00Z'),
    slide('emircanin-simvolu', 'Closed', '2026-10-01T19:59:00Z'),
  ]

  it('on the front page and the list, follows the open vote that closes first', () => {
    expect(finalTargetFor(slides, '/')?.slug).toBe('novxaninin-simvolu')
    expect(finalTargetFor(slides, '/competitions/')?.slug).toBe('novxaninin-simvolu')
  })

  it("on an open competition's page and ballot, follows that competition", () => {
    for (const path of [
      '/competitions/mastaganin-simvolu',
      '/competitions/mastaganin-simvolu/voting',
      '/competitions/mastaganin-simvolu/voting/SUB-2026-00210',
    ])
      expect(finalTargetFor(slides, path)).toEqual({
        slug: 'mastaganin-simvolu',
        endsAt: Date.parse('2026-11-03T19:59:00Z'),
      })
  })

  it('stays silent everywhere else', () => {
    for (const path of [
      '/login',
      '/register',
      '/about',
      '/my/profile',
      '/results',
      '/competitions/mastaganin-simvolu/results',
      '/competitions/mastaganin-simvolu/gallery',
      '/competitions/mastaganin-simvolu/submit',
      '/competitions/emircanin-simvolu',
      '/emircan',
    ])
      expect(finalTargetFor(slides, path)).toBeNull()
  })

  it('has nothing to follow when no vote is open, or an open one has no closing date', () => {
    expect(finalTargetFor([slides[2]], '/')).toBeNull()
    expect(finalTargetFor([slide('x', 'Open', null)], '/')).toBeNull()
  })
})
