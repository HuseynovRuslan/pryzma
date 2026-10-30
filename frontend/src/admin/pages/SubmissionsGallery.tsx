import { WinnerBadge } from '../../components/WinnerBadge'
import { formatDateTime } from '../../lib/format'
import type { AdminSubmissionRow } from '../api'
import { PreviewThumb } from '../PreviewThumb'
import { DemoBadge, SubmissionStatusBadge } from '../ui'

/**
 * A work's picture in a list: the card-size copy of the file the list names as its cover — the admin's chosen
 * gallery image, else its first picture, else its first file; for a PDF, the page that stands for the work
 * (PreviewThumb). A work whose copy is not made yet, or that has no file, says so instead.
 */
export function SubmissionCover({ row, lazy = true }: { row: AdminSubmissionRow; lazy?: boolean }) {
  return <PreviewThumb submissionId={row.id} preview={row.preview} lazy={lazy} none="Fayl yoxdur" />
}

/**
 * The same page of results as the table, drawn for looking rather than reading: one card per work, the picture
 * large and the few facts that tell works apart under it. Everything else is one click away in the quick preview.
 */
export function SubmissionsGallery({ items, onOpen }: { items: AdminSubmissionRow[]; onOpen: (id: number) => void }) {
  return (
    <ul role="list" className="a-gallery" aria-label="Təkliflər — qalereya">
      {items.map((s) => (
        <li key={s.id} className={`a-card${s.isWinner ? ' is-winner' : ''}`}>
          <button
            type="button"
            className="a-card__open"
            onClick={() => onOpen(s.id)}
            aria-label={`${s.referenceNumber ?? 'Təklif'} — ${s.title || 'Adsız'}, ${s.participant.name}: önizləməni aç`}
          >
            <span className="a-card__visual">
              <SubmissionCover row={s} />
              {s.fileCount > 1 && <span className="a-card__files">{s.fileCount} fayl</span>}
            </span>
            <span className="a-card__body">
              <span className="a-card__ref tabular">
                {s.referenceNumber}
                {s.isWinner && <WinnerBadge size="sm" />}
                {s.isDemo && <DemoBadge />}
              </span>
              <span className="a-card__title">{s.title || 'Adsız'}</span>
              <span className="a-card__line">{s.participant.name}</span>
              <span className="a-card__line a-card__line--muted">{s.competition.name}</span>
              <span className="a-card__foot">
                <span className="tabular">{formatDateTime(s.submittedAt)}</span>
                <SubmissionStatusBadge status={s.status} />
              </span>
            </span>
          </button>
        </li>
      ))}
    </ul>
  )
}
