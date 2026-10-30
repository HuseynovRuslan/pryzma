import { useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDateTime, shortCompetitionTitle } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { usePlatform } from '../../platform/context'
import { RequireAdmin } from '../AdminLayout'
import { adminApi, type LeaderboardPrint, type LeaderboardPrintRow } from '../api'
import { chunk, FULL_ROWS_PER_PAGE, leaderboardPrintHref } from '../print'
import { usePictures, type Picture } from '../printPictures'
import { PdfBadge } from '../PdfBadge'
import './print.css'
import './leaderboard-print.css'

/**
 * The public vote on paper, for the admin (owner, 7 October 2026) — a document of its own, never handed out with the
 * jury's sheets:
 *
 * - `mode=top10`: the ten finalists from the FROZEN snapshot (never recalculated) with valid votes and public points,
 *   the totals the snapshot was taken from and when.
 * - `mode=full`: every work on the ballot by valid votes, in competition ranking (1, 2, 2, 4) — marked as the audited
 *   public ranking, which decides no official place.
 *
 * Shown as the works themselves by default — four cards to an A4 page, each with its rank, picture, reference,
 * title, nomination, author (an internal document; the admin can untick it) and valid votes — or, as an option, as a
 * compact table (`view=table`). No voter appears anywhere. Each sheet on screen is one printed page, numbered.
 */
export default function LeaderboardPrintPage() {
  return (
    <RequireAdmin>
      <LeaderboardPrintView />
    </RequireAdmin>
  )
}

function LeaderboardPrintView() {
  const { competitionId } = useParams()
  const id = Number(competitionId)
  const [params, setParams] = useSearchParams()
  const mode = params.get('mode') === 'full' ? 'Full' : 'Top10'
  const view = params.get('view') === 'table' ? 'table' : 'cards'
  const withAuthor = params.get('author') !== '0'
  const state = useApi((signal) => adminApi.leaderboardPrint(id, mode, withAuthor, signal), [id, mode, withAuthor])
  const { platformName } = usePlatform()
  const brand = platformName.toLocaleUpperCase('az')
  const data = state.status === 'success' ? state.data : null
  const name = data ? shortCompetitionTitle(data.competitionTitle) : ''
  // The document's title is the PDF's default file name.
  useTitle(data ? `${name} — ${mode === 'Top10' ? 'Top 10 xalq nəticəsi' : 'tam səs sıralaması'}` : 'Çap')

  const items = useMemo(() => {
    if (!data) return undefined
    // The table of the full ranking has no pictures; everything else shows them.
    return view === 'table' && data.mode === 'Full' ? [] : data.rows.map((r) => r.item)
  }, [data, view])
  const picture = usePictures(items)

  const pages = useRef<HTMLDivElement>(null)
  const [overflow, setOverflow] = useState(false)
  // A safety net, not a layout: a sheet whose content no longer fits one A4 page is said on screen before printing.
  useLayoutEffect(() => {
    const sheets = pages.current ? [...pages.current.querySelectorAll<HTMLElement>('.pr-sheet')] : []
    setOverflow(sheets.some((s) => s.scrollHeight > s.clientHeight + 1))
  }, [data, view, picture])

  const set = (patch: Record<string, string | null>) => {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(patch)) {
      if (v) next.set(k, v)
      else next.delete(k)
    }
    setParams(next, { replace: true })
  }

  if (state.status === 'loading' && !data) return <PageLoading label="Çap üçün hazırlanır" />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  const d = data!
  const sheets =
    view === 'cards' ? chunk(d.rows, 4) : d.mode === 'Top10' ? [d.rows] : chunk(d.rows, FULL_ROWS_PER_PAGE)
  const withImage = (items ?? []).filter((i) => i.image)
  const waiting = withImage.some((i) => picture(i) === undefined)
  const doc = d.mode === 'Top10' ? 'Top 10 xalq nəticəsi' : 'Tam səs sıralaması'

  return (
    <div className="pr lb">
      <div className="pr-bar">
        <div className="pr-bar__row">
          <div className="pr-bar__text">
            <p className="pr-bar__eyebrow">Xalq səsverməsi · çap</p>
            <h1 className="pr-bar__title">{doc}</h1>
            <p className="pr-bar__meta">
              <span className="tabular">{d.rows.length}</span> iş · <span className="tabular">{sheets.length}</span> səhifə (A4)
            </p>
          </div>
          <div className="pr-bar__actions">
            <button type="button" className="btn btn--primary" onClick={() => window.print()} disabled={waiting}>
              Çap et
            </button>
            <Link to={`/admin/competitions/${id}?tab=final`} className="btn">
              Geri
            </Link>
          </div>
        </div>
        <nav className="pr-bar__row pr-bar__row--options" aria-label="Sənəd">
          <Link to={leaderboardPrintHref(id, 'top10')} aria-current={d.mode === 'Top10' ? 'page' : undefined} className="lb-mode">
            Top 10 xalq nəticəsi
          </Link>
          <Link to={leaderboardPrintHref(id, 'full')} aria-current={d.mode === 'Full' ? 'page' : undefined} className="lb-mode">
            Tam səs sıralaması
          </Link>
        </nav>
        <div className="pr-bar__row pr-bar__row--options">
          <fieldset className="pr-layout">
            <legend className="pr-layout__legend">Görünüş</legend>
            <label className="pr-radio">
              <input type="radio" name="view" checked={view === 'cards'} onChange={() => set({ view: null })} />
              Şəkilli görünüş — 4 iş / A4
            </label>
            <label className="pr-radio">
              <input type="radio" name="view" checked={view === 'table'} onChange={() => set({ view: 'table' })} />
              Cədvəl görünüşü
            </label>
          </fieldset>
          <label className="pr-check">
            <input type="checkbox" checked={withAuthor} onChange={(e) => set({ author: e.target.checked ? null : '0' })} />
            Müəllif adını göstər
          </label>
        </div>
        <p className="pr-bar__status" role="status">
          {waiting ? `Şəkillər yüklənir: ${withImage.filter((i) => picture(i) !== undefined).length} / ${withImage.length}` : 'Hazırdır.'}
        </p>
        {overflow && (
          <p className="pr-bar__warn" role="alert">
            Bəzi səhifələrin məzmunu A4 vərəqinə sığmır — çapda növbəti səhifəyə keçə bilər.
          </p>
        )}
        <p className="pr-bar__hint">
          Ayrıca sənəddir — münsif vərəqləri ilə birlikdə verilmir. PDF üçün çap pəncərəsində “PDF kimi saxla”nı seçin; miqyas
          100%.
        </p>
      </div>

      <div className="pr-pages" data-mode={d.mode} data-view={view} ref={pages}>
        {view === 'cards'
          ? sheets.map((rows, i) => (
              <section key={rows[0].item.id} className="pr-sheet pr-sheet--four lb-sheet" aria-label={`Səhifə ${i + 1}`}>
                <CardsHead d={d} brand={brand} name={name} />
                <div className="pr-grid">
                  {rows.map((r) => (
                    <VoteCard key={r.item.id} r={r} top={d.mode === 'Top10'} picture={picture(r.item)} />
                  ))}
                </div>
                <Foot brand={brand} page={i + 1} pages={sheets.length} d={d} />
              </section>
            ))
          : d.mode === 'Top10'
            ? <Top10Table d={d} brand={brand} name={name} picture={picture} />
            : sheets.map((rows, i) => (
                <FullTable key={rows[0]?.item.id ?? i} d={d} rows={rows} brand={brand} name={name} page={i + 1} pages={sheets.length} />
              ))}
      </div>
    </div>
  )
}

// ---- the cards: four works to a page ----------------------------------------------------------------------------

const NOTE_TOP10 = 'Bu siyahı final mərhələsi üçün dondurulmuş auditdən keçmiş xalq səsverməsi nəticəsidir.'
const NOTE_FULL = 'Bu sıralama xalq səsverməsinin nəticəsidir və rəsmi 1-ci, 2-ci, 3-cü yerləri avtomatik müəyyən etmir.'

function CardsHead({ d, brand, name }: { d: LeaderboardPrint; brand: string; name: string }) {
  const top = d.mode === 'Top10'
  return (
    <header className="lb-head lb-head--cards">
      <p className="lb-head__competition">
        {brand} · {name}
      </p>
      <h2 className="lb-head__title">{top ? `${brand} — Xalq səsverməsinin nəticələri` : 'Xalq səsverməsinin auditdən keçmiş sıralaması'}</h2>
      <p className="lb-head__meta">
        {top ? (
          <>
            Auditdən keçmiş etibarlı səslər əsasında · Dondurulub: <span className="tabular">{formatDateTime(d.frozenAt)}</span>{' '}
            (Bakı vaxtı)
          </>
        ) : (
          <>Yalnız etibarlı səslər · bərabər səs — bərabər sıra</>
        )}{' '}
        · Etibarlı: <strong className="tabular">{d.validVotes}</strong> · Etibarsız sayılmış:{' '}
        <strong className="tabular">{d.invalidatedVotes}</strong> · Ümumi: <strong className="tabular">{d.totalVotes}</strong>
      </p>
      <p className="lb-head__note">{top ? NOTE_TOP10 : NOTE_FULL}</p>
    </header>
  )
}

function VoteCard({ r, top, picture }: { r: LeaderboardPrintRow; top: boolean; picture: Picture | undefined }) {
  const item = r.item
  const facts = [
    ...(item.category ? [item.category] : []),
    ...item.facts.map((f) => `${f.label}: ${f.value}`),
  ]
  return (
    <article className="pr-card lb-card" aria-label={`${r.rank}: ${item.reference ?? item.title}`}>
      <div className="lb-card__top">
        <p className="lb-card__rank">
          <span className="lb-card__rank-n tabular">{r.rank}</span>
          <span className="lb-card__rank-label">xalq sıralaması</span>
        </p>
        <div className="lb-card__votes">
          <p className="lb-card__count tabular">
            <strong>{r.validVotes}</strong> etibarlı səs
          </p>
          {top && r.publicPoints !== null && (
            <p className="lb-card__points tabular">
              Xalq xalı: <strong>{r.publicPoints}</strong>
            </p>
          )}
        </div>
      </div>
      <p className="pr-card__ref tabular">
        {item.reference ?? '—'}
        <PdfBadge item={item} />
      </p>
      <h3 className="pr-card__title">{item.title}</h3>
      {(facts.length > 0 || item.author) && (
        <p className="pr-meta lb-card__meta">
          {facts.join(' · ')}
          {item.author && (
            <>
              {facts.length > 0 && <br />}
              Müəllif: {item.author}
            </>
          )}
        </p>
      )}
      <figure className="pr-figure">
        {!item.image ? (
          <span className="pr-figure__none">Şəkil yoxdur</span>
        ) : picture === undefined ? (
          <span className="pr-figure__none">Şəkil yüklənir…</span>
        ) : picture === 'failed' ? (
          <span className="pr-figure__none">Şəkil yüklənmədi</span>
        ) : (
          <img className="pr-figure__img" src={picture.url} alt={`${item.reference ?? ''} ${item.title}`.trim()} />
        )}
      </figure>
    </article>
  )
}

function Foot({ brand, page, pages, d }: { brand: string; page: number; pages: number; d: LeaderboardPrint }) {
  return (
    <footer className="pr-foot lb-foot">
      <span>
        {brand} · səs vəziyyətinin izi <span className="tabular">{d.votesFingerprint.slice(0, 12)}</span>
      </span>
      <span className="tabular">
        Səhifə {page} / {pages}
      </span>
    </footer>
  )
}

// ---- the optional table view --------------------------------------------------------------------------------------

function Totals({ d }: { d: LeaderboardPrint }) {
  return (
    <p className="lb-totals tabular">
      Etibarlı səslər: <strong>{d.validVotes}</strong> · Etibarsız sayılmış: <strong>{d.invalidatedVotes}</strong> · Ümumi:{' '}
      <strong>{d.totalVotes}</strong>
    </p>
  )
}

function Top10Table({
  d,
  brand,
  name,
  picture,
}: {
  d: LeaderboardPrint
  brand: string
  name: string
  picture: (item: LeaderboardPrintRow['item']) => Picture | undefined
}) {
  return (
    <section className="pr-sheet lb-sheet lb-sheet--landscape" aria-label="Səhifə 1">
      <header className="lb-head">
        <p className="lb-head__competition">{name}</p>
        <h2 className="lb-head__title">{brand} — Xalq səsverməsinin nəticələri</h2>
        <p className="lb-head__sub">Auditdən keçmiş etibarlı səslər əsasında</p>
        <p className="lb-head__meta">
          Dondurulub: <span className="tabular">{formatDateTime(d.frozenAt)}</span> (Bakı vaxtı)
        </p>
        <Totals d={d} />
      </header>
      <table className="lb-table lb-table--top">
        <colgroup>
          <col className="lb-col-rank" />
          <col className="lb-col-thumb" />
          <col />
          <col className="lb-col-votes" />
          <col className="lb-col-points" />
        </colgroup>
        <thead>
          <tr>
            <th scope="col" className="lb-num">
              Yer
            </th>
            <th scope="col" colSpan={2}>
              İş
            </th>
            <th scope="col" className="lb-num">
              Etibarlı səs
            </th>
            <th scope="col" className="lb-num">
              Xalq xalı
            </th>
          </tr>
        </thead>
        <tbody>
          {d.rows.map((r) => {
            const shot = picture(r.item)
            return (
              <tr key={r.item.id}>
                <td className="lb-num lb-rank tabular">{r.rank}</td>
                <td className="lb-thumb">
                  {shot && shot !== 'failed' ? <img src={shot.url} alt="" /> : <span className="lb-thumb__none" aria-hidden="true" />}
                </td>
                <td className="lb-work">
                  <span className="lb-ref tabular">{r.item.reference}</span>
                  <span className="lb-title">
                    {r.item.title}
                    {r.item.author && <> · Müəllif: {r.item.author}</>}
                  </span>
                </td>
                <td className="lb-num tabular">
                  <strong>{r.validVotes}</strong> etibarlı səs
                </td>
                <td className="lb-num tabular">
                  Xalq xalı: <strong>{r.publicPoints}</strong>
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
      <p className="lb-note">{NOTE_TOP10}</p>
      <Foot brand={brand} page={1} pages={1} d={d} />
    </section>
  )
}

function FullTable({
  d,
  rows,
  brand,
  name,
  page,
  pages,
}: {
  d: LeaderboardPrint
  rows: LeaderboardPrintRow[]
  brand: string
  name: string
  page: number
  pages: number
}) {
  return (
    <section className="pr-sheet lb-sheet lb-sheet--full" aria-label={`Səhifə ${page}`}>
      <header className="lb-head lb-head--compact">
        <p className="lb-head__competition">
          {brand} · {name}
        </p>
        <h2 className="lb-head__title">Xalq səsverməsinin auditdən keçmiş sıralaması</h2>
        <p className="lb-head__sub">{NOTE_FULL}</p>
        <p className="lb-head__meta">
          Yalnız etibarlı səslər, bərabər səs — bərabər sıra · çap: <span className="tabular">{formatDateTime(d.generatedAt)}</span>{' '}
          (Bakı vaxtı)
        </p>
        <Totals d={d} />
      </header>
      <table className="lb-table lb-table--full">
        <colgroup>
          <col className="lb-col-rank" />
          <col className="lb-col-ref" />
          <col />
          <col className="lb-col-votes" />
        </colgroup>
        <thead>
          <tr>
            <th scope="col" className="lb-num">
              Sıra
            </th>
            <th scope="col">İş</th>
            <th scope="col">Ad{rows.some((r) => r.item.author) ? ' · müəllif' : ''}</th>
            <th scope="col" className="lb-num">
              Etibarlı səs
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.item.id}>
              <td className="lb-num tabular">{r.rank}</td>
              <td className="lb-ref tabular">{r.item.reference}</td>
              <td className="lb-title">
                {r.item.title || '—'}
                {r.item.author && <span className="lb-author"> · {r.item.author}</span>}
              </td>
              <td className="lb-num tabular">{r.validVotes}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <Foot brand={brand} page={page} pages={pages} d={d} />
    </section>
  )
}
