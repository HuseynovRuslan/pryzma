import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { RequireAdmin } from '../AdminLayout'
import { adminApi, type AdminPrint, type AdminPrintItem, type SubmissionFilters } from '../api'
import { bakuDayEnd, bakuDayStart } from '../lib'
import { BLIND_INSTRUCTION, chunk, CRITERIA, excerpt, PRINT_FILTER_KEYS, printHref, type PrintLayout } from '../print'
import { PdfBadge } from '../PdfBadge'
import { materialPages, useMaterial, usePictures, type MaterialPage, type Picture } from '../printPictures'
import './print.css'

/**
 * The list's selection on paper: four works per A4 page — the jury's sheets — or one, the detailed PDF. Management
 * reads it, marks it and passes it round: reference, title, nomination, picture, public description, the author's
 * name (an internal document — shown unless the admin unticks it, owner, 7 October 2026), and the jury's three marks
 * (İdeya, Vizual / icra, Mövzuya uyğunluq, each of 10, and their sum of 30) with a note. Never a vote count or a
 * public rank; contact and registration data are not in the answer at all (AdminPrintDto).
 *
 * With `finalists=1` it prints the competition's frozen finalists only (D13): the jury's package. A work with a PDF
 * says "PDF · N səhifə" and, for two or more pages, shows its second page — the first is often only a cover (owner,
 * 7 October 2026). The whole material — every picture and every page of every PDF — follows the cards only when the
 * admin asks for it (`material=1`): the default print stays compact, and the full material is one click away on
 * screen ("Tam materiala bax": the work's admin page, where the PDF opens for admins only).
 *
 * Each sheet on screen is exactly one printed page, so what is seen is what prints: the browser's print dialog (and
 * its "Save as PDF") gets A4 pages with no margins of its own and nothing of the admin around them.
 */

/** Description sizes tried on a full page, largest first (pt). A long one also goes into two columns. */
const TEXT_SIZES = [10.5, 10, 9.5, 9, 8.5, 8, 7.5, 7]
const TWO_COLUMNS_FROM = 900

/**
 * Outside the admin layout on purpose — no sidebar, no top bar, on screen or on paper — but behind the same gate:
 * the page shows nothing to anybody who is not an admin, and the server checks the role on every request anyway.
 */
export default function SubmissionsPrintPage() {
  return (
    <RequireAdmin>
      <PrintView />
    </RequireAdmin>
  )
}

function PrintView() {
  const [params, setParams] = useSearchParams()
  // The choices answer at once; the address follows, so a reload or a shared link opens the same view.
  // Four to a page unless the address asks for one: the jury's sheets are the common print.
  const [layout, setLayout] = useState<PrintLayout>(params.get('layout') === '1' ? 1 : 4)
  const [withAuthor, setWithAuthor] = useState(params.get('author') !== '0')
  const finalists = params.get('finalists') === '1'
  const [withMaterial, setWithMaterial] = useState(params.get('material') === '1')

  const filters: SubmissionFilters = Object.fromEntries(PRINT_FILTER_KEYS.map((k) => [k, params.get(k) ?? undefined]))
  const filterKey = PRINT_FILTER_KEYS.map((k) => `${k}=${params.get(k) ?? ''}`).join('&')
  const state = useApi(
    (signal) =>
      adminApi.printSubmissions(
        {
          ...filters,
          from: filters.from ? bakuDayStart(filters.from) : undefined,
          to: filters.to ? bakuDayEnd(filters.to) : undefined,
        },
        withAuthor,
        signal,
        finalists,
      ),
    [filterKey, withAuthor, finalists],
  )

  // The last answer stays on the page while the next one (with or without names) is on its way.
  const [shown, setShown] = useState<AdminPrint | null>(null)
  useEffect(() => {
    if (state.status === 'success') setShown(state.data)
  }, [state.status, state.data])

  const title = `${shown?.competitionTitle ?? 'Təkliflər'}${finalists ? ' — münsif paketi (Top 10)' : ''}`
  // The document's title is the PDF's default file name.
  useTitle(`${title} — çap (${layout === 1 ? '1 iş' : '4 iş'} / səhifə)`)

  const items = shown?.items
  const picture = usePictures(items)
  const appendix = useMemo(() => (withMaterial && items ? materialPages(items) : undefined), [withMaterial, items])
  const page = useMaterial(appendix)

  const set = (patch: Record<string, string | null>) => {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(patch)) {
      if (v) next.set(k, v)
      else next.delete(k)
    }
    setParams(next, { replace: true })
  }

  if (!shown) {
    if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
    return <PageLoading label="Çap üçün hazırlanır" />
  }

  const withImage = shown.items.filter((i) => i.image)
  const extra = appendix ?? []
  const settled =
    withImage.filter((i) => picture(i) !== undefined).length + extra.filter((p) => page(p) !== undefined).length
  const failed = withImage.filter((i) => picture(i) === 'failed').length + extra.filter((p) => page(p) === 'failed').length
  const expected = withImage.length + extra.length
  const ready = settled === expected
  const sheets = layout === 1 ? shown.items.map((i) => [i]) : chunk(shown.items, 4)
  // The appendix follows the cards, numbered on from them.
  const total = sheets.length + extra.length
  // Back to where the print was opened: the competition's final, or the list as it was left (the same filters).
  const backQuery = printHref(params).split('?')[1]
  const back =
    finalists && filters.competitionId
      ? `/admin/competitions/${filters.competitionId}?tab=final`
      : `/admin/submissions${backQuery ? `?${backQuery}` : ''}`

  return (
    <div className="pr">
      <div className="pr-bar">
        <div className="pr-bar__row">
          <div className="pr-bar__text">
            <p className="pr-bar__eyebrow">{finalists ? 'Final · münsiflər üçün' : 'Çap / PDF'}</p>
            <h1 className="pr-bar__title">{title}</h1>
            <p className="pr-bar__meta">
              <span className="tabular">{shown.items.length}</span> iş · <span className="tabular">{total}</span> səhifə
              (A4){extra.length > 0 && <> — o cümlədən material əlavəsi: {extra.length}</>}
            </p>
          </div>
          <div className="pr-bar__actions">
            <button type="button" className="btn btn--primary" onClick={() => window.print()} disabled={!ready}>
              Çap et
            </button>
            <Link to={back} className="btn">
              Geri
            </Link>
          </div>
        </div>

        <div className="pr-bar__row pr-bar__row--options">
          <fieldset className="pr-layout">
            <legend className="pr-layout__legend">Səhifə düzümü</legend>
            <label className="pr-radio">
              <input
                type="radio"
                name="layout"
                checked={layout === 4}
                onChange={() => {
                  setLayout(4)
                  set({ layout: null })
                }}
              />
              4 iş / səhifə (münsif vərəqi)
            </label>
            <label className="pr-radio">
              <input
                type="radio"
                name="layout"
                checked={layout === 1}
                onChange={() => {
                  setLayout(1)
                  set({ layout: '1' })
                }}
              />
              1 iş / səhifə (ətraflı PDF)
            </label>
          </fieldset>
          <label className="pr-check">
            <input
              type="checkbox"
              checked={withAuthor}
              onChange={(e) => {
                setWithAuthor(e.target.checked)
                set({ author: e.target.checked ? null : '0' })
              }}
            />
            Müəllif adını göstər
          </label>
          <label className="pr-check">
            <input
              type="checkbox"
              checked={withMaterial}
              onChange={(e) => {
                setWithMaterial(e.target.checked)
                set({ material: e.target.checked ? '1' : null })
              }}
            />
            PDF-lərin bütün səhifələrini əlavə et
          </label>
        </div>

        <p className="pr-bar__status" role="status">
          {ready
            ? failed > 0
              ? `Hazırdır. ${failed} şəkil yüklənmədi — o səhifələrdə yeri boş qalacaq.`
              : 'Hazırdır. Bütün şəkillər yükləndi.'
            : `Şəkillər yüklənir: ${settled} / ${expected}`}
        </p>
        {shown.total > shown.items.length && (
          <p className="pr-bar__warn" role="alert">
            Seçimdə {shown.total} iş var; ilk {shown.items.length} iş göstərilir. Hamısını çap etmək üçün filtrlərlə
            seçimi daraldın.
          </p>
        )}
        {finalists ? (
          <p className="pr-bar__hint">
            Yalnız təsdiqlənmiş finalçılar, nömrə sırası ilə. Səs sayı və xalq sıralaması vərəqdə yoxdur.
          </p>
        ) : (
          !filters.status && (
            <p className="pr-bar__hint">Rədd edilmiş və geri çəkilmiş işlər daxil deyil (statusu siyahıda seçməklə çap etmək olar).</p>
          )
        )}
        <p className="pr-bar__hint">PDF üçün çap pəncərəsində “PDF kimi saxla”nı seçin; kağız A4, miqyas 100%.</p>
      </div>

      {shown.items.length === 0 ? (
        <p className="pr-empty">Bu seçimdə çap ediləcək iş yoxdur.</p>
      ) : (
        <div className="pr-pages" data-layout={layout}>
          {sheets.map((group, i) =>
            layout === 1 ? (
              <FullPage
                key={group[0].id}
                item={group[0]}
                picture={picture(group[0])}
                competition={shown.competitionTitle}
                page={i + 1}
                pages={total}
                juror={finalists}
              />
            ) : (
              <section key={group[0].id} className="pr-sheet pr-sheet--four" aria-label={`Səhifə ${i + 1}`}>
                <SheetHead competition={shown.competitionTitle} juror={finalists} />
                <div className="pr-grid">
                  {group.map((item) => (
                    <Card key={item.id} item={item} picture={picture(item)} />
                  ))}
                </div>
                <SheetFoot page={i + 1} pages={total} />
              </section>
            ),
          )}
          {extra.map((p, k) => (
            <AppendixPage
              key={`${p.item.id}-${p.m.fileId}-${p.m.page}`}
              p={p}
              picture={page(p)}
              competition={shown.competitionTitle}
              page={sheets.length + k + 1}
              pages={total}
            />
          ))}
        </div>
      )}
    </div>
  )
}

// ---- one work per page ----------------------------------------------------------------------------------------

function FullPage({
  item,
  picture,
  competition,
  page,
  pages,
  juror,
}: {
  item: AdminPrintItem
  picture: Picture | undefined
  competition: string | null
  page: number
  pages: number
  juror: boolean
}) {
  const sheet = useRef<HTMLElement>(null)
  const text = useRef<HTMLDivElement>(null)
  const [tooLong, setTooLong] = useState(false)

  // The description is set in the largest size at which the whole page fits on one A4 sheet. The picture takes
  // whatever height is left (down to a minimum), so only the text can push the page over; when not even the
  // smallest size fits, the page is allowed to run on rather than cut anything off, and the screen says so.
  useLayoutEffect(() => {
    const el = sheet.current
    const body = text.current
    if (!el || !body) return
    const fits = () => el.scrollHeight <= el.clientHeight + 1
    const measure = () => {
      const columns = (item.concept?.length ?? 0) >= TWO_COLUMNS_FROM ? 2 : 1
      body.style.columnCount = String(columns)
      for (const size of TEXT_SIZES) {
        body.style.fontSize = `${size}pt`
        if (fits()) {
          setTooLong(false)
          return
        }
      }
      setTooLong(true)
    }
    measure()
    let alive = true
    void document.fonts?.ready.then(() => {
      if (alive) measure()
    })
    return () => {
      alive = false
    }
  }, [item.concept, item.title, item.author, item.facts])

  return (
    <section
      ref={sheet}
      className={`pr-sheet pr-sheet--one${tooLong ? ' pr-sheet--long' : ''}`}
      aria-label={`Səhifə ${page}: ${item.reference ?? item.title}`}
    >
      <SheetHead competition={competition} reference={item.reference} juror={juror} />
      <h2 className="pr-title">{item.title}</h2>
      <Meta item={item} />
      <PdfBadge item={item} />
      <FullMaterialLink item={item} />
      <Figure item={item} picture={picture} />
      {item.concept && (
        <div className="pr-concept">
          {item.conceptLabel && <p className="pr-concept__label">{item.conceptLabel}</p>}
          <div ref={text} className="pr-concept__text">
            {item.concept}
          </div>
        </div>
      )}
      <div className="pr-eval">
        <Marks />
        <p className="pr-eval__note">Qeyd:</p>
        <span className="pr-eval__line" aria-hidden="true" />
        <span className="pr-eval__line" aria-hidden="true" />
      </div>
      <SheetFoot page={page} pages={pages} reference={item.reference} />
      {tooLong && (
        <p className="pr-sheet__warn" role="note">
          Bu işin mətni bir səhifəyə sığmadı — çapda növbəti səhifəyə keçəcək.
        </p>
      )}
    </section>
  )
}

// ---- four works per page --------------------------------------------------------------------------------------

function Card({ item, picture }: { item: AdminPrintItem; picture: Picture | undefined }) {
  return (
    <article className="pr-card" aria-label={item.reference ?? item.title}>
      <p className="pr-card__ref tabular">
        {item.reference ?? '—'}
        <PdfBadge item={item} />
        <FullMaterialLink item={item} />
      </p>
      <h2 className="pr-card__title">{item.title}</h2>
      <Meta item={item} />
      <Figure item={item} picture={picture} />
      {item.concept && <p className="pr-card__concept">{excerpt(item.concept)}</p>}
      <div className="pr-card__eval">
        <Marks />
        <p className="pr-card__note">
          Qeyd: <span className="pr-eval__rest" aria-hidden="true" />
        </p>
      </div>
    </article>
  )
}

// ---- shared parts ---------------------------------------------------------------------------------------------

/**
 * "Tam materiala bax", on screen only: the work's admin page, where every file opens for admins — the PDF itself never
 * leaves the admin's own session. A printed sheet has no use for a link.
 */
function FullMaterialLink({ item }: { item: AdminPrintItem }) {
  if (item.pdfPages === 0) return null
  return (
    <a className="pr-screen-only pr-material-link" href={`/admin/submissions/${item.id}`} target="_blank" rel="noreferrer">
      Tam materiala bax
    </a>
  )
}

// ---- the appendix: the whole material, one page each ---------------------------------------------------------

function AppendixPage({
  p,
  picture,
  competition,
  page,
  pages,
}: {
  p: MaterialPage
  picture: Picture | undefined
  competition: string | null
  page: number
  pages: number
}) {
  const { item, m, index, count } = p
  const what = m.kind === 'pdf' ? `PDF, səhifə ${m.page} / ${m.pages}` : 'şəkil'
  return (
    <section className="pr-sheet pr-sheet--appendix" aria-label={`Səhifə ${page}: ${item.reference ?? item.title}, material ${index} / ${count}`}>
      <SheetHead competition={competition} reference={item.reference} />
      <p className="pr-appendix__title">
        <strong className="tabular">{item.reference ?? '—'}</strong> {item.title}
      </p>
      <p className="pr-appendix__meta">
        Əlavə · material {index} / {count} · {what}
        {item.author && <> · Müəllif: {item.author}</>}
      </p>
      <figure className="pr-figure pr-figure--appendix">
        {picture === undefined ? (
          <span className="pr-figure__none">Şəkil yüklənir…</span>
        ) : picture === 'failed' ? (
          <span className="pr-figure__none">Şəkil yüklənmədi</span>
        ) : (
          <img className="pr-figure__img" src={picture.url} alt={`${item.reference ?? ''} — ${what}`} />
        )}
      </figure>
      <SheetFoot page={page} pages={pages} reference={item.reference} />
    </section>
  )
}

/** The marks, as lines to write on: the three criteria and their sum. No vote, no rank — the jury judges blind to them. */
function Marks() {
  return (
    <dl className="pr-marks">
      {CRITERIA.map((c) => (
        <div key={c} className="pr-marks__row">
          <dt>{c}:</dt>
          <dd>
            <span className="pr-eval__blank" aria-hidden="true" /> / 10
          </dd>
        </div>
      ))}
      <div className="pr-marks__row pr-marks__row--sum">
        <dt>Cəm:</dt>
        <dd>
          <span className="pr-eval__blank" aria-hidden="true" /> / 30
        </dd>
      </div>
    </dl>
  )
}

function SheetHead({
  competition,
  reference,
  juror = false,
}: {
  competition: string | null
  reference?: string | null
  /** A line for the juror's name or number: each juror gets their own set of the final's sheets. */
  juror?: boolean
}) {
  return (
    <header className={`pr-head${juror ? ' pr-head--jury' : ''}`}>
      <span className="pr-head__competition">{competition ?? 'Təkliflər'}</span>
      {juror && (
        <span className="pr-head__juror">
          Münsif: <span className="pr-eval__blank" aria-hidden="true" />
        </span>
      )}
      {reference !== undefined && <span className="pr-head__ref tabular">{reference ?? '—'}</span>}
      {/* On every sheet of the jury's set: a page handed round on its own still carries the instruction. */}
      {juror && <p className="pr-head__blind">{BLIND_INSTRUCTION}</p>}
    </header>
  )
}

function SheetFoot({ page, pages, reference }: { page: number; pages: number; reference?: string | null }) {
  return (
    <footer className="pr-foot">
      <span>{reference ?? ''}</span>
      <span className="tabular">
        Səhifə {page} / {pages}
      </span>
    </footer>
  )
}

/** Category, nomination-like public choices and — only when asked for — the author, on one line. */
function Meta({ item }: { item: AdminPrintItem }) {
  const parts = [
    ...(item.category ? [item.category] : []),
    ...item.facts.map((f) => `${f.label}: ${f.value}`),
    ...(item.author ? [`Müəllif: ${item.author}`] : []),
  ]
  if (parts.length === 0) return null
  return <p className="pr-meta">{parts.join(' · ')}</p>
}

function Figure({ item, picture }: { item: AdminPrintItem; picture: Picture | undefined }) {
  return (
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
  )
}
