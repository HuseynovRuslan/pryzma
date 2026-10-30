import { useState, type FormEvent } from 'react'
import type { FieldType } from '../../api/types'
import { PageError, PageLoading } from '../../components/PageState'
import { useApi } from '../../lib/useApi'
import { adminApi, type AdminCompetition, type AdminField, type FieldInput, type UploadPolicy } from '../api'
import { AField } from '../forms'
import { readErrors, type FieldErrors } from '../errors'
import { fieldTypeLabel, fieldTypes } from '../labels'
import { move } from '../lib'
import { ConfirmButton, Empty, Notice } from '../ui'

interface FileSettings {
  accept: string[]
  maxFileSizeMb: number
  maxFiles: number
}

interface Draft extends FieldInput {
  options: string
  file: FileSettings
  maxLength: string
}

const parseOptions = (json: string | null): Record<string, unknown> | string[] | null => {
  if (!json) return null
  try {
    return JSON.parse(json)
  } catch {
    return null
  }
}

function toDraft(f: AdminField | null, policy: UploadPolicy): Draft {
  const options = parseOptions(f?.optionsJson ?? null)
  const obj = options && !Array.isArray(options) ? options : {}
  return {
    key: f?.key ?? '',
    label: f?.label ?? '',
    fieldType: f?.fieldType ?? 'ShortText',
    placeholder: f?.placeholder ?? '',
    helpText: f?.helpText ?? '',
    isRequired: f?.isRequired ?? true,
    isSubmissionTitle: f?.isSubmissionTitle ?? false,
    isActive: f?.isActive ?? true,
    // Nothing becomes public by default; the admin opts each field in.
    isPublicAfterResults: f?.isPublicAfterResults ?? false,
    options: Array.isArray(options) ? options.join('\n') : '',
    maxLength: typeof obj.maxLength === 'number' ? String(obj.maxLength) : '',
    file: {
      accept: Array.isArray(obj.accept) ? (obj.accept as string[]) : policy.allowedTypes.map((t) => t.key),
      maxFileSizeMb: typeof obj.maxFileSizeMb === 'number' ? obj.maxFileSizeMb : policy.maxFileSizeMb,
      // A new upload field asks for one file: an entry is one work, and the competitions allow a separate entry
      // per nomination rather than several works bundled into one. An existing field keeps what it was saved
      // with, and the server's maximum is still there for a field that legitimately wants sketches beside it.
      maxFiles: typeof obj.maxFiles === 'number' ? obj.maxFiles : f ? policy.maxFilesPerSubmission : 1,
    },
  }
}

/** Serialises the type-specific settings into OptionsJson (see CompetitionField docs on the server). */
function toFieldInput(d: Draft): FieldInput {
  let optionsJson: string | null = null
  if (d.fieldType === 'Select') {
    optionsJson = JSON.stringify(d.options.split('\n').map((o) => o.trim()).filter(Boolean))
  } else if (d.fieldType === 'File') {
    optionsJson = JSON.stringify({ maxFiles: d.file.maxFiles, maxFileSizeMb: d.file.maxFileSizeMb, accept: d.file.accept })
  } else if ((d.fieldType === 'ShortText' || d.fieldType === 'LongText') && d.maxLength) {
    optionsJson = JSON.stringify({ maxLength: Number(d.maxLength) })
  }
  return {
    key: d.key || undefined,
    label: d.label,
    fieldType: d.fieldType,
    placeholder: d.placeholder || undefined,
    helpText: d.helpText || undefined,
    isRequired: d.isRequired,
    isSubmissionTitle: d.fieldType === 'ShortText' && d.isSubmissionTitle,
    isActive: d.isActive,
    isPublicAfterResults: d.isPublicAfterResults,
    optionsJson,
  }
}

function validateDraft(d: Draft, policy: UploadPolicy): FieldErrors {
  const errors: FieldErrors = { label: d.label.trim() ? undefined : 'Sahənin adını yazın.' }
  if (d.fieldType === 'Select' && !d.options.trim()) errors.optionsJson = 'Ən azı bir variant yazın.'
  if (d.fieldType === 'File') {
    if (d.file.accept.length === 0) errors.optionsJson = 'Ən azı bir format seçin.'
    else if (d.file.maxFileSizeMb < 1 || d.file.maxFileSizeMb > policy.maxFileSizeMb)
      errors.optionsJson = `Fayl ölçüsü 1–${policy.maxFileSizeMb} MB olmalıdır.`
    else if (d.file.maxFiles < 1 || d.file.maxFiles > policy.maxFilesPerSubmission)
      errors.optionsJson = `Fayl sayı 1–${policy.maxFilesPerSubmission} olmalıdır.`
  }
  return errors
}

export function FormBuilderTab({ competition, onChanged }: { competition: AdminCompetition; onChanged: () => Promise<void> }) {
  const state = useApi(
    async (signal) => ({
      fields: await adminApi.fields(competition.id, signal),
      policy: await adminApi.uploadPolicy(signal),
    }),
    [competition.id],
  )
  const [fields, setFields] = useState<AdminField[]>()
  const [selected, setSelected] = useState<number | 'new' | null>(null)
  const [message, setMessage] = useState<{ tone: 'error' | 'success'; text: string }>()

  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  const list = fields ?? state.data.fields
  const policy = state.data.policy
  const current = typeof selected === 'number' ? list.find((f) => f.id === selected) ?? null : null

  const reorder = async (index: number, delta: -1 | 1) => {
    const next = move(list, index, delta)
    setFields(next)
    try {
      setFields(await adminApi.reorderFields(competition.id, next.map((f) => f.id)))
    } catch (err) {
      setFields(list)
      setMessage({ tone: 'error', text: readErrors(err, 'Sıra dəyişmədi.').message })
    }
  }

  return (
    <div className="a-split a-split--wide-aside">
      <section className="a-panel" aria-labelledby="fields-title">
        <div className="a-panel__head">
          <h2 id="fields-title" className="a-panel__title">
            Təqdimat forması
          </h2>
          <button type="button" className="btn btn--primary btn--sm" onClick={() => setSelected('new')}>
            + Sahə əlavə et
          </button>
        </div>
        <p className="a-muted">
          İştirakçı bu sahələri sıra ilə doldurur. Cavab verilmiş sahənin növü dəyişmir, silinmir; onu deaktiv etmək olar —
          köhnə təkliflərdəki cavablar saxlanılır.
        </p>
        {message && <Notice tone={message.tone}>{message.text}</Notice>}
        {list.length === 0 ? (
          <Empty title="Formada hələ sahə yoxdur.">
            <p className="a-muted">Məsələn: “İşin adı” (qısa mətn), “Konsept” (uzun mətn), “Fayllar” (fayl).</p>
          </Empty>
        ) : (
          <ol role="list" className="a-fieldlist">
            {list.map((f, i) => (
              <li key={f.id} className={`a-fieldrow${selected === f.id ? ' is-selected' : ''}${f.isActive ? '' : ' is-inactive'}`}>
                <span className="a-order">
                  <button type="button" aria-label={`${f.label}: yuxarı`} disabled={i === 0} onClick={() => reorder(i, -1)}>
                    ↑
                  </button>
                  <button type="button" aria-label={`${f.label}: aşağı`} disabled={i === list.length - 1} onClick={() => reorder(i, 1)}>
                    ↓
                  </button>
                </span>
                <span className="a-fieldrow__main">
                  <span className="a-fieldrow__label">{f.label}</span>
                  <span className="a-fieldrow__tags">
                    <span className="a-tag a-tag--strong">{fieldTypeLabel[f.fieldType]}</span>
                    <span className="a-tag">{f.isRequired ? 'Məcburi' : 'İstəyə bağlı'}</span>
                    {f.isSubmissionTitle && <span className="a-tag">Təklifin adı</span>}
                    {!f.isActive && <span className="a-tag a-tag--strong">Deaktiv</span>}
                    {f.isPublicAfterResults && <span className="a-tag a-tag--public">Nəticədən sonra ictimai</span>}
                    {f.isLocked && <span className="a-tag">Cavablanıb: {f.answerCount}</span>}
                  </span>
                </span>
                <span className="a-fieldrow__actions">
                  <button type="button" className="btn btn--sm" onClick={() => setSelected(f.id)}>
                    Redaktə
                  </button>
                </span>
              </li>
            ))}
          </ol>
        )}
      </section>

      <div className="a-split__aside">
        {selected === null ? (
          <div className="a-panel">
            <p className="a-muted">Redaktə etmək üçün sahə seçin və ya yenisini əlavə edin.</p>
          </div>
        ) : (
          <FieldEditor
            key={selected}
            competitionId={competition.id}
            field={current}
            policy={policy}
            onClose={() => setSelected(null)}
            onSaved={async (saved, text) => {
              const exists = list.some((f) => f.id === saved.id)
              setFields(exists ? list.map((f) => (f.id === saved.id ? saved : f)) : [...list, saved])
              setSelected(saved.id)
              setMessage({ tone: 'success', text })
              // Setting a new title field clears the flag elsewhere; reload to stay in sync.
              setFields(await adminApi.fields(competition.id))
              await onChanged()
            }}
            onDeleted={async (id) => {
              setFields(list.filter((f) => f.id !== id))
              setSelected(null)
              setMessage({ tone: 'success', text: 'Sahə silindi.' })
              await onChanged()
            }}
          />
        )}
      </div>
    </div>
  )
}

function FieldEditor({
  competitionId,
  field,
  policy,
  onClose,
  onSaved,
  onDeleted,
}: {
  competitionId: number
  field: AdminField | null
  policy: UploadPolicy
  onClose: () => void
  onSaved: (f: AdminField, message: string) => Promise<void>
  onDeleted: (id: number) => Promise<void>
}) {
  const [d, setD] = useState<Draft>(() => toDraft(field, policy))
  const [errors, setErrors] = useState<FieldErrors>({})
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const locked = field?.isLocked ?? false

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => {
    setD((v) => ({ ...v, [key]: value }))
    setErrors((e) => ({ ...e, [key]: undefined, optionsJson: undefined }))
  }

  const save = async (e: FormEvent) => {
    e.preventDefault()
    const found = validateDraft(d, policy)
    setErrors(found)
    if (Object.values(found).some(Boolean)) return
    setBusy(true)
    setError(undefined)
    try {
      const input = toFieldInput(d)
      const saved = field
        ? await adminApi.updateField(competitionId, field.id, input)
        : await adminApi.createField(competitionId, input)
      await onSaved(saved, field ? 'Sahə yeniləndi.' : 'Sahə əlavə olundu.')
    } catch (err) {
      const { message, fields } = readErrors(err, 'Yadda saxlanılmadı.')
      setErrors(fields)
      setError(message)
    } finally {
      setBusy(false)
    }
  }

  const toggleFormat = (key: string) =>
    set('file', {
      ...d.file,
      accept: d.file.accept.includes(key) ? d.file.accept.filter((k) => k !== key) : [...d.file.accept, key],
    })

  return (
    <form className="a-panel a-form" onSubmit={save} noValidate aria-label={field ? `Sahə: ${field.label}` : 'Yeni sahə'}>
      <div className="a-panel__head">
        <h3 className="a-panel__title">{field ? 'Sahəni redaktə et' : 'Yeni sahə'}</h3>
        <button type="button" className="btn btn--ghost btn--sm" onClick={onClose}>
          Bağla
        </button>
      </div>
      {locked && <Notice>Bu sahəyə {field!.answerCount} cavab verilib: növü və açarı dəyişdirilmir.</Notice>}
      {error && <Notice tone="error">{error}</Notice>}

      <AField label="Sahənin adı" hint="İştirakçı bunu görür." error={errors.label}>
        <input className="input" value={d.label} onChange={(e) => set('label', e.target.value)} maxLength={200} />
      </AField>
      <AField label="Növ" error={errors.fieldType}>
        <select className="input" value={d.fieldType} disabled={locked} onChange={(e) => set('fieldType', e.target.value as FieldType)}>
          {fieldTypes.map((t) => (
            <option key={t} value={t}>
              {fieldTypeLabel[t]}
            </option>
          ))}
        </select>
      </AField>
      <AField label="İzah (istəyə bağlı)" hint="Sahənin altında kiçik mətn kimi görünür.">
        <textarea className="input" rows={2} value={d.helpText} onChange={(e) => set('helpText', e.target.value)} maxLength={1000} />
      </AField>
      {(d.fieldType === 'ShortText' || d.fieldType === 'LongText' || d.fieldType === 'Number' || d.fieldType === 'Url') && (
        <AField label="Nümunə mətn (istəyə bağlı)">
          <input className="input" value={d.placeholder} onChange={(e) => set('placeholder', e.target.value)} maxLength={200} />
        </AField>
      )}
      {(d.fieldType === 'ShortText' || d.fieldType === 'LongText') && (
        <AField label="Maksimal uzunluq (istəyə bağlı)" hint={d.fieldType === 'ShortText' ? 'Ən çox 200.' : 'Ən çox 5000.'} error={errors.optionsJson}>
          <input className="input" type="number" min={1} value={d.maxLength} onChange={(e) => set('maxLength', e.target.value)} />
        </AField>
      )}
      {d.fieldType === 'Select' && (
        <AField label="Variantlar" hint="Hər sətirdə bir variant." error={errors.optionsJson}>
          <textarea className="input" rows={4} value={d.options} onChange={(e) => set('options', e.target.value)} />
        </AField>
      )}
      {d.fieldType === 'File' && (
        <fieldset className="a-panel" style={{ background: 'var(--paper)', margin: 0 }}>
          <legend className="a-field__label" style={{ padding: '0 4px' }}>
            Yükləmə parametrləri
          </legend>
          <p className="a-field__hint">
            Server limiti: hər fayl ən çox {policy.maxFileSizeMb} MB, bir təklifə {policy.maxFilesPerSubmission} fayl. Burada
            yalnız daha sərt dəyər seçmək olar. SVG təhlükəsizlik səbəbindən bağlıdır.
          </p>
          <div className="a-checks" role="group" aria-label="İcazə verilən formatlar">
            {policy.allowedTypes.map((t) => (
              <label key={t.key} className="check">
                <input type="checkbox" checked={d.file.accept.includes(t.key)} onChange={() => toggleFormat(t.key)} />
                <span>{t.label}</span>
              </label>
            ))}
          </div>
          <div className="a-form__row">
            <AField label="Maksimum ölçü (MB)">
              <input
                className="input"
                type="number"
                min={1}
                max={policy.maxFileSizeMb}
                value={d.file.maxFileSizeMb}
                onChange={(e) => set('file', { ...d.file, maxFileSizeMb: Number(e.target.value) })}
              />
            </AField>
            <AField label="Maksimum fayl sayı">
              <input
                className="input"
                type="number"
                min={1}
                max={policy.maxFilesPerSubmission}
                value={d.file.maxFiles}
                onChange={(e) => set('file', { ...d.file, maxFiles: Number(e.target.value) })}
              />
            </AField>
          </div>
          {errors.optionsJson && <p className="a-field__error">{errors.optionsJson}</p>}
        </fieldset>
      )}
      <div className="a-checks" style={{ display: 'grid' }}>
        <label className="check">
          <input type="checkbox" checked={d.isRequired} onChange={(e) => set('isRequired', e.target.checked)} />
          <span>Məcburi sahə</span>
        </label>
        {d.fieldType === 'ShortText' && (
          <label className="check">
            <input type="checkbox" checked={d.isSubmissionTitle} onChange={(e) => set('isSubmissionTitle', e.target.checked)} />
            <span>Bu cavab təklifin adı kimi göstərilsin (siyahılarda)</span>
          </label>
        )}
        <label className="check">
          <input type="checkbox" checked={d.isActive} onChange={(e) => set('isActive', e.target.checked)} />
          <span>Aktiv (formada görünür)</span>
        </label>
      </div>
      <div className="a-panel a-public-toggle">
        <label className="check">
          <input
            type="checkbox"
            checked={d.isPublicAfterResults}
            onChange={(e) => set('isPublicAfterResults', e.target.checked)}
            aria-describedby="public-after-hint"
          />
          <span>Nəticələr dərc olunandan sonra qalereyada göstər</span>
        </label>
        <p id="public-after-hint" className="a-field__hint">
          {d.fieldType === 'File'
            ? 'Bu sahəyə yüklənmiş fayllar qalereyada görünəcək (şəkillər önizləmə, PDF yükləmə kimi). Faylın orijinal adı heç vaxt göstərilmir.'
            : 'İştirakçının bu sahəyə yazdığı cavab hamıya açıq olacaq. Şəxsi məlumat ola biləcək sahələri qeyd etməyin.'}{' '}
          Nəticə dərc olunana qədər heç nə ictimai deyil.
        </p>
      </div>
      <AField label="Texniki açar" hint={locked ? 'Cavablar bu açarla bağlıdır.' : 'Boş qalsa addan yaradılır.'} error={errors.key}>
        <input className="input" value={d.key} disabled={locked} onChange={(e) => set('key', e.target.value)} spellCheck={false} maxLength={60} />
      </AField>
      <div className="a-actions">
        <button type="submit" className="btn btn--primary btn--sm" disabled={busy}>
          {field ? 'Yadda saxla' : 'Sahəni əlavə et'}
        </button>
        {field && !locked && (
          <ConfirmButton
            label="Sil"
            confirmLabel="Bəli, sil"
            tone="danger"
            onConfirm={async () => {
              try {
                await adminApi.deleteField(competitionId, field.id)
                await onDeleted(field.id)
              } catch (err) {
                setError(readErrors(err, 'Silinmədi.').message)
              }
            }}
          />
        )}
      </div>
    </form>
  )
}
