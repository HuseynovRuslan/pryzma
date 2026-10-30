import { useEffect, useId, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Link } from 'react-router-dom'
import { submissionsApi } from '../../api/endpoints'
import { AuthedImage } from '../../components/submission/AuthedImage'
import { formatDateTime } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { adminApi, type AdminFile, type AdminSubmissionDetail, type AdminSubmissionRow } from '../api'
import { SubmissionStatusBadge } from '../ui'

interface Props {
  /** The work being looked at. */
  id: number
  /** Its row in the list, when the list is showing it: enough to draw the header before the details arrive. */
  row: AdminSubmissionRow | undefined
  /** "12 / 48" — its place in the filtered results, not just on this page. */
  position: { at: number; total: number } | null
  canPrev: boolean
  canNext: boolean
  /** The list is fetching the neighbouring page: stepping waits for it. */
  busy: boolean
  onPrev: () => void
  onNext: () => void
  onClose: () => void
}

/**
 * Looking through works one after another without leaving the list. Everything shown is a safe copy made on the
 * server — the metadata-free 1600 px image (a PDF: the page that stands for it), or while that is not ready, the
 * card-size copy.
 * The private original is never requested here; it opens on the work's own page, as it always has.
 */
export function SubmissionQuickPreview({ id, row, position, canPrev, canNext, busy, onPrev, onNext, onClose }: Props) {
  const titleId = useId()
  const box = useRef<HTMLDivElement>(null)
  const closeButton = useRef<HTMLButtonElement>(null)

  // Focus in on open, back to what opened it on close; the list behind does not scroll.
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

  // Arrows and Escape are heard on the whole document, not just inside the box: reaching the last work disables
  // "Növbəti", and a disabled button drops the focus to <body> — the keys must keep working from there.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose()
      } else if (e.key === 'ArrowLeft' && canPrev && !busy) {
        e.preventDefault()
        onPrev()
      } else if (e.key === 'ArrowRight' && canNext && !busy) {
        e.preventDefault()
        onNext()
      }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [canPrev, canNext, busy, onPrev, onNext, onClose])

  // …and the focus itself is put back somewhere useful, so Tab carries on inside the preview.
  useEffect(() => {
    const active = document.activeElement
    if (active instanceof HTMLButtonElement && !active.disabled && box.current?.contains(active)) return
    if (active instanceof HTMLAnchorElement && box.current?.contains(active)) return
    closeButton.current?.focus()
  }, [canPrev, canNext, busy, id])

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
    <div className="a-qp" onKeyDown={onKeyDown}>
      <div className="a-qp__backdrop" onClick={onClose} aria-hidden="true" />
      <div ref={box} className="a-qp__box" role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <div className="a-qp__bar">
          <span className="a-qp__count tabular" aria-live="polite">
            {position ? `${position.at} / ${position.total}` : ''}
          </span>
          <span className="a-qp__nav">
            <button type="button" className="btn btn--sm" onClick={onPrev} disabled={!canPrev || busy}>
              <span aria-hidden="true">←</span> Əvvəlki
            </button>
            <button type="button" className="btn btn--sm" onClick={onNext} disabled={!canNext || busy}>
              Növbəti <span aria-hidden="true">→</span>
            </button>
          </span>
          <button ref={closeButton} type="button" className="btn btn--sm btn--ghost a-qp__close" onClick={onClose}>
            Bağla
          </button>
        </div>
        {/* Keyed by the work: stepping to the next one starts its file choice afresh. */}
        <PreviewBody key={id} id={id} row={row} titleId={titleId} />
      </div>
    </div>,
    document.body,
  )
}

function PreviewBody({ id, row, titleId }: { id: number; row: AdminSubmissionRow | undefined; titleId: string }) {
  const detail = useApi((signal) => adminApi.submission(id, signal), [id])
  const s = detail.data
  // Starts on the file the list shows for the work, so the picture does not change under the reader's eyes.
  const [chosen, setChosen] = useState<number | null>(row?.preview?.fileId ?? null)
  const files = s?.files ?? []
  const file = files.find((f) => f.id === chosen) ?? files[0] ?? null

  const reference = s?.referenceNumber ?? row?.referenceNumber
  const title = (s?.title ?? row?.title) || 'Adsız'
  const status = s?.status ?? row?.status
  const answers = (s?.answers ?? []).filter((a) => a.value && a.value.trim())

  return (
    <div className="a-qp__body">
      <div className="a-qp__stage">
        {detail.status === 'error' ? (
          <span className="a-art a-art--none a-qp__large">{detail.error.message}</span>
        ) : !s ? (
          <span className="a-qp__large skeleton" aria-hidden="true" />
        ) : file ? (
          <LargePreview submissionId={id} file={file} key={file.id} />
        ) : (
          <span className="a-art a-art--none a-qp__large">Bu təklifdə fayl yoxdur</span>
        )}

        {files.length > 1 && (
          <ul role="list" className="a-qp__strip" aria-label="Fayllar">
            {files.map((f, i) => (
              <li key={f.id}>
                <button
                  type="button"
                  className="a-qp__thumb"
                  aria-pressed={f.id === file?.id}
                  aria-label={`${i + 1}-ci fayl: ${fileKind(f)}, ${f.originalFileName}`}
                  onClick={() => setChosen(f.id)}
                >
                  {f.thumbnailReady ? (
                    <AuthedImage submissionId={id} fileId={f.id} alt="" variant="thumb" className="a-art" />
                  ) : (
                    <span className="a-art a-art--none">{fileKind(f)}</span>
                  )}
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="a-qp__meta">
        <p className="a-qp__ref">
          <span className="tabular">{reference}</span>
          {status && <SubmissionStatusBadge status={status} />}
        </p>
        <h2 id={titleId} className="a-qp__title">
          {title}
        </h2>

        <dl className="a-kv a-qp__facts">
          <div>
            <dt>İştirakçı</dt>
            <dd>{s ? `${s.participant.firstName} ${s.participant.lastName}` : (row?.participant.name ?? '…')}</dd>
          </div>
          {(s?.participant.email ?? row?.participant.email) && (
            <div>
              <dt>E-mail</dt>
              <dd className="a-qp__wrap">{s?.participant.email ?? row?.participant.email}</dd>
            </div>
          )}
          {s?.participant.phone && (
            <div>
              <dt>Telefon</dt>
              <dd className="tabular">{s.participant.phone}</dd>
            </div>
          )}
          <div>
            <dt>Müsabiqə</dt>
            <dd>{s?.competition.name ?? row?.competition.name}</dd>
          </div>
          {(s?.category ?? row?.category) && (
            <div>
              <dt>{s?.categoryLabel ?? 'Kateqoriya'}</dt>
              <dd>{(s?.category ?? row?.category)?.name}</dd>
            </div>
          )}
          <div>
            <dt>Göndərilib</dt>
            <dd className="tabular">{formatDateTime(s?.submittedAt ?? row?.submittedAt)}</dd>
          </div>
        </dl>

        {/* The participant's own answers — nomination, concept, the meaning of the symbols — as they sent them. */}
        {answers.length > 0 && (
          <dl className="a-answers a-qp__answers">
            {answers.map((a) => (
              <div key={a.fieldId} className="a-answer">
                <dt>{a.label}</dt>
                <dd>{a.value}</dd>
              </div>
            ))}
          </dl>
        )}

        {s && <FileNote s={s} file={file} />}

        <p className="a-qp__more">
          <Link to={`/admin/submissions/${id}`} className="btn btn--sm">
            Ətraflı səhifə
          </Link>
        </p>
      </div>
    </div>
  )
}

/** What kind of file it is, in a word. */
function fileKind(f: AdminFile): string {
  if (f.contentType === 'application/pdf') return 'PDF'
  if (f.contentType.startsWith('image/')) return 'Şəkil'
  return f.originalFileName.split('.').pop()?.toUpperCase() ?? 'Fayl'
}

/** The line under the picture: which file this is, and for a PDF which of its pages is shown here. */
function FileNote({ s, file }: { s: AdminSubmissionDetail; file: AdminFile | null }) {
  if (!file) return null
  const n = s.files.findIndex((f) => f.id === file.id) + 1
  return (
    <p className="a-qp__file">
      {s.files.length > 1 && <span className="tabular">{n} / {s.files.length} · </span>}
      <span className="a-qp__wrap">{file.originalFileName}</span>
      {file.contentType === 'application/pdf' && (
        <span>
          {' '}
          — {(file.pdfPages ?? 0) > 0 ? `önizləmə: səhifə ${file.representativePage ?? 1} / ${file.pdfPages}` : 'birinci səhifə göstərilir'};
          bütöv sənəd ətraflı səhifədə açılır
        </span>
      )}
    </p>
  )
}

/**
 * The large picture: the 1600 px safe copy when it exists, the card-size copy while it is being made, and a plain
 * note when neither is there. Never the original — a file whose copy failed is opened from the work's own page.
 */
function LargePreview({ submissionId, file }: { submissionId: number; file: AdminFile }) {
  // A PDF is shown by the page that stands for it (the admin's choice, else the automatic one).
  const page = (file.pdfPages ?? 0) > 0 ? (file.representativePage ?? 1) : 1
  if (file.publicAsset === 'Ready') return <SafeImage submissionId={submissionId} fileId={file.id} large page={page} />
  if (file.thumbnailReady) return <SafeImage submissionId={submissionId} fileId={file.id} large={false} />
  return (
    <span className="a-art a-art--none a-qp__large">
      {file.publicAsset === 'Failed'
        ? 'Bu fayldan təhlükəsiz önizləmə alınmadı. Faylı ətraflı səhifədən açmaq olar.'
        : 'Önizləmə hazırlanır'}
    </span>
  )
}

function SafeImage({ submissionId, fileId, large, page = 1 }: { submissionId: number; fileId: number; large: boolean; page?: number }) {
  const [url, setUrl] = useState<string>()
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    let objectUrl: string | undefined
    const load =
      page > 1
        ? (id: number, file: number, signal: AbortSignal) => adminApi.documentPageBlob(id, file, page, signal, !large)
        : large
          ? adminApi.publicAssetBlob
          : submissionsApi.thumbBlob
    load(submissionId, fileId, controller.signal)
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
  }, [submissionId, fileId, large, page])

  if (failed) return <span className="a-art a-art--none a-qp__large">Önizləmə açılmadı</span>
  if (!url) return <span className="a-qp__large skeleton" aria-hidden="true" />
  return <img className="a-art a-qp__large" src={url} alt="" decoding="async" />
}
