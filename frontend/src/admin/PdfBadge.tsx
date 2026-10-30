import type { AdminPrintItem } from './api'
import { pdfBadge, previewLabel } from './printPictures'

/**
 * "PDF · N səhifə" on a printed card — the card shows the page that stands for the work; the appendix, when asked
 * for, has every one — with "Önizləmə: 2/4" beside it on screen only: which page that is, for the admin (never printed).
 */
export function PdfBadge({ item }: { item: AdminPrintItem }) {
  const badge = pdfBadge(item)
  const label = previewLabel(item.image)
  return (
    <>
      {badge && <span className="pr-badge">{badge}</span>}
      {label && <span className="pr-screen-only pr-preview-note">{label}</span>}
    </>
  )
}
