import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import type { UploadHandle } from '../api/client'
import { readErrors } from './errors'
import { ConfirmButton, Notice } from './ui'

interface Props<T> {
  /** Panel heading: "Örtük şəkli", "Poster"… */
  title: string
  hint: ReactNode
  image: string | null
  /** Current image, when the page does not already preview it elsewhere. */
  preview?: ReactNode
  upload: (file: File, onProgress: (fraction: number) => void) => UploadHandle<T>
  remove: () => Promise<T>
  onChange: (updated: T) => void
}

/** Upload, replace or remove one public image (a project cover, a competition poster). The server re-encodes it. */
export function CoverPanel<T>({ title, hint, image, preview, upload, remove, onChange }: Props<T>) {
  const titleId = useId()
  const input = useRef<HTMLInputElement>(null)
  const [progress, setProgress] = useState<number>()
  const [error, setError] = useState<string>()
  const [abort, setAbort] = useState<() => void>()

  useEffect(() => () => abort?.(), [abort])

  const start = (file: File) => {
    setError(undefined)
    setProgress(0)
    const handle = upload(file, setProgress)
    setAbort(() => handle.abort)
    handle.promise
      .then(onChange)
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return
        const { message, fields } = readErrors(err, 'Şəkil yüklənmədi.')
        setError(fields.cover ?? message)
      })
      .finally(() => setProgress(undefined))
  }

  return (
    <section className="a-panel" aria-labelledby={titleId}>
      <h2 id={titleId} className="a-panel__title">
        {title}
      </h2>
      <p className="a-muted">{hint}</p>
      {image && preview}
      {error && <Notice tone="error">{error}</Notice>}
      {progress !== undefined && <p className="a-muted tabular">Yüklənir… {Math.round(progress * 100)}%</p>}
      <input
        ref={input}
        type="file"
        accept=".png,.jpg,.jpeg,.webp,image/png,image/jpeg,image/webp"
        className="visually-hidden"
        aria-label={`${title} seçin`}
        onChange={(e) => {
          const file = e.target.files?.[0]
          if (file) start(file)
          e.target.value = ''
        }}
      />
      <div className="a-actions">
        <button type="button" className="btn btn--sm" onClick={() => input.current?.click()} disabled={progress !== undefined}>
          {image ? 'Şəkli dəyiş' : 'Şəkil yüklə'}
        </button>
        {image && <ConfirmButton label="Şəkli sil" confirmLabel="Bəli, sil" onConfirm={async () => onChange(await remove())} />}
      </div>
    </section>
  )
}
