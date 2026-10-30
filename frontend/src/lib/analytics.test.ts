import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { campaignFor, campaigns } from '../content/campaigns'
import { captureAttribution, readAttribution, registerAnalytics, track, type TrackedEvent } from './analytics'

describe('attribution', () => {
  beforeEach(() => localStorage.clear())

  it('keeps the parameters an ad arrived with, and where it landed', () => {
    captureAttribution('?utm_source=instagram&utm_medium=paid&utm_campaign=emircan&fbclid=abc&page=2', '/emircan', 1000)

    expect(readAttribution(2000)).toEqual({
      params: { utm_source: 'instagram', utm_medium: 'paid', utm_campaign: 'emircan', fbclid: 'abc' },
      landingPath: '/emircan',
      landedAt: 1000,
    })
  })

  it('is not wiped by a later visit that carries no campaign', () => {
    captureAttribution('?utm_source=instagram', '/emircan', 1000)
    captureAttribution('', '/login', 1500)
    captureAttribution('?next=%2Fcompetitions', '/login', 1600)

    expect(readAttribution(2000)?.params).toEqual({ utm_source: 'instagram' })
  })

  it('lets the most recent campaign win', () => {
    captureAttribution('?utm_source=instagram', '/emircan', 1000)
    captureAttribution('?utm_source=facebook&utm_content=reel', '/emircan', 1500)

    expect(readAttribution(2000)?.params).toEqual({ utm_source: 'facebook', utm_content: 'reel' })
  })

  it('forgets a campaign after thirty days', () => {
    captureAttribution('?utm_source=instagram', '/emircan', 0)
    expect(readAttribution(1000 * 60 * 60 * 24 * 31)).toBeNull()
  })
})

describe('track', () => {
  const received: TrackedEvent[] = []
  let unregister: () => void = () => {}

  beforeEach(() => {
    localStorage.clear()
    received.length = 0
    unregister = registerAnalytics((e) => received.push(e))
  })

  afterEach(() => unregister())

  it('hands events to a registered provider with the visit’s campaign attached', () => {
    captureAttribution('?utm_campaign=emircan', '/emircan')
    track({ name: 'cta_clicked', slug: 'emircanin-simvolu', placement: 'hero' })

    expect(received).toHaveLength(1)
    expect(received[0]).toMatchObject({
      name: 'cta_clicked',
      slug: 'emircanin-simvolu',
      placement: 'hero',
      attribution: { params: { utm_campaign: 'emircan' }, landingPath: '/emircan' },
    })
  })

  it('never lets a broken provider break the page', () => {
    const stop = registerAnalytics(() => {
      throw new Error('tracker down')
    })
    expect(() => track({ name: 'landing_viewed', slug: 'x', via: '/competitions/x' })).not.toThrow()
    expect(received).toHaveLength(1)
    stop()
  })
})

describe('campaigns', () => {
  it('each ad address points at one competition and does not collide with a real route', () => {
    const reserved = ['/', '/competitions', '/projects', '/results', '/about', '/login', '/register', '/admin', '/my']
    for (const c of campaigns) {
      expect(c.path).toMatch(/^\/[a-z0-9-]+$/)
      expect(reserved).not.toContain(c.path)
      expect(campaignFor(c.competitionSlug)).toBe(c)
    }
    expect(campaignFor('no-such-competition')).toBeNull()
  })
})
