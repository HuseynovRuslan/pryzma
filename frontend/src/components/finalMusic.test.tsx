import { act, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { CompetitionSummary, Voting } from '../api/types'
import type { Lifecycle } from '../lib/competitionLifecycle'
import { FeaturedContext, type Featured, type FeaturedSlide } from '../lib/featuredCompetitionContext'

// The audio itself is the browser's; here only what the page asks of it is checked.
const calls = vi.hoisted(() => ({ play: [] as number[], pause: 0, result: 'playing' as 'playing' | 'blocked' }))
vi.mock('../lib/finalMusic/engine', () => ({
  audioSupported: () => true,
  FinalMusicEngine: class {
    isPlaying = false
    async play(endsAt: number) {
      calls.play.push(endsAt)
      this.isPlaying = calls.result === 'playing'
      return calls.result
    }
    sync() {}
    async pause() {
      calls.pause++
      this.isPlaying = false
    }
    async dispose() {}
  },
}))

const { FinalMusic } = await import('./FinalMusic')

const MIN = 60_000
const open = (msLeft: number): FeaturedSlide => ({
  competition: { slug: 'novxaninin-simvolu' } as CompetitionSummary,
  voting: { state: 'Open', endsAt: new Date(Date.now() + msLeft).toISOString() } as Voting,
  lifecycle: {} as Lifecycle,
})

const show = (slides: FeaturedSlide[], at = '/competitions/novxaninin-simvolu/voting') => {
  const value: Featured = { competition: null, voting: null, lifecycle: null, slides, ready: true, published: [] }
  return render(
    <MemoryRouter initialEntries={[at]}>
      <FeaturedContext.Provider value={value}>
        <FinalMusic />
      </FeaturedContext.Provider>
    </MemoryRouter>,
  )
}

const settle = () => act(async () => {
  await Promise.resolve()
  await Promise.resolve()
})

beforeEach(() => {
  calls.play = []
  calls.pause = 0
  calls.result = 'playing'
  window.localStorage.clear()
})
afterEach(() => vi.restoreAllMocks())

describe('the final-hours music', () => {
  it('is not there more than three hours before the close', async () => {
    show([open(3 * 60 * MIN + 5 * MIN)])
    await settle()
    expect(screen.queryByRole('button', { name: /musiqi/i })).not.toBeInTheDocument()
    expect(calls.play).toEqual([])
  })

  it('starts by itself inside the last three hours, on the ballot', async () => {
    show([open(90 * MIN)])
    await settle()
    expect(calls.play).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Final musiqisini söndür' })).toBeInTheDocument()
  })

  it('stays silent on a page that is not about the vote', async () => {
    show([open(90 * MIN)], '/login')
    await settle()
    expect(calls.play).toEqual([])
    expect(screen.queryByRole('button', { name: /musiqi/i })).not.toBeInTheDocument()
  })

  it('waits for the first touch when the browser blocks the sound, and the touch still does its own work', async () => {
    calls.result = 'blocked'
    show([open(90 * MIN)])
    await settle()
    expect(screen.getByRole('button', { name: 'Final musiqisini başlat' })).toBeInTheDocument()
    calls.result = 'playing'
    const clicked = vi.fn()
    document.body.addEventListener('click', clicked)
    fireEvent.click(document.body)
    await settle()
    expect(calls.play).toHaveLength(2)
    expect(clicked).toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Final musiqisini söndür' })).toBeInTheDocument()
  })

  it("remembers the reader's \"off\" on this device", async () => {
    const first = show([open(90 * MIN)])
    await settle()
    fireEvent.click(screen.getByRole('button', { name: 'Final musiqisini söndür' }))
    await settle()
    expect(calls.pause).toBe(1)
    expect(window.localStorage.getItem('pryzma:final-music')).toBe('off')
    expect(screen.getByRole('button', { name: 'Final musiqisini aç' })).toBeInTheDocument()
    first.unmount()

    calls.play = []
    show([open(90 * MIN)])
    await settle()
    expect(calls.play).toEqual([])
    expect(screen.getByRole('button', { name: 'Final musiqisini aç' })).toBeInTheDocument()
  })

  it('makes way when another tab takes the music', async () => {
    if (typeof BroadcastChannel === 'undefined') return
    show([open(90 * MIN)])
    await settle()
    const other = new BroadcastChannel('pryzma-final-music')
    other.postMessage({ type: 'claim', id: 'another-tab' })
    await act(() => new Promise((r) => setTimeout(r, 50)))
    other.close()
    expect(calls.pause).toBe(1)
  })
})
