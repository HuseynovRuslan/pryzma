import { useEffect, useMemo, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { VoteBar, VoteBarKey } from '../../components/VoteBar'
import { formatDateTime } from '../../lib/format'
import { placeName } from '../../lib/text'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { catalogNumber } from '../../pages/voting/lib'
import { usePlatform } from '../../platform/context'
import { RequireAdmin } from '../AdminLayout'
import type { AdminPrintItem } from '../api'
import {
  CATALOG_PER_PAGE,
  juryAdminApi,
  markKeys,
  RANKING_ROWS,
  type JuryMatrixJuror,
  type JuryPrint,
  type JuryPrintWork,
  type JuryRanking,
} from '../juryApi'
import { chunk } from '../print'
import { materialPages, pdfBadge, useMaterial, usePrintPictures, type MaterialPage, type Picture } from '../printPictures'
import { VoteAnalyticsPrint } from './VoteAnalyticsPrint'
import './print.css'
import './jury-print.css'

/** "M1–M5" — the score fields of the jurors actually configured. */
const markRange = (keys: string[]) => (keys.length === 0 ? '' : keys.length === 1 ? keys[0] : `${keys[0]}–${keys[keys.length - 1]}`)

/** What the juror is asked, at the top of every catalog page. */
const catalogInstruction = (keys: string[]) =>
  `Hər işə 1-dən 10-a qədər tam bal yazın — yalnız öz xananıza${keys.length > 0 ? ` (${markRange(keys)})` : ''}. ` +
  'İşlər xalq səsverməsinin etibarlı səslərinə görə sıralanıb; yer və səs sayları məlumat üçündür.'

/** A card's picture is about 90 × 65 mm on paper: 900 px is about 250 dpi, and keeps the 99-work PDF light. */
const CATALOG_PRINT_PX = 900

/** Up to 15 jurors the fields stay five to a row (three rows at most); more share three rows, none is left out. */
const markColumns = (jurors: number) => (jurors <= 15 ? 5 : Math.ceil(jurors / 3))

/**
 * The jury's paper documents for the evaluation of every work (InternalJuryRankingV1):
 * <list>
 * - the catalog — the evaluation form itself: every work of the frozen roster, most valid public votes first, four to
 *   an A4 page (2×2). Each card: the work's public place ("1-ci yer", level works marked), its short number
 *   ("İş №90"), title, the picture that stands for it, its valid votes and the votes approved audit decisions set
 *   aside, with their bar — and one score field per juror actually configured (M1, M2, …), named in the key at the top
 *   of every page. Only when asked, the whole material after it;
 * - the internal ranking, once every juror has submitted: every work by its exact average, ties marked as ties —
 *   an internal document, marked as such on every page.
 * </list>
 * No author, no voter, no audit rule or evidence, no other juror's score and no long reference (SUB-…). What is seen on
 * screen is what prints.
 */
export default function JuryPrintPage() {
  return (
    <RequireAdmin>
      <PrintView />
    </RequireAdmin>
  )
}

/** The print data as the shared print helpers read it: no author, no description — the catalog is a visual index. */
const asItem = (w: JuryPrintWork): AdminPrintItem => ({
  id: w.submissionId,
  reference: w.reference,
  title: w.title,
  category: null,
  facts: [],
  conceptLabel: null,
  concept: null,
  image: w.image,
  author: null,
  pdfPages: w.pdfPages,
  material: w.material,
})

function PrintView() {
  const { competitionId = '0' } = useParams()
  const id = Number(competitionId)
  const [params, setParams] = useSearchParams()
  const requested = params.get('doc')
  // An old "sheets" link opens the catalog: it is the score sheet now.
  const doc = requested === 'ranking' || requested === 'analytics' ? requested : 'catalog'
  // The admin-only vote analytics reads its own endpoint and needs no frozen roster.
  const analytics = doc === 'analytics'
  const order = params.get('order') === 'reference' ? 'reference' : 'votes'
  const onlyInvalidated = params.get('only') === 'invalidated'
  const withMaterial = params.get('material') === '1'

  const state = useApi<JuryPrint | undefined>((signal) => (analytics ? Promise.resolve(undefined) : juryAdminApi.print(id, signal)), [id, analytics])
  const [data, setData] = useState<JuryPrint>()
  useEffect(() => {
    if (state.status === 'success' && state.data) setData(state.data)
  }, [state.status, state.data])

  const set = (patch: Record<string, string | null>) => {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(patch)) {
      if (v) next.set(k, v)
      else next.delete(k)
    }
    setParams(next, { replace: true })
  }

  const title =
    doc === 'catalog'
      ? `${data?.competitionTitle ?? 'Kataloq'} — münsif kataloqu (${CATALOG_PER_PAGE} iş / səhifə)`
      : doc === 'ranking'
        ? `${data?.competitionTitle ?? 'Reytinq'} — münsiflərin daxili reytinqi`
        : 'Səs analitikası — bütün işlər (yalnız admin)'
  useTitle(title)

  if (!analytics && !data) {
    if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
    return <PageLoading label="Çap üçün hazırlanır" />
  }

  return (
    <div className="pr jp">
      <div className="pr-bar">
        <div className="pr-bar__row">
          <div className="pr-bar__text">
            <p className="pr-bar__eyebrow">{analytics ? 'Admin analitikası · münsiflərə verilmir' : 'Münsiflər · bütün işlər'}</p>
            <h1 className="pr-bar__title">{title}</h1>
          </div>
          <div className="pr-bar__actions">
            <button type="button" className="btn btn--primary" onClick={() => window.print()}>
              Çap et
            </button>
            <Link to={`/admin/competitions/${id}?tab=jury`} className="btn">
              Geri
            </Link>
          </div>
        </div>
        <div className="pr-bar__row pr-bar__row--options">
          <fieldset className="pr-layout">
            <legend className="pr-layout__legend">Sənəd</legend>
            <label className="pr-radio">
              <input type="radio" name="doc" checked={doc === 'catalog'} onChange={() => set({ doc: 'catalog' })} />
              Münsif kataloqu
            </label>
            <label className="pr-radio">
              <input type="radio" name="doc" checked={doc === 'ranking'} onChange={() => set({ doc: 'ranking', material: null })} />
              Daxili reytinq
            </label>
            <label className="pr-radio">
              <input type="radio" name="doc" checked={analytics} onChange={() => set({ doc: 'analytics', material: null })} />
              Səs analitikası (yalnız admin)
            </label>
          </fieldset>
          {doc === 'catalog' && (
            <label className="pr-check">
              <input type="checkbox" checked={withMaterial} onChange={(e) => set({ material: e.target.checked ? '1' : null })} />
              Bütün materialı əlavə et (hər şəkil və PDF səhifəsi)
            </label>
          )}
        </div>
        <p className="pr-bar__hint">
          {analytics
            ? 'Bu sənəd yalnız adminlər üçündür: səslər audit qaydaları üzrə buradadır, münsiflərə verilmir. Münsif kataloqunda yalnız yer və səs sayları var.'
            : doc === 'catalog'
              ? 'Kataloq münsiflərin qiymətləndirmə formasıdır: hər işin xalq səsverməsindəki yeri, etibarlı və etibarsız sayılmış səsləri və hər münsifin bal xanası. Müəllif, səs verənlər və audit qaydaları yoxdur. Yalnız münsiflərə və səlahiyyətli təşkilatçılara verilir.'
              : 'Daxili sənəd: münsiflərə və ictimaiyyətə verilmir.'}{' '}
          PDF üçün “PDF kimi saxla”; A4, miqyas 100%.
        </p>
      </div>
      {analytics ? (
        <VoteAnalyticsPrint competitionId={id} order={order} onlyInvalidated={onlyInvalidated} set={set} />
      ) : !data ? null : doc === 'catalog' ? (
        <Catalog data={data} withMaterial={withMaterial} />
      ) : (
        <Ranking competitionId={id} />
      )}
    </div>
  )
}

// ---- The catalog ------------------------------------------------------------------------------------------------

function Catalog({ data, withMaterial }: { data: JuryPrint; withMaterial: boolean }) {
  const { platformName } = usePlatform()
  const items = useMemo(() => data.works.map(asItem), [data.works])
  const picture = usePrintPictures(items, CATALOG_PRINT_PX)
  const appendix = useMemo(() => (withMaterial ? materialPages(items) : undefined), [withMaterial, items])
  const page = useMaterial(appendix)
  const sheets = chunk(data.works, CATALOG_PER_PAGE)
  const extra = appendix ?? []
  const total = sheets.length + extra.length
  const keys = markKeys(data.jurors)
  const instruction = catalogInstruction(keys)
  // Every bar against the most-voted work, on every page alike.
  const max = Math.max(1, ...data.works.map((w) => w.vote.validVotes + w.vote.invalidatedVotes))

  const withImage = items.filter((i) => i.image)
  const settled = withImage.filter((i) => picture(i) !== undefined).length + extra.filter((p) => page(p) !== undefined).length
  const expected = withImage.length + extra.length

  return (
    <>
      <p className="pr-bar__status jp-status" role="status">
        {data.works.length} iş · {total} səhifə (A4){extra.length > 0 && ` — o cümlədən material əlavəsi: ${extra.length}`} ·{' '}
        {keys.length > 0 ? `${keys.length} münsif (${markRange(keys)})` : 'münsif yoxdur'} ·{' '}
        {settled === expected ? 'Bütün şəkillər yükləndi.' : `Şəkillər yüklənir: ${settled} / ${expected}`}
      </p>
      {keys.length === 0 && (
        <p className="pr-bar__warn jp-status">Münsif təyin edilməyib: kataloqda bal xanası olmayacaq. Əvvəlcə münsifləri əlavə edin.</p>
      )}
      <div className="pr-pages jp-pages">
        {sheets.map((group, i) => (
          <section key={group[0].submissionId} className="pr-sheet jp-sheet" aria-label={`Səhifə ${i + 1}`}>
            <header className="jp-head">
              <div className="jp-head__top">
                <span className="jp-head__brand">{platformName}</span>
                <span className="jp-head__doc">{data.competitionTitle} — münsif kataloqu</span>
                <span className="jp-head__page tabular">
                  Səhifə {i + 1} / {total}
                </span>
              </div>
              <p className="jp-head__rule">{instruction}</p>
              <div className="jp-head__legend">
                <Legend keys={keys} jurors={data.jurors} />
                <VoteBarKey className="jp-head__key" />
              </div>
            </header>
            <div className="jp-grid">
              {group.map((w, k) => (
                <CatalogCard key={w.submissionId} w={w} max={max} keys={keys} picture={picture(items[i * CATALOG_PER_PAGE + k])} />
              ))}
            </div>
            <footer className="pr-foot jp-foot">
              <span>Məxfi: yalnız münsiflər və səlahiyyətli təşkilatçılar üçün · {data.works.length} iş</span>
              <span className="tabular">
                Səhifə {i + 1} / {total}
              </span>
            </footer>
          </section>
        ))}
        {extra.map((p, k) => (
          <AppendixPage key={`${p.item.id}-${p.m.fileId}-${p.m.page}`} p={p} picture={page(p)} competition={data.competitionTitle} page={sheets.length + k + 1} pages={total} />
        ))}
      </div>
    </>
  )
}

/** "M1 — name": which field is whose, at the top of every page; the cards carry only the keys. */
function Legend({ keys, jurors }: { keys: string[]; jurors: JuryMatrixJuror[] }) {
  if (keys.length === 0) return <p className="jp-legend jp-legend--none">Münsif təyin edilməyib</p>
  return (
    <p className="jp-legend" aria-label="Münsiflər">
      {keys.map((k, n) => (
        <span key={k} className="jp-legend__item">
          <strong className="jp-legend__key">{k}</strong> {jurors[n].label}
        </span>
      ))}
    </p>
  )
}

function CatalogCard({ w, max, keys, picture }: { w: JuryPrintWork; max: number; keys: string[]; picture: Picture | undefined }) {
  const badge = pdfBadge(asItem(w))
  const number = catalogNumber(w.reference)
  const v = w.vote
  const rows = Math.ceil(keys.length / markColumns(keys.length))
  return (
    <article className="jp-card" aria-label={`${number}, ${placeName(v.position)}`}>
      <div className="jp-card__top">
        <span className="jp-card__place">
          <span className="jp-card__pos">{placeName(v.position)}</span>
          {v.tieSize > 1 && <span className="jp-card__tie">bərabər · {v.tieSize} iş</span>}
        </span>
        <span className="jp-card__no tabular">{number}</span>
      </div>
      <h2 className={`jp-card__title${w.title.length > 70 ? ' jp-card__title--long' : ''}`}>{w.title}</h2>
      <figure className="jp-card__fig">
        {!w.image ? (
          <span className="pr-figure__none">Şəkil yoxdur</span>
        ) : picture === undefined ? (
          <span className="pr-figure__none">Şəkil yüklənir…</span>
        ) : picture === 'failed' ? (
          <span className="pr-figure__none">Şəkil yüklənmədi</span>
        ) : (
          <img src={picture.url} alt={`${number} ${w.title}`} />
        )}
        {badge && <figcaption className="jp-card__badge">{badge}</figcaption>}
      </figure>
      <div className="jp-card__vote">
        <p className="jp-card__counts tabular">
          <span>
            <strong>{v.validVotes}</strong> etibarlı səs
          </span>
          {v.invalidatedVotes > 0 ? (
            <span className="jp-card__invalid">{v.invalidatedVotes} etibarsız sayılıb</span>
          ) : (
            <span className="jp-card__none">etibarsız səs yoxdur</span>
          )}
        </p>
        <VoteBar valid={v.validVotes} invalid={v.invalidatedVotes} max={max} />
      </div>
      {keys.length > 0 && (
        <div
          className={`jp-marks jp-marks--rows-${Math.min(rows, 3)}`}
          style={{ gridTemplateColumns: `repeat(${markColumns(keys.length)}, minmax(0, 1fr))` }}
          role="group"
          aria-label="Münsif balları"
        >
          <p className="jp-marks__caption">Bal: 1–10, tam ədəd</p>
          {keys.map((k) => (
            <span key={k} className="jp-mark">
              <span className="jp-mark__key">{k}</span>
              <span className="jp-mark__box" aria-hidden="true" />
            </span>
          ))}
        </div>
      )}
    </article>
  )
}

function AppendixPage({ p, picture, competition, page, pages }: { p: MaterialPage; picture: Picture | undefined; competition: string; page: number; pages: number }) {
  const { item, m, index, count } = p
  const what = m.kind === 'pdf' ? `PDF, səhifə ${m.page} / ${m.pages}` : 'şəkil'
  const number = catalogNumber(item.reference ?? '')
  return (
    <section className="pr-sheet pr-sheet--appendix" aria-label={`Səhifə ${page}: ${number}, material ${index} / ${count}`}>
      <header className="pr-head">
        <span className="pr-head__competition">{competition}</span>
        <span className="pr-head__ref tabular">{number}</span>
      </header>
      <p className="pr-appendix__title">
        <strong className="tabular">{number}</strong> {item.title}
      </p>
      <p className="pr-appendix__meta">
        Əlavə · material {index} / {count} · {what}
      </p>
      <figure className="pr-figure pr-figure--appendix">
        {picture === undefined ? (
          <span className="pr-figure__none">Şəkil yüklənir…</span>
        ) : picture === 'failed' ? (
          <span className="pr-figure__none">Şəkil yüklənmədi</span>
        ) : (
          <img className="pr-figure__img" src={picture.url} alt={`${number} — ${what}`} />
        )}
      </figure>
      <footer className="pr-foot">
        <span>Məxfi: yalnız münsiflər və səlahiyyətli təşkilatçılar üçün · {number}</span>
        <span className="tabular">
          Səhifə {page} / {pages}
        </span>
      </footer>
    </section>
  )
}

// ---- The internal ranking --------------------------------------------------------------------------------------

const comma = (n: number, places: number) => n.toFixed(places).replace('.', ',')

function Ranking({ competitionId }: { competitionId: number }) {
  const state = useApi((signal) => juryAdminApi.ranking(competitionId, signal), [competitionId])
  if (state.status === 'loading') return <p className="pr-bar__status jp-status">Reytinq yüklənir…</p>
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  const r: JuryRanking = state.data
  if (!r.complete)
    return (
      <p className="pr-empty">
        Reytinq bütün münsiflər bütün işləri qiymətləndirib yekunlaşdırdıqdan sonra hazır olur ({r.submittedJurors} /{' '}
        {r.jurorCount} münsif, {r.completedScores} / {r.expectedScores} bal).
      </p>
    )
  const pages = chunk(r.rows, RANKING_ROWS)
  return (
    <>
      <p className="pr-bar__status jp-status" role="status">
        {r.rows.length} iş · {pages.length} səhifə · {r.tieGroups} bərabərlik qrupu
      </p>
      <div className="pr-pages">
        {pages.map((rows, i) => (
          <section key={i} className="pr-sheet jp-scores jp-ranking" aria-label={`Reytinq, səhifə ${i + 1}`}>
            <header className="jp-scores__head">
              <div>
                <p className="jp-scores__doc">Daxili sənəd — ictimai deyil · {r.competitionTitle}</p>
                <p className="jp-scores__juror">
                  <strong>Münsiflərin daxili reytinqi</strong>
                </p>
              </div>
              <span className="jp-scores__page tabular">
                Səhifə {i + 1} / {pages.length}
              </span>
              <p className="jp-scores__rule">
                {r.rosterSize} iş, {r.jurorCount} münsifin 1–10 ballarının dəqiq ortası. Orta balı eyni olan işlər eyni yeri
                bölüşür (“bərabər”); aralarında seçim edilməyib. Hazırlanıb: {formatDateTime(r.generatedAt)}.
              </p>
            </header>
            <table className="jp-table">
              <thead>
                <tr>
                  <th scope="col" className="jp-table__n">
                    Yer
                  </th>
                  <th scope="col" className="jp-table__ref">
                    İş
                  </th>
                  <th scope="col">İşin adı</th>
                  <th scope="col" className="jp-table__avg">
                    Orta bal
                  </th>
                  <th scope="col" className="jp-table__count">
                    Bal sayı
                  </th>
                  <th scope="col" className="jp-table__tie">
                    Bərabərlik
                  </th>
                </tr>
              </thead>
              <tbody>
                {rows.map((x) => (
                  <tr key={x.submissionId} className={x.tied ? 'is-tied' : undefined}>
                    <td className="jp-table__n tabular">{x.position}</td>
                    <td className="jp-table__ref tabular">{catalogNumber(x.reference)}</td>
                    <td className="jp-table__title">{x.title}</td>
                    <td className="jp-table__avg tabular">
                      <strong>{comma(x.average, 2)}</strong> <span className="jp-sum">({x.scoreSum}/{x.scoreCount})</span>
                    </td>
                    <td className="jp-table__count tabular">
                      {x.scoreCount} / {r.jurorCount}
                    </td>
                    <td className="jp-table__tie">{x.tied ? `bərabər (${x.tieSize})` : ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <footer className="jp-scores__foot">
              <span className="jp-scores__cont">Daxili sənəd — ictimai deyil</span>
              <span className="jp-scores__id tabular">
                {i + 1}/{pages.length}
              </span>
            </footer>
          </section>
        ))}
      </div>
    </>
  )
}
