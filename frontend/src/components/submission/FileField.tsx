import { useEffect, useId, useRef, useState, type DragEvent } from 'react'
import { ApiError } from '../../api/client'
import { submissionsApi } from '../../api/endpoints'
import type { FormField, SubmissionFile } from '../../api/types'
import { formatBytes } from '../../lib/format'
import { acceptAttribute, describeFileRules, fileError } from '../../lib/formRules'
import { FieldError } from '../Form'
import { AuthedImage } from './AuthedImage'

interface Pending {
  id: string
  name: string
  size: number
  type: string
  progress: number
  error?: string
  previewUrl?: string
  abort?: () => void
}

interface Props {
  submissionId: number
  field: FormField
  files: SubmissionFile[]
  error?: string
  disabled?: boolean
  onUploaded: (file: SubmissionFile) => void
  onRemoved: (fileId: number) => void
}

const isImage = (type: string) => type.startsWith('image/')
const typeLabel = (contentType: string, name: string) =>
  contentType === 'application/pdf' ? 'PDF' : (name.split('.').pop() ?? '').toUpperCase()

/**
 * Upload area for a File field: drag & drop on desktop, a plain "Fayl seç" button everywhere (the reliable
 * path on phones). Files upload immediately with progress; rejected files explain why and can be dismissed.
 */
export function FileField({ submissionId, field, files, error, disabled, onUploaded, onRemoved }: Props) {
  const rules = field.files!
  const id = useId()
  const inputRef = useRef<HTMLInputElement>(null)
  const [pending, setPending] = useState<Pending[]>([])
  const [dragging, setDragging] = useState(false)
  const [removing, setRemoving] = useState<number | null>(null)
  const [removeError, setRemoveError] = useState<string>()
  // Local previews for files picked in this session, keyed by server file id.
  const [previews, setPreviews] = useState<Record<number, string>>({})
  const previewsRef = useRef(previews)
  previewsRef.current = previews

  useEffect(() => () => Object.values(previewsRef.current).forEach((u) => URL.revokeObjectURL(u)), [])

  const uploadingCount = pending.filter((p) => !p.error).length
  const slotsLeft = rules.maxFiles - files.length - uploadingCount

  const update = (pid: string, patch: Partial<Pending>) =>
    setPending((list) => list.map((p) => (p.id === pid ? { ...p, ...patch } : p)))

  const addFiles = (picked: FileList | File[]) => {
    let attached = files.length + uploadingCount
    for (const file of Array.from(picked)) {
      const pid = `${file.name}-${file.size}-${Math.random().toString(36).slice(2)}`
      const problem = fileError(file, rules, attached)
      const previewUrl = !problem && isImage(file.type) ? URL.createObjectURL(file) : undefined
      const item: Pending = { id: pid, name: file.name, size: file.size, type: file.type, progress: 0, error: problem, previewUrl }
      setPending((list) => [...list, item])
      if (problem) continue
      attached++

      const handle = submissionsApi.upload(submissionId, field.key, file, (fraction) => update(pid, { progress: fraction }))
      update(pid, { abort: handle.abort })
      handle.promise
        .then((saved) => {
          if (previewUrl) setPreviews((p) => ({ ...p, [saved.id]: previewUrl }))
          setPending((list) => list.filter((p) => p.id !== pid))
          onUploaded(saved)
        })
        .catch((err: unknown) => {
          if (err instanceof DOMException && err.name === 'AbortError') {
            setPending((list) => list.filter((p) => p.id !== pid))
            return
          }
          const message =
            err instanceof ApiError ? (err.fieldErrors[field.key]?.[0] ?? err.message) : 'Fayl yüklənmədi.'
          update(pid, { error: message, progress: 0 })
          if (previewUrl) URL.revokeObjectURL(previewUrl)
        })
    }
  }

  const dismiss = (pid: string) =>
    setPending((list) => {
      const item = list.find((p) => p.id === pid)
      item?.abort?.()
      return list.filter((p) => p.id !== pid)
    })

  const remove = async (fileId: number) => {
    setRemoveError(undefined)
    setRemoving(fileId)
    try {
      await submissionsApi.removeFile(submissionId, fileId)
      if (previews[fileId]) URL.revokeObjectURL(previews[fileId])
      onRemoved(fileId)
    } catch (err) {
      setRemoveError(err instanceof ApiError ? err.message : 'Fayl silinmədi.')
    } finally {
      setRemoving(null)
    }
  }

  const onDrop = (e: DragEvent) => {
    e.preventDefault()
    setDragging(false)
    if (!disabled && e.dataTransfer.files.length) addFiles(e.dataTransfer.files)
  }

  return (
    <div className="ffield">
      <div className="dfield__head">
        <p className="dfield__label" id={`${id}-label`}>
          {field.label}
          {!field.isRequired && <span className="dfield__optional"> (istəyə bağlı)</span>}
        </p>
        <span className="dfield__count tabular">
          {files.length} / {rules.maxFiles}
        </span>
      </div>
      {field.helpText && <p className="field__hint">{field.helpText}</p>}

      <div
        className={`dropzone${dragging ? ' is-dragging' : ''}${error ? ' is-invalid' : ''}`}
        onDragOver={(e) => {
          e.preventDefault()
          if (!disabled) setDragging(true)
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={onDrop}
      >
        <p className="dropzone__lead">
          <span className="dropzone__desktop">Faylları bura sürüşdürün və ya</span>
        </p>
        <input
          ref={inputRef}
          id={`${id}-input`}
          type="file"
          className="visually-hidden"
          multiple={rules.maxFiles > 1}
          accept={acceptAttribute(rules)}
          disabled={disabled || slotsLeft <= 0}
          aria-labelledby={`${id}-label`}
          aria-describedby={`${id}-rules`}
          onChange={(e) => {
            if (e.target.files) addFiles(e.target.files)
            e.target.value = ''
          }}
        />
        <button
          type="button"
          className="btn btn--primary"
          disabled={disabled || slotsLeft <= 0}
          onClick={() => inputRef.current?.click()}
        >
          Fayl seç
        </button>
        <p id={`${id}-rules`} className="dropzone__rules">
          {slotsLeft <= 0 ? `Limitə çatdınız: ${rules.maxFiles} fayl.` : describeFileRules(rules)}
        </p>
      </div>
      {error && <FieldError message={error} />}
      {removeError && <FieldError message={removeError} />}

      {(files.length > 0 || pending.length > 0) && (
        <ul role="list" className="filelist">
          {files.map((f) => (
            <li key={f.id} className="filecard">
              <span className="filecard__visual">
                {isImage(f.contentType) ? (
                  <AuthedImage submissionId={submissionId} fileId={f.id} alt="" localUrl={previews[f.id]} />
                ) : (
                  <span className="thumb thumb--doc" aria-hidden="true">
                    {typeLabel(f.contentType, f.originalFileName)}
                  </span>
                )}
              </span>
              <span className="filecard__meta">
                <span className="filecard__name" title={f.originalFileName}>
                  {f.originalFileName}
                </span>
                <span className="filecard__info">
                  {typeLabel(f.contentType, f.originalFileName)}, {formatBytes(f.sizeBytes)}
                </span>
              </span>
              {!disabled && (
                <button
                  type="button"
                  className="filecard__remove"
                  onClick={() => remove(f.id)}
                  disabled={removing === f.id}
                  aria-label={`${f.originalFileName} faylını sil`}
                >
                  {removing === f.id ? 'Silinir…' : 'Sil'}
                </button>
              )}
            </li>
          ))}
          {pending.map((p) => (
            <li key={p.id} className={`filecard${p.error ? ' is-error' : ' is-uploading'}`}>
              <span className="filecard__visual">
                {p.previewUrl ? (
                  <img className="thumb" src={p.previewUrl} alt="" />
                ) : (
                  <span className="thumb thumb--doc" aria-hidden="true">
                    {(p.name.split('.').pop() ?? '').toUpperCase().slice(0, 4)}
                  </span>
                )}
              </span>
              <span className="filecard__meta">
                <span className="filecard__name" title={p.name}>
                  {p.name}
                </span>
                {p.error ? (
                  <span className="filecard__error" role="alert">
                    {p.error}
                  </span>
                ) : (
                  <>
                    <span className="filecard__info">
                      Yüklənir… <span className="tabular">{Math.round(p.progress * 100)}%</span>
                    </span>
                    <span
                      className="filecard__progress"
                      role="progressbar"
                      aria-label={`${p.name} yüklənir`}
                      aria-valuemin={0}
                      aria-valuemax={100}
                      aria-valuenow={Math.round(p.progress * 100)}
                    >
                      <span style={{ transform: `scaleX(${p.progress})` }} />
                    </span>
                  </>
                )}
              </span>
              <button type="button" className="filecard__remove" onClick={() => dismiss(p.id)}>
                {p.error ? 'Bağla' : 'Ləğv et'}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
