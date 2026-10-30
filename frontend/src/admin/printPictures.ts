/**
 * Pictures for the admin's printed documents, fetched with the admin's own credentials (nothing is public before the
 * result), a few at a time, kept as local object URLs for the page's life and released when it closes.
 */
import { useEffect, useMemo, useReducer, useRef } from 'react'
import { submissionsApi } from '../api/endpoints'
import { adminApi, type AdminPrintImage, type AdminPrintItem, type AdminPrintMaterial } from './api'

export type Picture = { url: string } | 'failed'

/** Pictures fetched at once; enough to be quick, few enough not to crowd out the rest of the admin. */
const PARALLEL = 4

/** A queue of picture fetches, each kept once its key is in the cache. */
export function usePictureCache<T>(jobs: T[] | undefined, keyOf: (job: T) => string, fetch: (job: T, signal: AbortSignal) => Promise<Blob>) {
  const cache = useRef(new Map<string, Picture>())
  const [, changed] = useReducer((n: number) => n + 1, 0)

  useEffect(
    () => () => {
      for (const p of cache.current.values()) if (p !== 'failed') URL.revokeObjectURL(p.url)
    },
    [],
  )

  useEffect(() => {
    if (!jobs) return
    const controller = new AbortController()
    const todo = jobs.filter((j) => !cache.current.has(keyOf(j)))
    let next = 0
    const worker = async () => {
      while (!controller.signal.aborted && next < todo.length) {
        const job = todo[next++]
        try {
          const blob = await fetch(job, controller.signal)
          if (controller.signal.aborted) return
          cache.current.set(keyOf(job), { url: URL.createObjectURL(blob) })
        } catch {
          if (controller.signal.aborted) return
          cache.current.set(keyOf(job), 'failed')
        }
        changed()
      }
    }
    void Promise.all(Array.from({ length: PARALLEL }, worker))
    return () => controller.abort()
    // The jobs decide; keyOf and fetch are plain functions of them.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [jobs])

  return useMemo(
    () => (key: string): Picture | undefined => cache.current.get(key),
    // A new function after every arrival, so the page re-renders with it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [jobs, cache.current.size],
  )
}

/**
 * The bytes of a work's picture as the server named it (SubmissionImages): for a PDF the page that stands for it —
 * a page after the first rendered on request — else the metadata-free 1600 px copy where it is ready, else the
 * card-size one. Every admin screen and print fetches a work's picture through here; none picks a page itself.
 */
export function pictureBlob(submissionId: number, image: AdminPrintImage, signal?: AbortSignal): Promise<Blob> {
  if ((image.page ?? 1) > 1) return adminApi.documentPageBlob(submissionId, image.fileId, image.page!, signal)
  return image.large
    ? adminApi.publicAssetBlob(submissionId, image.fileId, signal)
    : submissionsApi.thumbBlob(submissionId, image.fileId, signal)
}

/**
 * "Önizləmə: 2/4" — which page of a PDF stands for the work, for admins on screen (never printed); null for a picture
 * and for a one-page PDF, where there is nothing to choose.
 */
export const previewLabel = (image: AdminPrintImage | null | undefined) =>
  image?.pages && image.pages > 1 ? `Önizləmə: ${image.page ?? 1}/${image.pages}` : null

const pictureKey = (i: AdminPrintItem) => `${i.id}:${i.image?.fileId}:${i.image?.page ?? 1}:${i.image?.large ? 'l' : 's'}`

/** Each work's card picture (pictureBlob). */
export function usePictures(items: AdminPrintItem[] | undefined) {
  const jobs = useMemo(() => items?.filter((i) => i.image), [items])
  const get = usePictureCache(jobs, pictureKey, (item, signal) => pictureBlob(item.id, item.image!, signal))
  return useMemo(() => (item: AdminPrintItem): Picture | undefined => (item.image ? get(pictureKey(item)) : undefined), [get])
}

/**
 * The picture re-encoded as a JPEG on white, its longer side at most `px` — every one, small ones too: a picture with
 * transparency would otherwise go into the PDF uncompressed with a mask. The original when the browser cannot
 * re-encode.
 */
export async function shrinkForPrint(blob: Blob, px: number): Promise<Blob> {
  try {
    if (typeof createImageBitmap !== 'function' || typeof document === 'undefined') return blob
    const bitmap = await createImageBitmap(blob)
    const scale = Math.min(1, px / Math.max(bitmap.width, bitmap.height))
    const canvas = document.createElement('canvas')
    canvas.width = Math.round(bitmap.width * scale)
    canvas.height = Math.round(bitmap.height * scale)
    const ctx = canvas.getContext('2d')
    if (!ctx) return blob
    ctx.fillStyle = '#fff'
    ctx.fillRect(0, 0, canvas.width, canvas.height)
    ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
    bitmap.close()
    return await new Promise<Blob>((resolve) => canvas.toBlob((b) => resolve(b ?? blob), 'image/jpeg', 0.84))
  } catch {
    return blob
  }
}

/** Each work's card picture (pictureBlob), re-encoded for print at most `px` on its longer side (shrinkForPrint). */
export function usePrintPictures(items: AdminPrintItem[] | undefined, px: number) {
  const jobs = useMemo(() => items?.filter((i) => i.image), [items])
  const get = usePictureCache(jobs, pictureKey, async (item, signal) => shrinkForPrint(await pictureBlob(item.id, item.image!, signal), px))
  return useMemo(() => (item: AdminPrintItem): Picture | undefined => (item.image ? get(pictureKey(item)) : undefined), [get])
}

export interface MaterialPage {
  item: AdminPrintItem
  m: AdminPrintMaterial
  /** This page's place in the work's material (1-based) and how many there are. */
  index: number
  count: number
}

const materialKey = (p: MaterialPage) => `${p.item.id}:${p.m.fileId}:${p.m.page}`

/** Every page of every work's material, in order — the printed appendix. */
export const materialPages = (items: AdminPrintItem[]): MaterialPage[] =>
  items.flatMap((item) => item.material.map((m, i) => ({ item, m, index: i + 1, count: item.material.length })))

/** The appendix's pictures: a picture's or a PDF's first page safe copy, or a later PDF page rendered for print. */
export function useMaterial(pages: MaterialPage[] | undefined) {
  const get = usePictureCache(pages, materialKey, ({ item, m }, signal) =>
    m.kind === 'pdf' && m.page > 1
      ? adminApi.documentPageBlob(item.id, m.fileId, m.page, signal)
      : adminApi.publicAssetBlob(item.id, m.fileId, signal),
  )
  return useMemo(() => (p: MaterialPage): Picture | undefined => get(materialKey(p)), [get])
}

/** "PDF · 12 səhifə" for a work with a PDF; nothing otherwise. */
export const pdfBadge = (item: AdminPrintItem) => (item.pdfPages > 0 ? `PDF · ${item.pdfPages} səhifə` : null)
