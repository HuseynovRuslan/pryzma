import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { submissionsApi } from '../../api/endpoints'
import type { Submission, SubmissionFile } from '../../api/types'
import { Check } from '../../components/Icons'
import { PageError, PageLoading } from '../../components/PageState'
import { DynamicField } from '../../components/submission/DynamicField'
import { FileField } from '../../components/submission/FileField'
import { Stepper, type Step } from '../../components/submission/Stepper'
import { SubmissionSummary } from '../../components/submission/SubmissionSummary'
import { accentVars } from '../../lib/accent'
import { track } from '../../lib/analytics'
import { formatDateTime, submissionStatusLabel } from '../../lib/format'
import { hasAny, validateFields, type Answers, type FieldErrorMap } from '../../lib/formRules'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { focusFirstInvalid } from '../../lib/validation'
import './Submission.css'

/** Draft → multi-step form; anything submitted → read-only receipt. */
export default function SubmissionPage() {
  const { id = '' } = useParams()
  const submissionId = Number(id)
  const state = useApi((signal) => submissionsApi.get(submissionId, signal), [submissionId])
  const [submitted, setSubmitted] = useState<Submission>()

  useTitle(submitted ? 'Təklif qəbul edildi' : state.data?.isEditable ? 'Təklif hazırlanır' : 'Təklif')

  // The confirmation replaces the preview in place; bring the reader to the top where it starts.
  useEffect(() => {
    if (submitted) window.scrollTo({ top: 0 })
  }, [submitted])

  if (submitted) return <Receipt submission={submitted} justSubmitted />
  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  return state.data.isEditable ? (
    <Wizard initial={state.data} onSubmitted={setSubmitted} />
  ) : (
    <Receipt submission={state.data} justSubmitted={false} />
  )
}

// ---- Context header shared by every state ----------------------------------------------------

function ContextBar({ s }: { s: Submission }) {
  const back = `/competitions/${s.competition.slug}${s.category ? `?category=${encodeURIComponent(s.category.slug)}` : ''}`
  return (
    <div className="sub-context">
      <Link to={back} className="sub-context__back">
        <span aria-hidden="true" className="sub-context__arrow" />
        Müsabiqəyə qayıt
      </Link>
      <p className="sub-context__line">
        <span className="sub-context__swatch" aria-hidden="true" />
        <span>{s.project.name}</span>
        <span className="sub-context__sep" aria-hidden="true" />
        <span className="sub-context__competition">{s.competition.title}</span>
        {s.category && (
          <>
            <span className="sub-context__sep" aria-hidden="true" />
            <strong>{s.category.name}</strong>
          </>
        )}
      </p>
    </div>
  )
}

// ---- Wizard ------------------------------------------------------------------------------------

type StepKey = 'details' | 'files' | 'preview'

function Wizard({ initial, onSubmitted }: { initial: Submission; onSubmitted: (s: Submission) => void }) {
  const [sub, setSub] = useState(initial)
  const [answers, setAnswers] = useState<Answers>(initial.answers)
  const [errors, setErrors] = useState<FieldErrorMap>({})
  const [formError, setFormError] = useState<string>()
  const [saveState, setSaveState] = useState<'idle' | 'saving' | 'saved' | 'error'>('idle')
  const [busy, setBusy] = useState(false)
  const [params, setParams] = useSearchParams()
  const formRef = useRef<HTMLDivElement>(null)
  const version = useRef(0)
  const savedVersion = useRef(0)

  const dataFields = useMemo(() => sub.fields.filter((f) => f.fieldType !== 'File'), [sub.fields])
  const fileFields = useMemo(() => sub.fields.filter((f) => f.fieldType === 'File'), [sub.fields])
  const steps: (Step & { key: StepKey | 'done' })[] = [
    { key: 'details', label: 'Məlumatlar' },
    ...(fileFields.length ? [{ key: 'files' as const, label: 'Fayllar' }] : []),
    { key: 'preview', label: 'Önizləmə' },
    { key: 'done', label: 'Təsdiq' },
  ]
  const requested = params.get('step')
  const step: StepKey = steps.some((s) => s.key === requested && s.key !== 'done') ? (requested as StepKey) : 'details'
  const stepIndex = steps.findIndex((s) => s.key === step)

  const goTo = (next: StepKey) => {
    setFormError(undefined)
    setParams(next === 'details' ? {} : { step: next })
    window.scrollTo({ top: 0, behavior: 'smooth' })
  }

  // ---- Saving (explicit, on step change, and quietly while typing) ----

  const save = useCallback(async (): Promise<boolean> => {
    const at = version.current
    const payload = Object.fromEntries(dataFields.map((f) => [f.key, answers[f.key] ?? null]))
    setSaveState('saving')
    try {
      const updated = await submissionsApi.save(sub.id, payload)
      setSub(updated)
      savedVersion.current = Math.max(savedVersion.current, at)
      setSaveState('saved')
      return true
    } catch (err) {
      const apiErr = err instanceof ApiError ? err : new ApiError(0, 'Yadda saxlanılmadı.')
      const fieldMessages = Object.fromEntries(Object.entries(apiErr.fieldErrors).map(([k, v]) => [k, v[0]]))
      setErrors((e) => ({ ...e, ...fieldMessages }))
      setFormError(Object.keys(fieldMessages).length ? 'Bəzi sahələrdə düzəliş tələb olunur.' : apiErr.message)
      setSaveState('error')
      return false
    }
  }, [answers, dataFields, sub.id])

  const dirty = () => version.current > savedVersion.current

  // Quiet autosave 1.5 s after the participant stops typing.
  useEffect(() => {
    if (version.current <= savedVersion.current) return
    const timer = window.setTimeout(() => void save(), 1500)
    return () => window.clearTimeout(timer)
  }, [answers, save])

  const change = (key: string, value: string | null) => {
    version.current++
    setAnswers((a) => ({ ...a, [key]: value }))
    if (errors[key]) setErrors((e) => ({ ...e, [key]: undefined }))
  }

  // ---- Step transitions ----

  const fileCounts = Object.fromEntries(fileFields.map((f) => [f.key, sub.files.filter((x) => x.fieldKey === f.key).length]))

  const nextFromDetails = async () => {
    const found = validateFields(dataFields, answers, {})
    setErrors(found)
    if (hasAny(found)) {
      setFormError('Davam etmək üçün qeyd olunan sahələri tamamlayın.')
      focusFirstInvalid(formRef.current)
      return
    }
    setBusy(true)
    const ok = dirty() || saveState === 'idle' ? await save() : true
    setBusy(false)
    if (ok) goTo(fileFields.length ? 'files' : 'preview')
  }

  const nextFromFiles = () => {
    const found = validateFields(fileFields, answers, fileCounts)
    setErrors(found)
    if (hasAny(found)) {
      setFormError('Davam etmək üçün faylları əlavə edin.')
      return
    }
    goTo('preview')
  }

  const onUploaded = (file: SubmissionFile) => {
    setSub((s) => ({ ...s, files: [...s.files, file] }))
    setErrors((e) => ({ ...e, [file.fieldKey]: undefined }))
  }
  const onRemoved = (fileId: number) => setSub((s) => ({ ...s, files: s.files.filter((f) => f.id !== fileId) }))

  return (
    <div className="container sub" style={accentVars(sub.project.accentColor)}>
      <ContextBar s={sub} />
      <header className="sub__head">
        <h1>Təklifinizi hazırlayın</h1>
        <Stepper steps={steps} current={stepIndex} />
      </header>

      <div className="sub__layout">
        <div className="sub__main" ref={formRef}>
          {formError && (
            <div className="alert" role="alert">
              {formError}
            </div>
          )}

          {step === 'details' && (
            <section aria-labelledby="step-title" className="sub__step">
              <h2 id="step-title" className="sub__step-title">
                İşin məlumatları
              </h2>
              <div className="sub__fields">
                {dataFields.map((f) => (
                  <DynamicField
                    key={f.key}
                    field={f}
                    value={answers[f.key] ?? null}
                    error={errors[f.key]}
                    onChange={(v) => change(f.key, v)}
                  />
                ))}
              </div>
              <StepActions
                primary={{ label: 'Davam et', onClick: nextFromDetails, busy }}
                saveState={saveState}
                savedAt={sub.updatedAt}
                onSave={() => void save()}
              />
            </section>
          )}

          {step === 'files' && (
            <section aria-labelledby="step-title" className="sub__step">
              <h2 id="step-title" className="sub__step-title">
                İşin faylları
              </h2>
              <div className="sub__fields">
                {fileFields.map((f) => (
                  <FileField
                    key={f.key}
                    submissionId={sub.id}
                    field={f}
                    files={sub.files.filter((x) => x.fieldKey === f.key)}
                    error={errors[f.key]}
                    onUploaded={onUploaded}
                    onRemoved={onRemoved}
                  />
                ))}
              </div>
              <StepActions
                back={{ label: 'Geri', onClick: () => goTo('details') }}
                primary={{ label: 'Önizləməyə keç', onClick: nextFromFiles }}
                saveState="saved"
                savedAt={sub.updatedAt}
                note="Fayllar seçilən kimi qaralamaya yüklənir."
              />
            </section>
          )}

          {step === 'preview' && (
            <Preview
              sub={sub}
              onEdit={(target) => goTo(target)}
              onChecked={setSub}
              onSubmitted={onSubmitted}
            />
          )}
        </div>

        <aside className="sub__aside" aria-label="Qaralama">
          <p className="sub__aside-label">Status</p>
          <p className="sub__aside-status">Qaralama</p>
          <p className="sub__aside-note">
            Təqdim edənə qədər təklif yalnız sizə görünür və istənilən vaxt davam etdirilə bilər.
          </p>
          <Link to="/my/submissions" className="sub__aside-link">
            İşlərimə keç
          </Link>
        </aside>
      </div>
    </div>
  )
}

function StepActions({
  back,
  primary,
  saveState,
  savedAt,
  onSave,
  note,
}: {
  back?: { label: string; onClick: () => void }
  primary: { label: string; onClick: () => void; busy?: boolean }
  saveState: 'idle' | 'saving' | 'saved' | 'error'
  savedAt: string
  onSave?: () => void
  note?: string
}) {
  return (
    <div className="sub__actions">
      <p className="sub__save" aria-live="polite">
        {saveState === 'saving'
          ? 'Yadda saxlanılır…'
          : saveState === 'saved'
            ? `Qaralama saxlanıldı: ${formatDateTime(savedAt)}`
            : saveState === 'error'
              ? 'Qaralama saxlanılmadı.'
              : (note ?? 'Yazdıqlarınız avtomatik saxlanılır.')}
      </p>
      <div className="sub__buttons">
        {back && (
          <button type="button" className="btn btn--lg" onClick={back.onClick}>
            {back.label}
          </button>
        )}
        {onSave && (
          <button type="button" className="btn btn--ghost btn--lg" onClick={onSave}>
            Qaralama kimi saxla
          </button>
        )}
        <button
          type="button"
          className="btn btn--primary btn--lg"
          onClick={primary.onClick}
          disabled={primary.busy}
          aria-busy={primary.busy}
        >
          {primary.busy && <span className="btn__spinner" aria-hidden="true" />}
          {primary.label}
        </button>
      </div>
    </div>
  )
}

// ---- Preview & final submit -------------------------------------------------------------------

function Preview({
  sub,
  onEdit,
  onChecked,
  onSubmitted,
}: {
  sub: Submission
  onEdit: (step: StepKey) => void
  onChecked: (s: Submission) => void
  onSubmitted: (s: Submission) => void
}) {
  const [issues, setIssues] = useState<Record<string, string[]>>()
  const [checking, setChecking] = useState(true)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string>()

  useEffect(() => {
    let cancelled = false
    submissionsApi
      .check(sub.id)
      .then((checked) => {
        if (cancelled) return
        onChecked(checked)
        setIssues(undefined)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        const apiErr = err instanceof ApiError ? err : new ApiError(0, 'Yoxlama alınmadı.')
        if (Object.keys(apiErr.fieldErrors).length) setIssues(apiErr.fieldErrors)
        else setError(apiErr.message)
      })
      .finally(() => !cancelled && setChecking(false))
    return () => {
      cancelled = true
    }
    // Checked once on entering the step.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sub.id])

  const submit = async () => {
    setSubmitting(true)
    setError(undefined)
    try {
      const sent = await submissionsApi.submit(sub.id)
      // The one moment an entry is accepted — where a Lead / CompleteRegistration event belongs (lib/analytics.ts).
      // Only here: the recovery below shows an entry some other tab already sent, and that tab has counted it.
      track({
        name: 'submission_completed',
        slug: sent.competition.slug,
        submissionId: sent.id,
        reference: sent.referenceNumber,
      })
      onSubmitted(sent)
    } catch (err) {
      const apiErr = err instanceof ApiError ? err : new ApiError(0, 'Təqdim etmək alınmadı.')
      if (apiErr.status === 409) {
        // Possibly submitted already (another tab, a double click): show the real state.
        const fresh = await submissionsApi.get(sub.id).catch(() => null)
        if (fresh && !fresh.isEditable) return onSubmitted(fresh)
      }
      if (Object.keys(apiErr.fieldErrors).length) setIssues(apiErr.fieldErrors)
      else setError(apiErr.message)
      setSubmitting(false)
    }
  }

  const fileKeys = new Set(sub.fields.filter((f) => f.fieldType === 'File').map((f) => f.key))
  const issueList = issues ? Object.entries(issues) : []

  return (
    <section aria-labelledby="step-title" className="sub__step">
      <h2 id="step-title" className="sub__step-title">
        Önizləmə
      </h2>
      <p className="lede">Hər şeyi yoxlayın. Təqdim etdikdən sonra təklifi dəyişmək mümkün olmayacaq.</p>

      {issueList.length > 0 && (
        <div className="alert" role="alert">
          <strong>Təqdim etməzdən əvvəl bunları düzəldin:</strong>
          <ul className="sub__issues">
            {issueList.map(([key, messages]) => (
              <li key={key}>
                {messages[0]}{' '}
                <button
                  type="button"
                  className="linkbtn"
                  onClick={() => onEdit(fileKeys.has(key) ? 'files' : 'details')}
                >
                  Düzəliş et
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
      {error && (
        <div className="alert" role="alert">
          {error}
        </div>
      )}

      <SubmissionSummary submission={sub} />

      <div className="sub__actions sub__actions--final">
        <p className="sub__save">Təqdimdən sonra sizə istinad nömrəsi veriləcək.</p>
        <div className="sub__buttons">
          <button type="button" className="btn btn--lg" onClick={() => onEdit('details')}>
            Geri qayıt və düzəliş et
          </button>
          <button
            type="button"
            className="btn btn--primary btn--lg"
            onClick={submit}
            disabled={checking || submitting || issueList.length > 0}
            aria-busy={submitting}
          >
            {submitting && <span className="btn__spinner" aria-hidden="true" />}
            Təklifi təqdim et
          </button>
        </div>
      </div>
    </section>
  )
}

// ---- Receipt / read-only view ------------------------------------------------------------------

function Receipt({ submission: s, justSubmitted }: { submission: Submission; justSubmitted: boolean }) {
  return (
    <div className="container sub" style={accentVars(s.project.accentColor)}>
      <ContextBar s={s} />

      {justSubmitted ? (
        <section className="receipt" aria-labelledby="receipt-title" role="status">
          <span className="receipt__mark" aria-hidden="true">
            <Check size={30} />
          </span>
          <h1 id="receipt-title" className="receipt__title">
            Təklifiniz qəbul edildi.
          </h1>
          <ul role="list" className="receipt__context">
            <li>{s.project.name}</li>
            <li>{s.competition.title}</li>
            {s.category && <li className="receipt__category">{s.category.name}</li>}
          </ul>
          <div className="receipt__ref">
            <span className="receipt__ref-label">İstinad nömrəsi</span>
            <strong className="receipt__ref-value tabular">{s.referenceNumber}</strong>
          </div>
          <p className="receipt__note">Təklifinizi bu nömrə ilə “İşlərim” bölməsində tapa bilərsiniz.</p>
          <div className="receipt__actions">
            <Link to="/my/submissions" className="btn btn--primary btn--lg">
              İşlərimə keç
            </Link>
            <Link to={`/competitions/${s.competition.slug}`} className="btn btn--lg">
              Müsabiqəyə qayıt
            </Link>
          </div>
        </section>
      ) : (
        <header className="sub__head sub__head--receipt">
          <p className="sub__ref tabular">Təklif #{s.referenceNumber}</p>
          <h1>{s.title || 'Adsız təklif'}</h1>
          <p className="sub__meta">
            <span className={`subs__status subs__status--${s.status.toLowerCase()}`}>{submissionStatusLabel[s.status]}</span>
            {s.submittedAt && <span>Göndərilib: {formatDateTime(s.submittedAt)}</span>}
          </p>
        </header>
      )}

      <section className="sub__readonly" aria-labelledby="sent-title">
        <h2 id="sent-title" className="sub__step-title">
          Göndərdiyiniz məlumatlar
        </h2>
        <SubmissionSummary submission={s} />
        {!justSubmitted && (
          <Link to="/my/submissions" className="btn">
            İşlərimə qayıt
          </Link>
        )}
      </section>
    </div>
  )
}
