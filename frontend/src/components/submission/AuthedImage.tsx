import { useEffect, useRef, useState } from 'react'
import { submissionsApi } from '../../api/endpoints'

/**
 * Image of an uploaded file. Files are served only with the owner's (or an admin's) bearer token, which <img src>
 * cannot send, so the bytes are fetched and shown from a local object URL.
 *
 * `thumb` asks for the server's card-size copy (a few dozen kilobytes) instead of the original, and never falls back
 * to the original: a list must not download multi-megabyte files to draw small previews. Until the copy exists the
 * placeholder says so; the original stays one click away.
 *
 * `page` (admins only) shows a later page of a PDF — the page that stands for the work, as the server names it —
 * rendered on request; page 1 is the file's own copy.
 *
 * `lazy` holds the request back until the image is about to scroll into view. A page of fifty cards then asks for
 * the few on screen and the rest as the reader reaches them — the `loading="lazy"` on the <img> alone cannot do
 * that, because the bytes are fetched before there is an <img> at all.
 */
export function AuthedImage({
  submissionId,
  fileId,
  alt,
  localUrl,
  variant = 'original',
  className = 'thumb',
  lazy = false,
  page = 1,
}: {
  submissionId: number
  fileId: number
  alt: string
  /** Object URL of the file picked in this session, if any — avoids a round trip. */
  localUrl?: string
  variant?: 'original' | 'thumb'
  className?: string
  lazy?: boolean
  /** A PDF's page after the first (admins only); 1 is the file's own copy. */
  page?: number
}) {
  const [url, setUrl] = useState<string | undefined>(localUrl)
  const [failed, setFailed] = useState(false)
  const [near, setNear] = useState(!lazy)
  const holder = useRef<HTMLSpanElement>(null)

  useEffect(() => {
    if (near) return
    const el = holder.current
    if (!el || typeof IntersectionObserver === 'undefined') {
      setNear(true)
      return
    }
    // A screen's worth of margin: by the time a card scrolls in, its picture is usually already there.
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setNear(true)
          observer.disconnect()
        }
      },
      { rootMargin: '600px 0px' },
    )
    observer.observe(el)
    return () => observer.disconnect()
  }, [near])

  useEffect(() => {
    if (localUrl || !near) return
    const controller = new AbortController()
    let objectUrl: string | undefined
    const load =
      page > 1
        ? (id: number, file: number, signal: AbortSignal) => submissionsApi.pageBlob(id, file, page, signal, variant === 'thumb')
        : variant === 'thumb'
          ? submissionsApi.thumbBlob
          : submissionsApi.fileBlob
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
  }, [submissionId, fileId, localUrl, variant, near, page])

  if (failed)
    return (
      <span className={`${className} thumb--missing`} role={alt ? 'img' : undefined} aria-label={alt ? `${alt} — önizləmə hələ hazır deyil` : undefined}>
        {variant === 'thumb' && <span className="thumb__note">Önizləmə hazırlanır</span>}
      </span>
    )
  if (!url) return <span ref={holder} className={`${className} skeleton`} aria-hidden="true" />
  return <img className={className} src={url} alt={alt} loading="lazy" decoding="async" />
}
