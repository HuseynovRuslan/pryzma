import { useCallback, useEffect, useRef, useState } from 'react'
import { useLocation } from 'react-router-dom'
import { useNow } from '../lib/competitionLifecycle'
import { useFeaturedCompetition } from '../lib/featuredCompetitionContext'
import { audioSupported, FinalMusicEngine } from '../lib/finalMusic/engine'
import { finalPhaseOf, finalTargetFor, isMusicPhase, soonestFinal } from '../lib/finalMusic/schedule'
import { serverNow } from '../lib/serverClock'
import './FinalMusic.css'

/**
 * The music of a vote's last three hours, and the one button that silences it.
 *
 * On by default, at 20 %; the reader's "off" is remembered on this device and honoured in every tab. A browser that
 * will not play sound before the reader has touched the page gets the music on the first touch, click or key
 * press — that touch does whatever it was meant to do as well, the music only listens. Only the visible tab plays,
 * and of two visible tabs only the one used last, so the soundtrack never doubles up.
 *
 * It plays on the pages about the open vote only (lib/finalMusic/schedule), never touches the ballot, and goes
 * quiet on the close: the last-minute track ends on zero by itself.
 */

const PREF_KEY = 'pryzma:final-music'
const CHANNEL = 'pryzma-final-music'

function readPref(): boolean {
  try {
    return window.localStorage.getItem(PREF_KEY) !== 'off'
  } catch {
    return true
  }
}

function writePref(on: boolean) {
  try {
    window.localStorage.setItem(PREF_KEY, on ? 'on' : 'off')
  } catch {
    /* private mode: the choice lasts for this page only */
  }
}

type Status = 'idle' | 'blocked' | 'playing'

const GESTURES = ['pointerdown', 'touchend', 'click', 'keydown'] as const

export function FinalMusic() {
  const { slides } = useFeaturedCompetition()
  const { pathname } = useLocation()
  const now = useNow()
  const target = finalTargetFor(slides, pathname)
  const phase = finalPhaseOf(target?.endsAt ?? null, now)
  const active = target !== null && isMusicPhase(phase)
  /** Whether any open vote is still in its final hours, on any page. When none is, the audio device is released. */
  const anyFinal = isMusicPhase(finalPhaseOf(soonestFinal(slides)?.endsAt ?? null, now))

  const [enabled, setEnabled] = useState(readPref)
  const [status, setStatus] = useState<Status>('idle')
  const [visible, setVisible] = useState(() => typeof document === 'undefined' || !document.hidden)
  /** Another tab has taken the music; this one waits until the reader comes back to it. */
  const [yielded, setYielded] = useState(false)

  const engineRef = useRef<FinalMusicEngine | null>(null)
  const channelRef = useRef<BroadcastChannel | null>(null)
  const tabId = useRef(`${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`)
  const endsAtRef = useRef<number | null>(null)
  endsAtRef.current = target?.endsAt ?? null
  const buttonRef = useRef<HTMLButtonElement | null>(null)

  const shouldPlay = active && enabled && visible && !yielded && audioSupported()

  const start = useCallback(async () => {
    const endsAt = endsAtRef.current
    if (endsAt === null) return
    const result = await (engineRef.current ??= new FinalMusicEngine()).play(endsAt, serverNow())
    if (result === 'playing') {
      setStatus('playing')
      channelRef.current?.postMessage({ type: 'claim', id: tabId.current })
    } else setStatus(result === 'blocked' ? 'blocked' : 'idle')
  }, [])

  // Start and stop with the window, the page, the reader's choice and the tab's visibility.
  useEffect(() => {
    if (shouldPlay) {
      if (!engineRef.current?.isPlaying) void start()
    } else if (engineRef.current?.isPlaying) {
      void engineRef.current.pause(0.8).then(() => setStatus('idle'))
    } else setStatus('idle')
  }, [shouldPlay, start])

  // Once a second, with the countdown: the next pass of the bed, the last-minute track, the close.
  useEffect(() => {
    if (status === 'playing' && target) engineRef.current?.sync(target.endsAt, now)
  }, [now, status, target])

  // No vote is in its final hours any more (the close has come, or the server has closed it): let the audio go.
  // The last-minute track has already ended on zero; this only releases the device.
  useEffect(() => {
    if (anyFinal || !engineRef.current) return
    const e = engineRef.current
    engineRef.current = null
    setStatus('idle')
    const t = setTimeout(() => void e.dispose(), 1800)
    return () => clearTimeout(t)
  }, [anyFinal])

  // The browser refused to start the sound: the reader's first touch, click or key starts it.
  useEffect(() => {
    if (status !== 'blocked' || !shouldPlay) return
    const onGesture = (e: Event) => {
      // The music button handles its own press.
      if (buttonRef.current && e.target instanceof Node && buttonRef.current.contains(e.target)) return
      void start()
    }
    for (const g of GESTURES) window.addEventListener(g, onGesture, { capture: true, passive: true })
    return () => {
      for (const g of GESTURES) window.removeEventListener(g, onGesture, { capture: true })
    }
  }, [status, shouldPlay, start])

  // Visibility, the choice made in another tab, and which tab holds the music.
  useEffect(() => {
    const onVisibility = () => {
      setVisible(!document.hidden)
      if (!document.hidden) setYielded(false)
    }
    const onStorage = (e: StorageEvent) => {
      if (e.key === PREF_KEY) setEnabled(e.newValue !== 'off')
    }
    const onUse = () => setYielded(false)
    document.addEventListener('visibilitychange', onVisibility)
    window.addEventListener('storage', onStorage)
    window.addEventListener('pointerdown', onUse, { passive: true })
    window.addEventListener('focus', onUse)

    let channel: BroadcastChannel | null = null
    if (typeof BroadcastChannel !== 'undefined') {
      channel = new BroadcastChannel(CHANNEL)
      channel.onmessage = (e: MessageEvent<{ type: string; id: string }>) => {
        if (e.data?.type === 'claim' && e.data.id !== tabId.current) setYielded(true)
      }
      channelRef.current = channel
    }
    return () => {
      document.removeEventListener('visibilitychange', onVisibility)
      window.removeEventListener('storage', onStorage)
      window.removeEventListener('pointerdown', onUse)
      window.removeEventListener('focus', onUse)
      channel?.close()
      channelRef.current = null
    }
  }, [])

  // Leaving the site for good: release the audio device.
  useEffect(
    () => () => {
      void engineRef.current?.dispose()
      engineRef.current = null
    },
    [],
  )

  if (!active || !audioSupported()) return null

  const playing = status === 'playing'
  const waiting = enabled && !playing
  const label = playing ? 'Final musiqisini söndür' : waiting ? 'Final musiqisini başlat' : 'Final musiqisini aç'

  const onPress = () => {
    if (enabled && !playing) {
      // On but not sounding (the browser was waiting for a gesture, or another tab had it): this press starts it.
      setYielded(false)
      void start()
      return
    }
    const next = !enabled
    setEnabled(next)
    writePref(next)
    if (next) {
      setYielded(false)
      void start()
    }
  }

  return (
    <button
      ref={buttonRef}
      type="button"
      className={`final-music${playing ? ' final-music--on' : ''}${phase === 'lastMinute' || phase === 'lastTen' ? ' final-music--final' : ''}`}
      aria-label={label}
      title={label}
      onClick={onPress}
    >
      <svg className="final-music__icon" viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
        <path d="M4 9.5h3.2L12 5.5v13l-4.8-4H4z" fill="currentColor" />
        {/* Waves while the music is on (playing, or waiting for the first touch); crossed out only when switched off. */}
        {enabled ? (
          <path
            d="M15.5 9a4.2 4.2 0 0 1 0 6M18 6.5a7.8 7.8 0 0 1 0 11"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.8"
            strokeLinecap="round"
          />
        ) : (
          <path d="M16 9.5l5 5M21 9.5l-5 5" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
        )}
      </svg>
      <span className="final-music__text">{playing ? 'Musiqi' : waiting ? 'Musiqini başlat' : 'Musiqi sönülü'}</span>
    </button>
  )
}
