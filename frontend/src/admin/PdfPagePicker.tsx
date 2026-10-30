import { useState } from 'react'
import { AuthedImage } from '../components/submission/AuthedImage'
import type { AdminFile } from './api'
import { ConfirmDialog } from './ConfirmDialog'

/**
 * "Önizləmə səhifəsini seç": every page of a PDF side by side, so an admin picks the one that shows the work best — the
 * page every admin screen and print then shows for it (the jury's package, the prints, the Final tab and show, the
 * vote ranking, the lists). Only that choice is stored; the PDF is never changed, and the full material keeps every
 * page. "Avtomatik seçimə qaytar" gives the choice back to the automatic rule.
 */
export function PdfPagePicker({
  submissionId,
  file,
  busy,
  error,
  onSave,
  onCancel,
}: {
  submissionId: number
  /** A PDF whose safe first page is ready (pdfPages > 0). */
  file: AdminFile
  busy: boolean
  error?: string
  /** A page, or null for the automatic choice. */
  onSave: (page: number | null) => void
  onCancel: () => void
}) {
  const pages = file.pdfPages ?? 0
  // Both pages come from the server's one rule (SubmissionImages.RepresentativePage); nothing is decided here.
  const auto = file.automaticPage ?? 1
  const current = file.representativePage ?? auto
  const [page, setPage] = useState(current)

  return (
    <ConfirmDialog
      wide
      title="Önizləmə səhifəsini seç"
      confirmLabel="Bu səhifəni seç"
      confirmDisabled={page === current}
      busy={busy}
      error={error}
      onCancel={onCancel}
      onConfirm={() => onSave(page)}
    >
      <p>
        <strong className="tabular">PDF · {pages} səhifə</strong> <span className="a-muted a-qp__wrap">— {file.originalFileName}</span>
      </p>
      <p className="a-muted">
        Seçdiyiniz səhifə bu işi bütün admin ekranlarında və çaplarda təmsil edir. PDF dəyişmir: “Tam materiala bax” və
        çap əlavəsi bütün səhifələri göstərməyə davam edir.
      </p>
      <div className="a-pages" role="radiogroup" aria-label="PDF-in səhifələri">
        {Array.from({ length: pages }, (_, i) => i + 1).map((p) => (
          <label key={p} className={`a-page${p === page ? ' is-chosen' : ''}`}>
            <input
              type="radio"
              name={`pdf-page-${file.id}`}
              className="visually-hidden"
              value={p}
              checked={p === page}
              onChange={() => setPage(p)}
            />
            <span className="a-page__art">
              {/* Card-size copies: page 1 is the file's own, later pages are rendered on request. */}
              <AuthedImage submissionId={submissionId} fileId={file.id} page={p} alt="" variant="thumb" className="a-art" lazy />
            </span>
            <span className="a-page__label tabular">
              Səhifə {p}
              {p === auto && (
                <>
                  {' · '}
                  <span className="a-page__auto">avtomatik</span>
                </>
              )}
            </span>
          </label>
        ))}
      </div>
      <p className="a-pages__chosen" aria-live="polite">
        Əsas önizləmə: səhifə <span className="tabular">{page}</span>
        {file.previewPage == null && page === auto && <span className="a-muted"> (avtomatik seçim)</span>}
      </p>
      {file.previewPage != null && (
        <p>
          <button type="button" className="btn btn--sm btn--ghost" disabled={busy} onClick={() => onSave(null)}>
            Avtomatik seçimə qaytar
          </button>{' '}
          <span className="a-muted">— səhifə {auto}</span>
        </p>
      )}
    </ConfirmDialog>
  )
}
