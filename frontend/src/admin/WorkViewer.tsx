import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Link } from 'react-router-dom'
import { AuthedImage } from '../components/submission/AuthedImage'
import { useApi } from '../lib/useApi'
import { adminApi, type AdminPrintItem, type AdminPrintMaterial } from './api'
import { pdfBadge, previewLabel } from './printPictures'

/** A line the opening screen adds about the work, e.g. its place in the public vote. */
export interface ViewerFact {
  label: string
  value: ReactNode
}

interface Props {
  /** The works in the opening screen's order — the vote's ranking, the jury's rows. */
  ids: number[]
  /** Which of them is open. */
  index: number
  onIndex: (index: number) => void
  onClose: () => void
  /**
   * What the opening screen knows about the work beyond the work itself — the vote ranking passes its place and valid
   * votes. The jury's rows pass nothing: the jury judges the work, not the vote.
   */
  context?: { title: string; facts: (id: number) => ViewerFact[] }
  /** "Ətraflı səhifə": the work's admin page (contact details live there, never here); absent: no link. */
  detailHref?: (id: number) => string
}

/**
 * One work, whole, over the screen it was opened from: every picture and every page of every PDF (the same material
 * the printed appendix has), page by page, fitted or at full size; the reference, title, author, nomination and the
 * full concept beside it. It opens on the page that stands for the work — the admin's choice, else the automatic one
 * — and steps to the previous and next work in the opening screen's order. Closing it leaves that screen as it was:
 * its filters, its scroll, the focus on what opened it.
 *
 * Never a contact or registration detail, never a file hidden from the public or of a private field, nothing of how
 * anyone voted. Everything shown is a metadata-free copy made on the server; the PDF itself is never served.
 */
export function WorkViewer({ ids, index, onIndex, onClose, context, detailHref }: Props) {
  const titleId = useId()
  const box = useRef<HTMLDivElement>(null)
  const closeButton = useRef<HTMLButtonElement>(null)
  const id = ids[index]
  const canPrev = index > 0
  const canNext = index < ids.length - 1

  // Focus in on open, back to what opened it on close; the screen behind keeps its scroll and does not move.
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null
    closeButton.current?.focus()
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.body.style.overflow = overflow
      opener?.focus?.()
    }
  }, [])

  // Escape closes; ← → step through the works. Heard on the whole document, so they work from anywhere in the box.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      else if (e.key === 'ArrowLeft' && canPrev && !e.altKey) {
        e.preventDefault()
        onIndex(index - 1)
      } else if (e.key === 'ArrowRight' && canNext && !e.altKey) {
        e.preventDefault()
        onIndex(index + 1)
      }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [index, canPrev, canNext, onIndex, onClose])

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key !== 'Tab' || !box.current) return
    const focusable = [...box.current.querySelectorAll<HTMLElement>('button:not([disabled]), a[href]')]
    if (focusable.length === 0) return
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault()
      last.focus()
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault()
      first.focus()
    }
  }

  return createPortal(
    <div className="a-qp a-wv" onKeyDown={onKeyDown}>
      <div className="a-qp__backdrop" onClick={onClose} aria-hidden="true" />
      <div ref={box} className="a-qp__box" role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <div className="a-qp__bar">
          <span className="a-qp__count tabular" aria-live="polite">
            {index + 1} / {ids.length}
          </span>
          <span className="a-qp__nav">
            <button type="button" className="btn btn--sm" onClick={() => onIndex(index - 1)} disabled={!canPrev}>
              <span aria-hidden="true">←</span> Əvvəlki iş
            </button>
            <button type="button" className="btn btn--sm" onClick={() => onIndex(index + 1)} disabled={!canNext}>
              Növbəti iş <span aria-hidden="true">→</span>
            </button>
          </span>
          <button ref={closeButton} type="button" className="btn btn--sm btn--ghost a-qp__close" onClick={onClose}>
            Bağla
          </button>
        </div>
        {/* Keyed by the work: the next one opens on its own representative page, fitted. */}
        <ViewerBody key={id} id={id} titleId={titleId} context={context} detailHref={detailHref} />
      </div>
    </div>,
    document.body,
  )
}

function ViewerBody({ id, titleId, context, detailHref }: { id: number; titleId: string } & Pick<Props, 'context' | 'detailHref'>) {
  const state = useApi((signal) => adminApi.workMaterial(id, signal), [id])
  const item = state.data

  if (state.status === 'error')
    return (
      <div className="a-qp__body">
        <p className="a-art a-art--none a-qp__large" role="alert">
          {state.error.message}
        </p>
      </div>
    )
  if (!item)
    return (
      <div className="a-qp__body">
        <span className="a-qp__large skeleton" aria-hidden="true" />
        <h2 id={titleId} className="visually-hidden">
          Yüklənir
        </h2>
      </div>
    )
  return <Loaded item={item} titleId={titleId} context={context} detailHref={detailHref} />
}

/** Where the work's material starts: the page that stands for it, else the first. */
const startOf = (item: AdminPrintItem) => {
  const at = item.material.findIndex((m) => m.fileId === item.image?.fileId && m.page === (item.image?.page ?? 1))
  return Math.max(0, at)
}

function Loaded({ item, titleId, context, detailHref }: { item: AdminPrintItem; titleId: string } & Pick<Props, 'context' | 'detailHref'>) {
  const [at, setAt] = useState(() => startOf(item))
  const [zoom, setZoom] = useState(false)
  const material = item.material
  const m = material[at] as AdminPrintMaterial | undefined
  const facts = context?.facts(item.id) ?? []
  const label = previewLabel(item.image)
  const badge = pdfBadge(item)
  // "3 / 8" across the whole material only when it has more than one file; a lone PDF counts its own pages.
  const severalFiles = new Set(material.map((x) => x.fileId)).size > 1

  const go = (to: number) => {
    setAt(Math.min(material.length - 1, Math.max(0, to)))
    setZoom(false)
  }

  return (
    <div className="a-qp__body">
      <div className="a-qp__stage a-wv__stage">
        {m ? (
          <>
            <LargePage key={`${m.fileId}:${m.page}`} submissionId={item.id} m={m} zoom={zoom} onToggleZoom={() => setZoom((z) => !z)} />
            <div className="a-wv__pagebar">
              <button type="button" className="btn btn--sm" onClick={() => go(at - 1)} disabled={at === 0} aria-label="Əvvəlki səhifə">
                <span aria-hidden="true">‹</span>
              </button>
              <span className="a-wv__where tabular" aria-live="polite">
                {severalFiles && `${at + 1} / ${material.length} · `}
                {m.kind === 'pdf' ? `PDF, səhifə ${m.page} / ${m.pages}` : 'Şəkil'}
              </span>
              <button
                type="button"
                className="btn btn--sm"
                onClick={() => go(at + 1)}
                disabled={at >= material.length - 1}
                aria-label="Növbəti səhifə"
              >
                <span aria-hidden="true">›</span>
              </button>
              <button type="button" className="btn btn--sm btn--ghost" aria-pressed={zoom} onClick={() => setZoom((z) => !z)}>
                {zoom ? 'Ekrana sığdır' : 'Tam ölçü'}
              </button>
            </div>
            {material.length > 1 && (
              <ul role="list" className="a-qp__strip" aria-label="Bütün material">
                {material.map((x, i) => (
                  <li key={`${x.fileId}:${x.page}`}>
                    <button
                      type="button"
                      className="a-qp__thumb"
                      aria-pressed={i === at}
                      aria-label={x.kind === 'pdf' ? `PDF, səhifə ${x.page} / ${x.pages}` : `Şəkil ${i + 1}`}
                      onClick={() => go(i)}
                    >
                      <AuthedImage submissionId={item.id} fileId={x.fileId} page={x.page} alt="" variant="thumb" className="a-art" lazy />
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </>
        ) : (
          <span className="a-art a-art--none a-qp__large">Bu işin göstəriləcək materialı yoxdur</span>
        )}
      </div>

      <div className="a-qp__meta">
        <p className="a-qp__ref">
          <span className="tabular">{item.reference ?? '—'}</span>
          {badge && <span className="a-wv__badge tabular">{badge}</span>}
        </p>
        <h2 id={titleId} className="a-qp__title">
          {item.title || 'Adsız'}
        </h2>
        {label && <p className="a-qp__file tabular">{label}</p>}

        {facts.length > 0 && context && (
          <section className="a-wv__context" aria-label={context.title}>
            <h3 className="a-wv__context-title">{context.title}</h3>
            <dl className="a-kv">
              {facts.map((f) => (
                <div key={f.label}>
                  <dt>{f.label}</dt>
                  <dd className="tabular">{f.value}</dd>
                </div>
              ))}
            </dl>
          </section>
        )}

        <dl className="a-kv">
          {item.author && (
            <div>
              <dt>Müəllif</dt>
              <dd>{item.author}</dd>
            </div>
          )}
          {item.category && (
            <div>
              <dt>Kateqoriya</dt>
              <dd>{item.category}</dd>
            </div>
          )}
          {item.facts.map((f) => (
            <div key={f.label}>
              <dt>{f.label}</dt>
              <dd>{f.value}</dd>
            </div>
          ))}
        </dl>

        {item.concept && (
          <div className="a-wv__concept">
            <h3 className="a-wv__context-title">{item.conceptLabel ?? 'Konsepsiya'}</h3>
            <p>{item.concept}</p>
          </div>
        )}

        {detailHref && (
          <p className="a-qp__more">
            <Link to={detailHref(item.id)} className="btn btn--sm">
              Ətraflı səhifə
            </Link>
          </p>
        )}
      </div>
    </div>
  )
}

/**
 * One page at a time: fitted to the stage, or at its full size to read small print (scrolled within the stage). A
 * picture or a PDF's first page is the file's safe copy; a later page is rendered on request.
 */
function LargePage({
  submissionId,
  m,
  zoom,
  onToggleZoom,
}: {
  submissionId: number
  m: AdminPrintMaterial
  zoom: boolean
  onToggleZoom: () => void
}) {
  const [url, setUrl] = useState<string>()
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    let objectUrl: string | undefined
    const load =
      m.kind === 'pdf' && m.page > 1
        ? adminApi.documentPageBlob(submissionId, m.fileId, m.page, controller.signal)
        : adminApi.publicAssetBlob(submissionId, m.fileId, controller.signal)
    load
      .then((blob) => {
        objectUrl = URL.createObjectURL(blob)
        setUrl(objectUrl)
      })
      .catch(() => {
        if (!controller.signal.aborted) setFailed(true)
      })
    return () => {
      controller.abort()
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [submissionId, m.fileId, m.kind, m.page])

  if (failed) return <span className="a-art a-art--none a-qp__large">Bu səhifə açılmadı</span>
  if (!url) return <span className="a-qp__large skeleton" aria-hidden="true" />
  const alt = m.kind === 'pdf' ? `PDF, səhifə ${m.page} / ${m.pages}` : 'Şəkil'
  return zoom ? (
    <div className="a-wv__zoom">
      <img src={url} alt={alt} decoding="async" onDoubleClick={onToggleZoom} />
    </div>
  ) : (
    <img className="a-art a-qp__large" src={url} alt={alt} decoding="async" onDoubleClick={onToggleZoom} />
  )
}
