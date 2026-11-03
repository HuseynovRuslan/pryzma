/**
 * The final-hours soundtrack, on the Web Audio API.
 *
 * Why not an <audio loop> element: an MP3 loops with a gap (the encoder's padding), iOS ignores an element's
 * volume, and two elements cannot be cross-faded on a sample-exact clock. Here the bed is decoded once and laid
 * end to end with an equal-power cross-fade, the last-minute track is placed so that its final note lands on
 * the close of the vote, and every change of level is a ramp: nothing ever starts or stops at full volume.
 *
 * The engine holds no timers of its own beyond a fade's end: the page calls `sync()` once a second (the same tick
 * as the countdown) and the engine schedules whatever the next few seconds need. A hidden tab is paused by the
 * page, so a throttled timer can never leave the music running unattended.
 */

export const FINAL_MUSIC_SOURCES = {
  /** "Thinking Time" by Sonican (Pixabay, Pixabay Content License): trimmed, levelled, 96 kbps. */
  bed: '/audio/final-hours-bed.mp3',
  /** "Time Pressure (Action Tension Countdown Trailer Main)" by AudioAtlant (Pixabay, Pixabay Content License). */
  finale: '/audio/final-minute.mp3',
} as const

/** The starting volume: background, under whatever the reader is doing. */
export const DEFAULT_VOLUME = 0.2

/** Length of the cross-fade between two passes of the bed, in seconds. */
const LOOP_XFADE = 4
/** How long a fade-in from silence takes when the music starts or comes back. */
const FADE_IN = 2.5
/** The last-minute track is placed this long before the close (it is shorter than this). */
const FINALE_LEAD_S = 75
/** The last-minute track is fetched this long before the close. */
const FINALE_PREFETCH_MS = 10 * 60 * 1000

type Ctor = typeof AudioContext
const AudioCtor = (): Ctor | undefined =>
  typeof window === 'undefined'
    ? undefined
    : (window.AudioContext ?? (window as unknown as { webkitAudioContext?: Ctor }).webkitAudioContext)

export const audioSupported = () => AudioCtor() !== undefined

/** Equal-power curves, so a cross-fade has no dip in the middle. */
function curve(rising: boolean): Float32Array {
  const n = 64
  const c = new Float32Array(n)
  for (let i = 0; i < n; i++) {
    const x = i / (n - 1)
    c[i] = rising ? Math.sin((x * Math.PI) / 2) : Math.cos((x * Math.PI) / 2)
  }
  return c
}
const RISE = curve(true)
const FALL = curve(false)

interface Voice {
  src: AudioBufferSourceNode
  gain: GainNode
}

export type PlayResult = 'playing' | 'blocked' | 'unsupported'

export class FinalMusicEngine {
  private ctx: AudioContext | null = null
  private master: GainNode | null = null
  private bedBus: GainNode | null = null
  private finaleBus: GainNode | null = null
  private bed: AudioBuffer | null = null
  private finale: AudioBuffer | null = null
  private bedLoad: Promise<AudioBuffer | null> | null = null
  private finaleLoad: Promise<AudioBuffer | null> | null = null
  private voices: Voice[] = []
  /** Context time at which the next pass of the bed must start; null while the bed is not running. */
  private nextBedAt: number | null = null
  private finaleScheduled = false
  private running = false
  private stopTimer: ReturnType<typeof setTimeout> | undefined
  private readonly volume = DEFAULT_VOLUME
  private readonly sources: { bed: string; finale: string }

  constructor(sources: { bed: string; finale: string } = FINAL_MUSIC_SOURCES) {
    this.sources = sources
  }

  get isPlaying() {
    return this.running
  }

  /**
   * Starts the music, or reports that the browser will not let it start yet. Call it straight from the user's
   * gesture when there is one: the context is created and resumed before the first `await`.
   */
  async play(endsAt: number, now: number): Promise<PlayResult> {
    const Ctx = AudioCtor()
    if (!Ctx) return 'unsupported'
    if (!this.ctx) {
      this.ctx = new Ctx({ latencyHint: 'playback' })
      this.master = this.ctx.createGain()
      this.master.gain.value = 0
      this.master.connect(this.ctx.destination)
      this.bedBus = this.ctx.createGain()
      this.bedBus.connect(this.master)
      this.finaleBus = this.ctx.createGain()
      this.finaleBus.connect(this.master)
    }
    const ctx = this.ctx
    clearTimeout(this.stopTimer)
    const resumed = ctx.state === 'running' ? Promise.resolve() : ctx.resume().catch(() => undefined)
    // iOS unlocks its audio only for a sound started inside the gesture: an empty one is enough.
    try {
      const unlock = ctx.createBufferSource()
      unlock.buffer = ctx.createBuffer(1, 1, 22050)
      unlock.connect(ctx.destination)
      unlock.start()
    } catch {
      /* not needed where it fails */
    }
    // A resume the browser refuses neither resolves nor rejects in every browser: give it a moment, then look.
    await Promise.race([resumed, new Promise((r) => setTimeout(r, 400))])
    if ((ctx.state as string) !== 'running') return 'blocked'

    const bed = await this.loadBed()
    if (!bed || this.ctx !== ctx) return 'unsupported'
    if (this.running) return 'playing'
    // Back before a fade-out had finished: what was fading is let go, and the music comes in afresh.
    this.silenceVoices()
    this.running = true

    const t = ctx.currentTime + 0.05
    const g = this.master!.gain
    g.cancelScheduledValues(t)
    g.setValueAtTime(0, t)
    g.linearRampToValueAtTime(this.volume, t + FADE_IN)

    this.finaleScheduled = false
    this.nextBedAt = null
    const left = (endsAt - now) / 1000
    // Coming in during the last minute itself, with the last-minute track ready: that track alone, from where it
    // is now. Otherwise the bed, from its beginning.
    if (!(this.finale && left <= this.finale.duration)) {
      this.bedBus!.gain.cancelScheduledValues(t)
      this.bedBus!.gain.setValueAtTime(1, t)
      this.startBedPass(t, false)
    }
    this.sync(endsAt, now)
    return 'playing'
  }

  /**
   * Called about once a second while the music is playing: lays the next pass of the bed in good time, fetches
   * and places the last-minute track, and fades everything to nothing on the close.
   */
  sync(endsAt: number, now: number): void {
    const ctx = this.ctx
    if (!ctx || !this.running) return
    const left = (endsAt - now) / 1000

    if (left * 1000 <= FINALE_PREFETCH_MS) void this.loadFinale()

    if (this.nextBedAt !== null && ctx.currentTime >= this.nextBedAt - 8) this.startBedPass(this.nextBedAt, true)

    if (!this.finaleScheduled && this.finale && left <= FINALE_LEAD_S && left > 0.5) this.placeFinale(left)

    // No last-minute track (it did not load): the bed fades out over the last six seconds instead.
    if (!this.finaleScheduled && left <= 6 && left > 0) this.rampMaster(0, Math.max(left, 0.3))
    if (left <= 0) void this.pause(1.5)
  }

  /** Fades out and stops. The context is suspended, not closed, so the music can come back without a gesture. */
  pause(fadeSeconds = 0.8): Promise<void> {
    const ctx = this.ctx
    if (!ctx || !this.running) return Promise.resolve()
    this.running = false
    this.rampMaster(0, fadeSeconds)
    return new Promise((resolve) => {
      clearTimeout(this.stopTimer)
      this.stopTimer = setTimeout(() => {
        this.silenceVoices()
        if (!this.running) void ctx.suspend().catch(() => undefined)
        resolve()
      }, fadeSeconds * 1000 + 60)
    })
  }

  /** Releases everything: the vote is over, or the page is going away. */
  async dispose(): Promise<void> {
    await this.pause(0.4)
    clearTimeout(this.stopTimer)
    this.silenceVoices()
    const ctx = this.ctx
    this.ctx = null
    this.master = this.bedBus = this.finaleBus = null
    this.bed = this.finale = null
    this.bedLoad = this.finaleLoad = null
    this.running = false
    if (ctx) await ctx.close().catch(() => undefined)
  }

  private silenceVoices() {
    for (const v of this.voices) {
      try {
        v.src.stop()
      } catch {
        /* already stopped */
      }
      v.src.disconnect()
      v.gain.disconnect()
    }
    this.voices = []
    this.nextBedAt = null
    this.finaleScheduled = false
  }

  private rampMaster(to: number, seconds: number) {
    const ctx = this.ctx
    if (!ctx || !this.master) return
    const g = this.master.gain
    const t = ctx.currentTime
    g.cancelScheduledValues(t)
    g.setValueAtTime(g.value, t)
    g.linearRampToValueAtTime(to, t + seconds)
  }

  private track(src: AudioBufferSourceNode, gain: GainNode) {
    const voice = { src, gain }
    this.voices.push(voice)
    src.onended = () => {
      this.voices = this.voices.filter((v) => v !== voice)
      gain.disconnect()
    }
  }

  private startBedPass(at: number, fadeIn: boolean) {
    const ctx = this.ctx!
    const bed = this.bed!
    const src = ctx.createBufferSource()
    src.buffer = bed
    const gain = ctx.createGain()
    src.connect(gain).connect(this.bedBus!)
    const start = Math.max(at, ctx.currentTime + 0.02)
    const end = start + bed.duration
    // A curve may not share its start with another event in every browser, so the rising one stands alone.
    if (fadeIn) gain.gain.setValueCurveAtTime(RISE, start, LOOP_XFADE)
    else gain.gain.setValueAtTime(1, start)
    gain.gain.setValueCurveAtTime(FALL, end - LOOP_XFADE, LOOP_XFADE)
    src.start(start)
    src.stop(end + 0.05)
    this.track(src, gain)
    this.nextBedAt = end - LOOP_XFADE
  }

  /** Places the last-minute track so that it ends on the close, and lets the bed make way for it. */
  private placeFinale(leftSeconds: number) {
    const ctx = this.ctx!
    const finale = this.finale!
    this.finaleScheduled = true
    const now = ctx.currentTime
    const startIn = leftSeconds - finale.duration
    const at = now + Math.max(startIn, 0.05)
    const offset = startIn < 0 ? Math.min(-startIn, finale.duration - 0.5) : 0

    const src = ctx.createBufferSource()
    src.buffer = finale
    const gain = ctx.createGain()
    src.connect(gain).connect(this.finaleBus!)
    gain.gain.setValueCurveAtTime(RISE, at, offset > 0 ? 1.5 : 2)
    src.start(at, offset)
    this.track(src, gain)

    // The bed hands over: down across four seconds around the entry, and no more passes of it.
    const bus = this.bedBus!.gain
    const duckAt = Math.max(at - 1, now)
    bus.cancelScheduledValues(now)
    bus.setValueAtTime(bus.value, duckAt)
    bus.linearRampToValueAtTime(0, duckAt + 4)
    this.nextBedAt = null
  }

  private loadBed() {
    this.bedLoad ??= this.decode(this.sources.bed).then((b) => (this.bed = b))
    return this.bedLoad
  }

  private loadFinale() {
    this.finaleLoad ??= this.decode(this.sources.finale).then((b) => (this.finale = b))
    return this.finaleLoad
  }

  private async decode(url: string): Promise<AudioBuffer | null> {
    const ctx = this.ctx
    if (!ctx) return null
    try {
      const res = await fetch(url, { credentials: 'same-origin' })
      if (!res.ok) return null
      const data = await res.arrayBuffer()
      // The callback form as well: older Safari returns no promise from decodeAudioData.
      return await new Promise<AudioBuffer>((resolve, reject) => {
        const p = ctx.decodeAudioData(data, resolve, reject) as Promise<AudioBuffer> | undefined
        if (p && typeof p.then === 'function') p.then(resolve, reject)
      })
    } catch {
      return null
    }
  }
}
