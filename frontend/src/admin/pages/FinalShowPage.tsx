import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useParams } from 'react-router-dom'
import { shortCompetitionTitle } from '../../lib/format'
import { azGenitive, azOrdinal } from '../../lib/text'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { usePlatform } from '../../platform/context'
import { RequireAdmin } from '../AdminLayout'
import { adminApi, type FinalShow, type FinalShowWork } from '../api'
import { pictureBlob } from '../printPictures'
import { buildDeck, finalOrder, points, type Slide } from '../finalShow'
import './final-show.css'

/**
 * The final, on a television or a projector (D13): admins only, full screen, run by the presenter one step at a time —
 * no timers. It reads the locked final and changes nothing, so it can be rehearsed and run again; the position is in
 * the address (#12), so a reload comes back to the same screen.
 *
 * Keys: → / Space / Page Down — next; ← / Page Up — back; Home — the start; F — full screen. A presenter's clicker
 * sends Page Down / Page Up.
 */
export default function FinalShowPage() {
  return (
    <RequireAdmin>
      <ShowLoader />
    </RequireAdmin>
  )
}

function ShowLoader() {
  const { competitionId } = useParams()
  const id = Number(competitionId)
  const state = useApi((signal) => adminApi.finalShow(id, signal), [id])
  useTitle('Final')
  if (state.status === 'loading') return <div className="fs fs--plain">Final hazırlanır…</div>
  if (state.status === 'error')
    return (
      <div className="fs fs--plain" role="alert">
        <p>{state.error.message}</p>
        <a className="fs-link" href={`/admin/competitions/${id}?tab=final`}>
          Finala qayıt
        </a>
      </div>
    )
  return <Show show={state.data} />
}

function Show({ show }: { show: FinalShow }) {
  const { platformName } = usePlatform()
  const deck = useMemo(() => buildDeck(show), [show])
  const [at, setAt] = useState(() => clamp(Number(window.location.hash.slice(1)) || 0, deck.length))
  const picture = usePictures(show.works)
  const full = useFullscreen()

  const go = useCallback((next: number) => setAt(clamp(next, deck.length)), [deck.length])
  useEffect(() => {
    window.history.replaceState(null, '', `#${at}`)
  }, [at])
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLElement && e.target.closest('input, textarea')) return
      if (['ArrowRight', 'PageDown', ' ', 'Enter'].includes(e.key)) {
        if (e.key === 'Enter' && e.target instanceof HTMLButtonElement) return
        e.preventDefault()
        go(at + 1)
      } else if (['ArrowLeft', 'PageUp'].includes(e.key)) {
        e.preventDefault()
        go(at - 1)
      } else if (e.key === 'Home') go(0)
      else if (e.key === 'f' || e.key === 'F') full.toggle()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [at, go, full])

  const slide = deck[at]
  const brand = platformName.toLocaleUpperCase('az')
  const ctx: Ctx = { show, brand, picture, next: () => go(at + 1) }

  return (
    <div className="fs" data-kind={slide.kind}>
      <header className="fs-top">
        <span className="fs-top__name">
          {brand} · {shortCompetitionTitle(show.competitionTitle)}
        </span>
        <button type="button" className="fs-ghost" onClick={full.toggle}>
          {full.on ? 'Tam ekrandan çıx' : 'Tam ekran'}
        </button>
      </header>

      <main className="fs-stage" key={at} aria-live="polite">
        <SlideView slide={slide} ctx={ctx} />
      </main>

      <footer className="fs-bottom">
        <button type="button" className="fs-ghost" onClick={() => go(at - 1)} disabled={at === 0}>
          ‹ Geri
        </button>
        <span className="fs-progress tabular" aria-label={`Addım ${at + 1} / ${deck.length}`}>
          <span className="fs-progress__bar" style={{ inlineSize: `${((at + 1) / deck.length) * 100}%` }} />
        </span>
        {slide.kind === 'table' ? (
          <button type="button" className="fs-ghost" onClick={() => go(0)}>
            Şounu yenidən başlat
          </button>
        ) : slide.kind === 'blocked' || slide.kind === 'opening' ? (
          // The opening has its own large button; the end of a blocked show has nowhere to go.
          <span />
        ) : (
          <button type="button" className="fs-next" onClick={() => go(at + 1)}>
            {nextLabel(slide)} ›
          </button>
        )}
      </footer>
    </div>
  )
}

const clamp = (n: number, length: number) => Math.min(Math.max(0, Math.trunc(n)), length - 1)

function nextLabel(slide: Slide): string {
  switch (slide.kind) {
    case 'opening':
      return 'Finalı başlat'
    case 'juryIntro':
    case 'jury':
    case 'publicIntro':
    case 'public':
      return 'Növbəti nəticəni aç'
    case 'combineIntro':
    case 'place':
      return 'Növbəti yeri aç'
    case 'placeCard':
      return 'Aç'
    case 'winnerPause':
      return 'Qalibi aç'
    default:
      return 'Davam'
  }
}

interface Ctx {
  show: FinalShow
  brand: string
  picture: (w: FinalShowWork) => string | undefined
  next: () => void
}

/** "PRYZMA" → "PRYZMA-nın": the genitive of a name written in capitals, with its hyphen. */
const genitive = (name: string) => `${name}-${azGenitive(name.toLocaleLowerCase('az')).slice(name.length)}`

function SlideView({ slide, ctx }: { slide: Slide; ctx: Ctx }) {
  const { show, brand } = ctx
  switch (slide.kind) {
    case 'opening':
      return (
        <section className="fs-center fs-opening">
          <p className="fs-eyebrow">{shortCompetitionTitle(show.competitionTitle)}</p>
          <h1 className="fs-brand">
            {brand} <span>FINAL</span>
          </h1>
          <p className="fs-tagline">Bir mövzu, min baxış</p>
          <p className="fs-lede">
            Xalq səsverməsi tamamlandı.
            <br />
            Finalçılar münsiflər tərəfindən qiymətləndirildi.
          </p>
          <button type="button" className="fs-cta" onClick={ctx.next}>
            Finalı başlat
          </button>
        </section>
      )
    case 'finalists':
      return (
        <section className="fs-grid-slide">
          <h2 className="fs-title">{genitive(brand)} 10 finalçısı</h2>
          <ol className="fs-grid" role="list">
            {[...show.works]
              .sort((a, b) => (a.reference < b.reference ? -1 : 1))
              .map((w) => (
                <li key={w.submissionId} className="fs-grid__item">
                  <Art work={w} ctx={ctx} />
                  <span className="fs-ref tabular">{w.reference}</span>
                  {w.title && <span className="fs-grid__title">{w.title}</span>}
                </li>
              ))}
          </ol>
        </section>
      )
    case 'finalist':
      return (
        <Spread work={slide.work} ctx={ctx}>
          <p className="fs-eyebrow tabular">
            Finalçı {slide.index + 1} / {slide.count}
          </p>
          <WorkName work={slide.work} />
        </Spread>
      )
    case 'juryIntro':
      return <Intro title="Münsiflər necə qiymətləndirdi?" sub="Münsiflərin sıralaması — 10-cu yerdən 1-ci yerə." />
    case 'jury':
      return (
        <Spread work={slide.work} ctx={ctx} trail={<Trail works={slide.revealed} rank={(w) => w.juryRank} ctx={ctx} />}>
          <p className="fs-eyebrow">Münsiflərin sıralaması</p>
          <p className="fs-rank">{azOrdinal(slide.work.juryRank)} yer</p>
          <WorkName work={slide.work} />
          <dl className="fs-facts">
            <Fact label="Münsif balı" value={`${points(slide.work.juryTotal)} / ${points(show.juryMax)}`} />
            <Fact label="Münsif xalı" value={String(slide.work.juryPoints)} strong />
          </dl>
        </Spread>
      )
    case 'publicIntro':
      return <Intro title="Bəs xalq necə seçim etdi?" sub="Yalnız audit olunmuş etibarlı səslər sayılır." />
    case 'public':
      return (
        <Spread work={slide.work} ctx={ctx} trail={<Trail works={slide.revealed} rank={(w) => w.publicRank} ctx={ctx} />}>
          <p className="fs-eyebrow">Xalq səsverməsi</p>
          <p className="fs-rank">{azOrdinal(slide.work.publicRank)} yer</p>
          <WorkName work={slide.work} />
          <dl className="fs-facts">
            <Fact label="Etibarlı səs" value={String(slide.work.validVotes)} />
            <Fact label="Xalq sıralaması" value={String(slide.work.publicRank)} />
            <Fact label="Xalq xalı" value={String(slide.work.publicPoints)} strong />
          </dl>
        </Spread>
      )
    case 'combineIntro':
      return <Intro title="İndi nəticələri birləşdiririk." sub="Xalq xalı + münsif xalı = yekun xal" />
    case 'place':
      return (
        <Spread work={slide.work} ctx={ctx} trail={<Trail works={slide.revealed} rank={(w) => w.place ?? w.rank} ctx={ctx} />}>
          <p className="fs-eyebrow">Yekun nəticə</p>
          <p className="fs-rank">{azOrdinal(slide.work.place ?? slide.work.rank)} yer</p>
          {slide.work.tie === 'SharedPlace' && <p className="fs-note">Bərabər xal — yer bölünür</p>}
          <WorkName work={slide.work} />
          <Sum work={slide.work} />
        </Spread>
      )
    case 'blocked':
      return (
        <Intro
          title="Münsif qərarı tələb olunur"
          sub="Mükafat yerində tam bərabərlik var. Top 3 münsiflərin qərarı qeyd olunandan sonra açılacaq."
        />
      )
    case 'top3Intro':
      return <Intro title={`${brand} — TOP 3`} big />
    case 'placeCard':
      return (
        <section className="fs-center">
          <p className="fs-placecard">{azOrdinal(slide.place)} yer</p>
        </section>
      )
    case 'winnerPause':
      return (
        <section className="fs-center">
          <p className="fs-placecard fs-placecard--winner">{brand} qalibi…</p>
        </section>
      )
    case 'prize':
      return <Prize work={slide.work} place={slide.place} ctx={ctx} />
    case 'table':
      return <FinalTable ctx={ctx} />
  }
}

function Intro({ title, sub, big = false }: { title: string; sub?: string; big?: boolean }) {
  return (
    <section className="fs-center">
      <h2 className={big ? 'fs-brand' : 'fs-title fs-title--intro'}>{title}</h2>
      {sub && <p className="fs-lede">{sub}</p>}
    </section>
  )
}

/** A work large on one side, what is said about it on the other, and the ones already revealed underneath. */
function Spread({ work, ctx, children, trail }: { work: FinalShowWork; ctx: Ctx; children: ReactNode; trail?: ReactNode }) {
  return (
    <section className="fs-spread-wrap">
      <div className="fs-spread">
        <div className="fs-spread__art">
          <Art work={work} ctx={ctx} large />
        </div>
        <div className="fs-spread__text">{children}</div>
      </div>
      {trail}
    </section>
  )
}

function WorkName({ work }: { work: FinalShowWork }) {
  return (
    <div className="fs-work">
      <p className="fs-ref tabular">{work.reference}</p>
      {work.title && <p className="fs-work__title">{work.title}</p>}
    </div>
  )
}

function Fact({ label, value, strong = false }: { label: string; value: string; strong?: boolean }) {
  return (
    <div className={`fs-fact${strong ? ' fs-fact--strong' : ''}`}>
      <dt>{label}</dt>
      <dd className="tabular">{value}</dd>
    </div>
  )
}

function Sum({ work }: { work: FinalShowWork }) {
  return (
    <dl className="fs-sum">
      <div>
        <dt>Xalq</dt>
        <dd className="tabular">{work.publicPoints} xal</dd>
      </div>
      <div>
        <dt>Münsiflər</dt>
        <dd className="tabular">{work.juryPoints} xal</dd>
      </div>
      <div className="fs-sum__total">
        <dt>Yekun</dt>
        <dd className="tabular">{work.finalScore} xal</dd>
      </div>
    </dl>
  )
}

/** The ones revealed so far in this part, small, best at the right — so the room sees the ranking grow. */
function Trail({ works, rank, ctx }: { works: FinalShowWork[]; rank: (w: FinalShowWork) => number; ctx: Ctx }) {
  return (
    <ol className="fs-trail" role="list" aria-label="Açılanlar">
      {works.map((w) => (
        <li key={w.submissionId} className="fs-trail__item">
          <Art work={w} ctx={ctx} />
          <span className="fs-trail__rank tabular">{rank(w)}</span>
        </li>
      ))}
    </ol>
  )
}

function Prize({ work, place, ctx }: { work: FinalShowWork; place: number; ctx: Ctx }) {
  const { show } = ctx
  return (
    <section className={`fs-spread-wrap fs-prize${place === 1 ? ' fs-prize--winner' : ''}`}>
      {place === 1 && <span className="fs-glow" aria-hidden="true" />}
      <div className="fs-spread">
        <div className="fs-spread__art">
          <Art work={work} ctx={ctx} large />
        </div>
        <div className="fs-spread__text">
          <p className="fs-eyebrow">{place === 1 ? `${ctx.brand} qalibi` : `${ctx.brand} — Top 3`}</p>
          <p className="fs-rank">{azOrdinal(place)} yer</p>
          {work.tie === 'DecidedByJury' && <p className="fs-note">Bərabərlik münsiflərin qərarı ilə həll olunub</p>}
          <WorkName work={work} />
          <p className="fs-score tabular">
            {work.finalScore} <span>xal</span>
          </p>
          {work.prizeAzn !== null && <p className="fs-money tabular">{work.prizeAzn} AZN</p>}
          <dl className="fs-why">
            <div>
              <dt>Xalq</dt>
              <dd className="tabular">
                {work.validVotes} etibarlı səs · <strong>{work.publicPoints} xal</strong>
              </dd>
            </div>
            <div>
              <dt>Münsiflər</dt>
              <dd className="tabular">
                {points(work.juryTotal)} / {points(show.juryMax)} · <strong>{work.juryPoints} xal</strong>
              </dd>
            </div>
            <div className="fs-why__total">
              <dt>Yekun</dt>
              <dd className="tabular">
                <strong>{work.finalScore} xal</strong>
              </dd>
            </div>
          </dl>
        </div>
      </div>
    </section>
  )
}

function FinalTable({ ctx }: { ctx: Ctx }) {
  const { show } = ctx
  const rows = finalOrder(show.works)
  return (
    <section className="fs-table-slide">
      <h2 className="fs-title">Yekun nəticələr</h2>
      <table className="fs-table">
        <thead>
          <tr>
            <th scope="col">Yer</th>
            <th scope="col">İş</th>
            <th scope="col" className="fs-num">
              Etibarlı səs
            </th>
            <th scope="col" className="fs-num">
              Xalq xalı
            </th>
            <th scope="col" className="fs-num">
              Münsif balı
            </th>
            <th scope="col" className="fs-num">
              Münsif xalı
            </th>
            <th scope="col" className="fs-num">
              Yekun xal
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((w) => (
            <tr key={w.submissionId} className={w.place !== null && w.place <= 3 ? 'is-top' : undefined}>
              <td className="tabular">{w.place ?? w.rank}</td>
              <td>
                <span className="fs-table__work">
                  <Art work={w} ctx={ctx} />
                  <span>
                    <span className="tabular">{w.reference}</span>
                    {w.publicRank === 1 && <span className="fs-tag">Xalq səsverməsində 1-ci</span>}
                    {w.juryRank === 1 && <span className="fs-tag">Münsiflərin ən yüksək balı</span>}
                    {w.title && <span className="fs-table__title">{w.title}</span>}
                  </span>
                </span>
              </td>
              <td className="fs-num tabular">{w.validVotes}</td>
              <td className="fs-num tabular">{w.publicPoints}</td>
              <td className="fs-num tabular">
                {points(w.juryTotal)} / {points(show.juryMax)}
              </td>
              <td className="fs-num tabular">{w.juryPoints}</td>
              <td className="fs-num tabular">
                <strong>{w.finalScore}</strong>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}

function Art({ work, ctx, large = false }: { work: FinalShowWork; ctx: Ctx; large?: boolean }) {
  const url = ctx.picture(work)
  return (
    <span className={`fs-art${large ? ' fs-art--large' : ''}`}>
      {url ? <img src={url} alt={`${work.reference} ${work.title ?? ''}`.trim()} /> : <span className="fs-art__none" />}
    </span>
  )
}

// ---- pictures, full screen ----------------------------------------------------------------------------------------

/**
 * Every finalist's picture fetched once, at the start, with the admin's own credentials (nothing is public yet) —
 * the large metadata-free copy where it is ready, and for a PDF the page that stands for it — so no reveal waits on
 * the network.
 */
function usePictures(works: FinalShowWork[]) {
  const [urls, setUrls] = useState<Record<number, string>>({})
  useEffect(() => {
    const controller = new AbortController()
    const made: string[] = []
    void Promise.all(
      works
        .filter((w) => w.image)
        .map(async (w) => {
          try {
            const blob = await pictureBlob(w.submissionId, w.image!, controller.signal)
            if (controller.signal.aborted) return
            const url = URL.createObjectURL(blob)
            made.push(url)
            setUrls((all) => ({ ...all, [w.submissionId]: url }))
          } catch {
            // The work is shown without its picture rather than holding up the show.
          }
        }),
    )
    return () => {
      controller.abort()
      for (const url of made) URL.revokeObjectURL(url)
    }
  }, [works])
  return useCallback((w: FinalShowWork) => urls[w.submissionId], [urls])
}

function useFullscreen() {
  const [on, setOn] = useState(() => Boolean(document.fullscreenElement))
  useEffect(() => {
    const changed = () => setOn(Boolean(document.fullscreenElement))
    document.addEventListener('fullscreenchange', changed)
    return () => document.removeEventListener('fullscreenchange', changed)
  }, [])
  const toggle = useCallback(() => {
    if (document.fullscreenElement) void document.exitFullscreen?.()
    else void document.documentElement.requestFullscreen?.()
  }, [])
  return useMemo(() => ({ on, toggle }), [on, toggle])
}
