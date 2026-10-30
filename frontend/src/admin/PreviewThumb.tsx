import { AuthedImage } from '../components/submission/AuthedImage'
import type { AdminPreview, AdminPrintImage } from './api'

/**
 * A work's picture in an admin list, exactly as the server named it (AdminPreview): the card-size copy of its cover
 * file — for a PDF, of the page that stands for the work (the admin's choice, else the automatic one). No screen picks
 * a page itself. Only the small copy is ever requested; a work without one says so instead.
 */
export function PreviewThumb({
  submissionId,
  preview,
  lazy = false,
  pending = 'Önizləmə hazırlanır',
  none = 'Şəkil yoxdur',
}: {
  submissionId: number
  preview: AdminPreview | null
  lazy?: boolean
  pending?: string
  none?: string
}) {
  if (!preview) return <span className="a-art a-art--none">{none}</span>
  const page = preview.page ?? 1
  // A later page is rendered on request from the PDF's safe copy; page 1 needs the file's own small copy.
  if (page === 1 && !preview.thumbnailReady) return <span className="a-art a-art--none">{pending}</span>
  return (
    <AuthedImage
      submissionId={submissionId}
      fileId={preview.fileId}
      page={page}
      alt=""
      variant="thumb"
      className="a-art"
      lazy={lazy}
    />
  )
}

/**
 * A work's picture as the prints, the vote ranking and the final name it (AdminPrintImage), at card size: for a PDF
 * the page that stands for the work. The server picked it; this only fetches it.
 */
export function ImageThumb({
  submissionId,
  image,
  className = 'a-art',
  lazy = true,
}: {
  submissionId: number
  image: AdminPrintImage | null | undefined
  className?: string
  lazy?: boolean
}) {
  if (!image) return <span className={`${className} a-art--none`}>Şəkil yoxdur</span>
  return (
    <AuthedImage
      submissionId={submissionId}
      fileId={image.fileId}
      page={image.page ?? 1}
      alt=""
      variant="thumb"
      className={className}
      lazy={lazy}
    />
  )
}
