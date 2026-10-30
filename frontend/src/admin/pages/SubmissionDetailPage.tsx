import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { submissionsApi } from '../../api/endpoints'
import type { SubmissionStatus } from '../../api/types'
import { PageError, PageLoading } from '../../components/PageState'
import { AuthedImage } from '../../components/submission/AuthedImage'
import { WinnerBadge } from '../../components/WinnerBadge'
import { formatBytes, formatDateTime } from '../../lib/format'
import { placeName, singularAz as singular } from '../../lib/text'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { adminApi, type AdminSubmissionDetail } from '../api'
import { ConfirmDialog } from '../ConfirmDialog'
import { PdfPagePicker } from '../PdfPagePicker'
import { PermanentDelete } from '../PermanentDelete'
import { AField } from '../forms'
import { readErrors } from '../errors'
import { submissionStatusAdmin } from '../labels'
import { DemoBadge, Notice, PageHeader, SubmissionStatusBadge } from '../ui'

export default function SubmissionDetailPage() {
  const { id = '' } = useParams()
  const state = useApi((signal) => adminApi.submission(Number(id), signal), [id])
  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  return <Detail key={id} initial={state.data} />
}

/** Opens a stored file in a new tab from an authorised blob (the server serves it only to admins/owners). */
async function openFile(submissionId: number, fileId: number, download?: string) {
  const blob = await submissionsApi.fileBlob(submissionId, fileId)
  const url = URL.createObjectURL(blob)
  if (download) {
    const a = document.createElement('a')
    a.href = url
    a.download = download
    a.click()
  } else {
    window.open(url, '_blank', 'noopener')
  }
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

function Detail({ initial }: { initial: AdminSubmissionDetail }) {
  const navigate = useNavigate()
  const [s, setS] = useState(initial)
  useTitle(`${s.referenceNumber} — idarəetmə`)

  return (
    <>
      <PageHeader
        crumbs={[{ label: 'İdarəetmə', to: '/admin' }, { label: 'Təkliflər', to: '/admin/submissions' }, { label: s.referenceNumber ?? '' }]}
        title={s.title || 'Adsız təklif'}
        meta={
          <>
            <span className="tabular" style={{ fontWeight: 700 }}>
              {s.referenceNumber}
            </span>
            <SubmissionStatusBadge status={s.status} />
            {(s.result.isWinner || s.result.place) && <WinnerBadge size="sm" label={s.result.place ? placeName(s.result.place) : undefined} />}
            {s.isDemo && <DemoBadge />}
            <span className="tabular">Göndərilib: {formatDateTime(s.submittedAt)}</span>
          </>
        }
      />

      <div className="a-split a-split--wide-aside">
        <div className="a-form" style={{ gap: 'var(--s-5)' }}>
          <section className="a-panel" aria-labelledby="ctx-title">
            <h2 id="ctx-title" className="a-panel__title">
              Kontekst
            </h2>
            <dl className="a-kv">
              <div>
                <dt>Layihə</dt>
                <dd>
                  <Link to={`/admin/projects/${s.project.id}`}>{s.project.name}</Link>
                </dd>
              </div>
              <div>
                <dt>Müsabiqə</dt>
                <dd>
                  <Link to={`/admin/competitions/${s.competition.id}`}>{s.competition.name}</Link>
                </dd>
              </div>
              {s.category && (
                <div>
                  <dt>{s.categoryLabel ? singular(s.categoryLabel) : 'Kateqoriya'}</dt>
                  <dd>
                    <Link to={`/admin/submissions?competitionId=${s.competition.id}&categoryId=${s.category.id}`}>{s.category.name}</Link>
                  </dd>
                </div>
              )}
              <div>
                <dt>Yaradılıb</dt>
                <dd className="tabular">{formatDateTime(s.createdAt)}</dd>
              </div>
              <div>
                <dt>Yenilənib</dt>
                <dd className="tabular">{formatDateTime(s.updatedAt)}</dd>
              </div>
            </dl>
          </section>

          <section className="a-panel" aria-labelledby="answers-title">
            <h2 id="answers-title" className="a-panel__title">
              Cavablar
            </h2>
            <dl className="a-answers">
              {s.answers.map((a) => (
                <div key={a.fieldId} className="a-answer">
                  <dt>
                    {a.label}
                    {a.label !== a.currentLabel && <span className="a-field__hint"> (indiki adı: {a.currentLabel})</span>}
                    {!a.fieldActive && <span className="a-field__hint"> — sahə deaktivdir</span>}
                  </dt>
                  <dd>{a.value ?? <span className="a-muted">Doldurulmayıb</span>}</dd>
                </div>
              ))}
            </dl>
          </section>

          <FilesPanel s={s} onChange={setS} />
        </div>

        <div className="a-form a-split__aside" style={{ gap: 'var(--s-5)' }}>
          <WinnerPanel s={s} onChange={setS} />
          <StatusPanel s={s} onChange={setS} />

          <section className="a-panel" aria-labelledby="person-title">
            <h2 id="person-title" className="a-panel__title">
              İştirakçı
            </h2>
            <dl className="a-kv">
              <div>
                <dt>Ad</dt>
                <dd>
                  {s.participant.firstName} {s.participant.lastName}
                </dd>
              </div>
              <div>
                <dt>E-mail</dt>
                <dd>{s.participant.email ? <a href={`mailto:${s.participant.email}`}>{s.participant.email}</a> : '—'}</dd>
              </div>
              <div>
                <dt>Telefon</dt>
                <dd>{s.participant.phone ?? '—'}</dd>
              </div>
              <div>
                <dt>Qeydiyyat</dt>
                <dd className="tabular">{formatDateTime(s.participant.registeredAt)}</dd>
              </div>
            </dl>
          </section>

          <NotesPanel s={s} onChange={setS} />

          <section className="a-panel" aria-labelledby="hist-title">
            <h2 id="hist-title" className="a-panel__title">
              Status tarixçəsi
            </h2>
            <ol role="list" className="a-timeline">
              {[...s.history].reverse().map((h, i) => (
                <li key={i}>
                  <span className="a-timeline__what">
                    {submissionStatusAdmin[h.from]} → {submissionStatusAdmin[h.to]}
                  </span>
                  {h.comment && <span>{h.comment}</span>}
                  <span className="a-timeline__who">
                    {h.changedBy ?? 'Sistem'}, <span className="tabular">{formatDateTime(h.changedAt)}</span>
                  </span>
                </li>
              ))}
            </ol>
          </section>

          <section className="a-panel" aria-labelledby="delete-title">
            <h2 id="delete-title" className="a-panel__title">
              Silmək
            </h2>
            <PermanentDelete
              noun="təklifi"
              loadImpact={() => adminApi.submissionDeleteImpact(s.id)}
              onDelete={() => adminApi.deleteSubmissionPermanently(s.id)}
              onDeleted={() => navigate('/admin/submissions', { replace: true, state: { deleted: true } })}
            />
          </section>
        </div>
      </div>
    </>
  )
}

function StatusPanel({ s, onChange }: { s: AdminSubmissionDetail; onChange: (d: AdminSubmissionDetail) => void }) {
  const [status, setStatus] = useState<SubmissionStatus | ''>('')
  const [comment, setComment] = useState('')
  const [message, setMessage] = useState<{ tone: 'error' | 'success'; text: string }>()
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!status) return setMessage({ tone: 'error', text: 'Yeni statusu seçin.' })
    setBusy(true)
    try {
      onChange(await adminApi.submissionStatus(s.id, status, comment))
      setMessage({ tone: 'success', text: `Status dəyişdi: ${submissionStatusAdmin[status]}.` })
      setStatus('')
      setComment('')
    } catch (err) {
      setMessage({ tone: 'error', text: readErrors(err, 'Status dəyişmədi.').message })
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="a-panel a-form" onSubmit={submit} noValidate aria-labelledby="st-title">
      <div className="a-panel__head">
        <h2 id="st-title" className="a-panel__title">
          Status
        </h2>
        <SubmissionStatusBadge status={s.status} />
      </div>
      {message && <Notice tone={message.tone}>{message.text}</Notice>}
      <AField label="Yeni status">
        <select className="input" value={status} onChange={(e) => setStatus(e.target.value as SubmissionStatus)}>
          <option value="">Seçin</option>
          {s.allowedStatuses.map((x) => (
            <option key={x} value={x}>
              {submissionStatusAdmin[x]}
            </option>
          ))}
        </select>
      </AField>
      <AField label="Şərh (istəyə bağlı)" hint="Tarixçədə görünür. İştirakçıya göstərilmir.">
        <textarea className="input" rows={2} value={comment} onChange={(e) => setComment(e.target.value)} maxLength={1000} />
      </AField>
      <div className="a-actions">
        <button type="submit" className="btn btn--primary btn--sm" disabled={busy || !status}>
          Statusu dəyiş
        </button>
      </div>
    </form>
  )
}

function NotesPanel({ s, onChange }: { s: AdminSubmissionDetail; onChange: (d: AdminSubmissionDetail) => void }) {
  const [text, setText] = useState('')
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!text.trim()) return setError('Qeydin mətnini yazın.')
    setBusy(true)
    try {
      onChange(await adminApi.addNote(s.id, text))
      setText('')
      setError(undefined)
    } catch (err) {
      setError(readErrors(err, 'Qeyd əlavə olunmadı.').message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="a-panel" aria-labelledby="notes-title">
      <h2 id="notes-title" className="a-panel__title">
        Daxili qeydlər
      </h2>
      <p className="a-muted">Yalnız idarəetmə üçündür — iştirakçı görmür.</p>
      <form className="a-form" onSubmit={submit} noValidate>
        <AField label="Yeni qeyd" error={error}>
          <textarea
            className="input"
            rows={3}
            value={text}
            onChange={(e) => setText(e.target.value)}
            maxLength={4000}
            placeholder="məs. PDF keyfiyyəti yoxlanmalıdır."
          />
        </AField>
        <div>
          <button type="submit" className="btn btn--sm" disabled={busy}>
            Qeydi əlavə et
          </button>
        </div>
      </form>
      {s.notes.length > 0 && (
        <ul role="list" className="a-notes">
          {s.notes.map((n) => (
            <li key={n.id} className="a-note">
              {n.text}
              <span className="a-note__meta">
                {n.authorName}, <span className="tabular">{formatDateTime(n.createdAt)}</span>
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

/**
 * The competition result, deliberately separate from the moderation status: choosing a winner is its own action with
 * its own confirmation, never a value in the status list. A work can only win the category (settlement) it was
 * entered in; choosing does not publish anything, and once results are public every change asks for confirmation.
 */
function WinnerPanel({ s, onChange }: { s: AdminSubmissionDetail; onChange: (d: AdminSubmissionDetail) => void }) {
  const r = s.result
  const [dialog, setDialog] = useState<'select' | 'change' | 'remove' | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [message, setMessage] = useState<{ tone: 'error' | 'success'; text: string }>()
  const resultsLink = `/admin/competitions/${s.competition.id}?tab=results`
  const place = r.category?.name ?? null
  const slot = place ? `${place} üzrə` : 'Müsabiqə üzrə'
  const published = r.resultPublished

  const confirm = async () => {
    setBusy(true)
    setError(undefined)
    try {
      if (dialog === 'change' && r.currentWinner)
        await adminApi.changeWinner(s.competition.id, s.id, r.currentWinner.submissionId, published)
      else if (dialog === 'remove') await adminApi.removeWinner(s.competition.id, s.id, published)
      else await adminApi.selectWinner(s.competition.id, s.id, published)
      onChange(await adminApi.submission(s.id))
      const done = dialog === 'change' ? 'Qalib dəyişdirildi.' : dialog === 'remove' ? 'Qalib ləğv edildi.' : 'Qalib seçildi.'
      setMessage({
        tone: 'success',
        text: `${done}${r.resultPublicNow ? ' Dəyişiklik saytda görünür.' : published ? ' Nəticələr yenidən görünəndə saytda olacaq.' : ' Nəticə hələ dərc olunmayıb.'}`,
      })
      setDialog(null)
    } catch (err) {
      const text = readErrors(err, 'Alınmadı.').message
      if (err instanceof ApiError && err.status === 409) {
        // Someone chose, changed or published meanwhile: show the real state instead of overwriting it.
        onChange(await adminApi.submission(s.id))
        setMessage({ tone: 'error', text })
        setDialog(null)
      } else {
        setError(text)
      }
    } finally {
      setBusy(false)
    }
  }

  const facts = (
    <dl className="a-kv a-kv--dialog">
      <div>
        <dt>İstinad</dt>
        <dd className="tabular">{s.referenceNumber}</dd>
      </div>
      <div>
        <dt>İşin adı</dt>
        <dd>{s.title || 'Adsız təklif'}</dd>
      </div>
      {s.category && (
        <div>
          <dt>{s.categoryLabel ? singular(s.categoryLabel) : 'Kateqoriya'}</dt>
          <dd>{s.category.name}</dd>
        </div>
      )}
      <div>
        <dt>İştirakçı</dt>
        <dd>
          {s.participant.firstName} {s.participant.lastName}
        </dd>
      </div>
    </dl>
  )
  const publicWarning = published && (
    <Notice tone="warn">
      {r.resultPublicNow
        ? 'Nəticələr artıq dərc olunub: bu dəyişiklik saytda dərhal görünəcək.'
        : 'Nəticələr dərc olunub (hazırda saytda görünmür): dəyişiklik nəticələr yenidən görünəndə saytda olacaq.'}
    </Notice>
  )

  return (
    <section className="a-panel a-winner-panel" aria-labelledby="win-title">
      <div className="a-panel__head">
        <h2 id="win-title" className="a-panel__title">
          Müsabiqə nəticəsi
        </h2>
        {(r.isWinner || r.place) && <WinnerBadge size="sm" label={r.place ? placeName(r.place) : undefined} />}
      </div>
      {place && (
        <dl className="a-kv">
          <div>
            <dt>{s.categoryLabel ? singular(s.categoryLabel) : 'Kateqoriya'}</dt>
            <dd>{place}</dd>
          </div>
        </dl>
      )}
      {message && <Notice tone={message.tone}>{message.text}</Notice>}

      {r.place && r.place > 1 ? (
        // Second and third place are changed together with the first, on the results tab — never one by one here.
        <>
          <p>
            <strong>Bu iş {placeName(r.place)} tutur.</strong>
          </p>
          <p className="a-muted">Yerlər «Nəticələr» bölməsində birlikdə dəyişdirilir.</p>
          <div className="a-actions">
            <Link to={resultsLink} className="btn btn--sm">
              Nəticələr bölməsi
            </Link>
          </div>
        </>
      ) : r.isWinner ? (
        <>
          <p>
            <strong>Bu iş {slot} qalibdir{r.place ? ` (${placeName(r.place)})` : ''}.</strong>
          </p>
          <p className="a-muted">
            {r.resultPublicNow
              ? 'Nəticələr saytda görünür: qalibin adı ictimaidir.'
              : published
                ? 'Nəticələr dərc olunub, amma hazırda saytda görünmür (müsabiqə yenidən qəbul açıb və ya saytdan götürülüb).'
                : 'Nəticələr hələ dərc olunmayıb — saytda heç kim görmür.'}
          </p>
          <div className="a-actions">
            <Link to={resultsLink} className="btn btn--sm">
              Nəticələr bölməsi
            </Link>
            <button type="button" className="btn btn--sm btn--ghost a-btn--danger" onClick={() => setDialog('remove')}>
              Qalibi ləğv et
            </button>
          </div>
        </>
      ) : r.currentWinner ? (
        <>
          <Notice tone="warn">
            {slot} qalib artıq seçilib:{' '}
            <Link to={`/admin/submissions/${r.currentWinner.submissionId}`} className="tabular">
              {r.currentWinner.reference}
            </Link>
            {r.currentWinner.title && ` — ${r.currentWinner.title}`} ({r.currentWinner.participantName})
          </Notice>
          {r.canBeWinner ? (
            <div className="a-actions">
              <button type="button" className="btn btn--sm" onClick={() => setDialog('change')}>
                Qalibi dəyiş
              </button>
            </div>
          ) : (
            <p className="a-muted">{r.ineligibleReason}</p>
          )}
        </>
      ) : r.canBeWinner ? (
        <>
          <p className="a-muted">
            {place ? `Hər ${singular(s.categoryLabel ?? 'Kateqoriyalar').toLocaleLowerCase('az')} üçün bir qalib seçilir.` : 'Müsabiqə üzrə bir qalib seçilir.'}{' '}
            Seçim dərhal ictimai olmur — nəticə ayrıca dərc edilir.
          </p>
          <div className="a-actions">
            <button type="button" className="btn btn--primary btn--sm" onClick={() => setDialog('select')}>
              {slot} qalib seç
            </button>
          </div>
        </>
      ) : (
        <p className="a-muted">{r.ineligibleReason}</p>
      )}

      {dialog === 'select' && (
        <ConfirmDialog
          title={`Bu işi ${place ? `${place} üzrə` : 'müsabiqənin'} qalibi seçmək istəyirsiniz?`}
          confirmLabel="Bəli, qalib seç"
          onConfirm={confirm}
          onCancel={() => setDialog(null)}
          busy={busy}
          error={error}
        >
          {publicWarning}
          {facts}
          {!published && <p className="a-muted">Nəticə saytda yalnız siz onu «Nəticələr» bölməsindən dərc edəndə görünəcək.</p>}
        </ConfirmDialog>
      )}
      {dialog === 'change' && r.currentWinner && (
        <ConfirmDialog
          title={`${slot} qalibi dəyişmək istəyirsiniz?`}
          confirmLabel="Bəli, qalibi dəyiş"
          onConfirm={confirm}
          onCancel={() => setDialog(null)}
          busy={busy}
          error={error}
        >
          {publicWarning}
          <p>
            Hazırkı qalib: <strong className="tabular">{r.currentWinner.reference}</strong>
            {r.currentWinner.title && ` — ${r.currentWinner.title}`} ({r.currentWinner.participantName})
          </p>
          <p>Yeni qalib:</p>
          {facts}
          <p className="a-muted">Dəyişiklik kim və nə vaxt etdiyi ilə birlikdə tarixçədə qeyd olunur.</p>
        </ConfirmDialog>
      )}
      {dialog === 'remove' && (
        <ConfirmDialog
          title={`${slot} qalibi ləğv etmək istəyirsiniz?`}
          confirmLabel="Bəli, ləğv et"
          onConfirm={confirm}
          onCancel={() => setDialog(null)}
          busy={busy}
          error={error}
        >
          {publicWarning}
          {facts}
          <p className="a-muted">İşin özü və statusu dəyişmir; {place ?? 'müsabiqə'} sadəcə qalibsiz qalır.</p>
        </ConfirmDialog>
      )}
    </section>
  )
}

const assetErrors: Record<string, string> = {
  'too-large': 'şəkil çox böyükdür',
  unreadable: 'fayl oxunmadı',
  'decode-failed': 'fayl zədələnib',
  'pdf-unreadable': 'PDF oxunmadı',
  'encode-failed': 'kodlaşdırma alınmadı',
}

/** Opens the metadata-free public copy (what the gallery will serve) in a new tab. */
async function openPublicCopy(submissionId: number, fileId: number) {
  const blob = await adminApi.publicAssetBlob(submissionId, fileId)
  const url = URL.createObjectURL(blob)
  window.open(url, '_blank', 'noopener')
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

/**
 * Originals (private, as uploaded) with their public status: whether the field is shown after results, the state
 * of the metadata-free copy the gallery uses, and which image illustrates the work in the gallery.
 *
 * A single file can be taken off every public page ("Saytda gizlət") — a picture that names its author during an
 * anonymous vote — while the work keeps its place and its other files. Nothing is deleted; it can be put back.
 */
function FilesPanel({ s, onChange }: { s: AdminSubmissionDetail; onChange: (d: AdminSubmissionDetail) => void }) {
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [hiding, setHiding] = useState<AdminSubmissionDetail['files'][number]>()
  const [picking, setPicking] = useState<AdminSubmissionDetail['files'][number]>()
  const unscanned = s.files.some((f) => f.scanStatus === 'NotScanned')

  const run = async (action: () => Promise<AdminSubmissionDetail>, failure: string) => {
    setBusy(true)
    setError(undefined)
    try {
      onChange(await action())
      return true
    } catch (err) {
      setError(readErrors(err, failure).message)
      return false
    } finally {
      setBusy(false)
    }
  }
  const choose = (fileId: number | null) => run(() => adminApi.setPreview(s.id, fileId), 'Önizləmə dəyişmədi.')
  const setHidden = (fileId: number, hidden: boolean) =>
    run(() => adminApi.setFileHidden(s.id, fileId, hidden), hidden ? 'Fayl gizlədilmədi.' : 'Fayl göstərilmədi.')
  const setPreviewPage = (fileId: number, page: number | null) =>
    run(() => adminApi.setPdfPreviewPage(s.id, fileId, page), 'Önizləmə səhifəsi dəyişmədi.')

  return (
    <section className="a-panel" aria-labelledby="files-title">
      <h2 id="files-title" className="a-panel__title">
        Fayllar <span className="a-muted tabular">({s.files.length})</span>
      </h2>
      <p className="a-muted">
        Orijinal fayllar gizlidir və dəyişdirilmir. Qalereyada yalnız serverin hazırladığı təhlükəsiz nüsxə (metadata, GPS və
        müəllif məlumatı olmadan) göstərilir.
      </p>
      {error && <Notice tone="error">{error}</Notice>}
      {s.files.length === 0 ? (
        <p className="a-muted">Fayl yoxdur.</p>
      ) : (
        <ul role="list" className="a-files">
          {s.files.map((f) => {
            const image = f.contentType.startsWith('image/')
            const isPreview = s.effectivePreviewFileId === f.id
            // A PDF is pictured by the page that stands for it — the admin's choice, else the automatic one.
            const pdfPages = f.pdfPages ?? 0
            const page = pdfPages > 0 ? (f.representativePage ?? 1) : 1
            return (
              <li key={f.id} className={`a-file${isPreview ? ' is-preview' : ''}${f.hiddenFromPublic ? ' is-hidden' : ''}`}>
                <button
                  type="button"
                  className="a-file__visual"
                  onClick={() => openFile(s.id, f.id)}
                  aria-label={`${f.originalFileName} — orijinalı yeni vərəqdə aç`}
                >
                  {f.thumbnailReady || page > 1 ? (
                    // The small server-made copy (a PDF: the page that stands for it) — never the multi-megabyte original.
                    <AuthedImage submissionId={s.id} fileId={f.id} page={page} alt="" variant="thumb" className="a-art" />
                  ) : image ? (
                    <span className="a-art a-art--none">{f.publicAsset === 'Failed' ? 'Önizləmə yoxdur' : 'Önizləmə hazırlanır'}</span>
                  ) : (
                    <span className="thumb thumb--doc" aria-hidden="true">
                      PDF
                    </span>
                  )}
                  {isPreview && <span className="a-file__flag">Önizləmə şəkli</span>}
                  {f.hiddenFromPublic && <span className="a-file__flag a-file__flag--hidden">Saytda gizlədilib</span>}
                </button>
                <span className="a-file__name" title={f.originalFileName}>
                  {f.originalFileName}
                </span>
                <span className="a-muted">
                  {formatBytes(f.sizeBytes)}
                  {f.fieldLabel && `, ${f.fieldLabel}`}
                </span>
                {pdfPages > 0 && (
                  <span className="a-file__page">
                    <span className="tabular">
                      PDF · {pdfPages} səhifə · Əsas önizləmə: səhifə {page}
                    </span>
                    <span className="a-muted">{f.previewPage == null ? ' (avtomatik)' : ' (admin seçib)'}</span>
                  </span>
                )}
                <span className="a-file__public">
                  {f.hiddenFromPublic ? (
                    <span className="a-state a-state--bad">Saytda gizlədilib: səsvermədə, iş səhifəsində və qalereyada görünmür</span>
                  ) : !f.isPublicField ? (
                    <span className="a-muted">Gizli: sahə nəticədən sonra göstərilmir</span>
                  ) : f.publicAsset === 'Ready' ? (
                    <>
                      <span className="a-state a-state--ok">Təhlükəsiz nüsxə hazırdır</span>
                      <button type="button" className="a-link" onClick={() => openPublicCopy(s.id, f.id)}>
                        Təhlükəsiz nüsxəyə bax
                      </button>
                    </>
                  ) : f.publicAsset === 'Pending' ? (
                    <span className="a-state">Təhlükəsiz nüsxə hazırlanır…</span>
                  ) : (
                    <span className="a-state a-state--bad">
                      Təhlükəsiz nüsxə yaradılmadı ({assetErrors[f.publicAssetError ?? ''] ?? 'xəta'}). Qalereyada yer tutucu göstəriləcək.
                    </span>
                  )}
                </span>
                <span className="a-inline">
                  <button type="button" className="btn btn--sm btn--ghost" onClick={() => openFile(s.id, f.id)}>
                    Aç
                  </button>
                  <button type="button" className="btn btn--sm btn--ghost" onClick={() => openFile(s.id, f.id, f.originalFileName)}>
                    Yüklə
                  </button>
                  {pdfPages > 0 && (
                    <button type="button" className="btn btn--sm" disabled={busy} onClick={() => setPicking(f)}>
                      Önizləmə səhifəsini seç
                    </button>
                  )}
                  {f.canBePreview && !isPreview && (
                    <button type="button" className="btn btn--sm" disabled={busy} onClick={() => choose(f.id)}>
                      Önizləmə et
                    </button>
                  )}
                  {f.isPublicField &&
                    (f.hiddenFromPublic ? (
                      <button type="button" className="btn btn--sm btn--ghost" disabled={busy} onClick={() => setHidden(f.id, false)}>
                        Saytda göstər
                      </button>
                    ) : (
                      <button type="button" className="btn btn--sm btn--ghost a-btn--danger" disabled={busy} onClick={() => setHiding(f)}>
                        Saytda gizlət
                      </button>
                    ))}
                </span>
              </li>
            )
          })}
        </ul>
      )}
      {s.previewFileId !== null && (
        <div>
          <button type="button" className="btn btn--sm btn--ghost" disabled={busy} onClick={() => choose(null)}>
            Önizləməni avtomatik seçimə qaytar
          </button>
        </div>
      )}
      {unscanned && <p className="a-muted">Antivirus yoxlaması qoşulmayıb: fayllar format və məzmun yoxlamasından keçib.</p>}
      {picking && (
        <PdfPagePicker
          submissionId={s.id}
          // The file as it is now (it changes after a save): its choice and page count, fresh.
          file={s.files.find((f) => f.id === picking.id) ?? picking}
          busy={busy}
          error={error}
          onCancel={() => {
            setPicking(undefined)
            setError(undefined)
          }}
          onSave={async (page) => {
            if (await setPreviewPage(picking.id, page)) setPicking(undefined)
          }}
        />
      )}
      {hiding && (
        <ConfirmDialog
          title="Bu fayl saytda gizlədilsin?"
          confirmLabel="Bəli, gizlət"
          busy={busy}
          error={error}
          onCancel={() => setHiding(undefined)}
          onConfirm={async () => {
            if (await setHidden(hiding.id, true)) setHiding(undefined)
          }}
        >
          <p>
            <strong>{hiding.originalFileName}</strong> səsvermə siyahısında, işin səhifəsində və nəticələrdən sonra qalereyada
            görünməyəcək. İş müsabiqədə qalır, digər faylları görünməyə davam edir.
          </p>
          <p className="a-muted">
            Fayl silinmir — istənilən vaxt «Saytda göstər» ilə qaytara bilərsiniz.
            {s.effectivePreviewFileId === hiding.id && ' Bu şəkil hazırda önizləmədir: önizləmə avtomatik olaraq növbəti faylla əvəz olunacaq.'}
          </p>
        </ConfirmDialog>
      )}
    </section>
  )
}
