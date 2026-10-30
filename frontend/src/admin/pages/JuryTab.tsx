import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDateTime } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { catalogNumber } from '../../pages/voting/lib'
import type { AdminCompetition } from '../api'
import { ConfirmDialog } from '../ConfirmDialog'
import { readErrors } from '../errors'
import {
  invitationUrl,
  juryAdminApi,
  juryPrintHref,
  type AdminJurorProgress,
  type AdminJuryEvaluation,
  type InvitationState,
  type IssuedInvitation,
  type JuryRanking,
  type PaperLine,
  type PaperPreview,
} from '../juryApi'
import { auditActionLabel } from '../labels'
import { Badge, Notice } from '../ui'
import { WorkViewer } from '../WorkViewer'
import './final.css'
import './jury-admin.css'

/**
 * The jury's evaluation of every eligible work and its internal ranking (InternalJuryRankingV1): start it, freeze the
 * roster, invite the jurors, follow their progress, enter paper sheets — and, once every juror has submitted, read
 * every work ranked by its exact average, with ties shown as ties, and export it as CSV or PDF.
 *
 * Internal only: it publishes nothing, awards no prize and never touches the public vote or its results.
 */

/** 8.6667 → "8,67": the Azerbaijani decimal comma. */
const average = (n: number | null, places = 2) => (n === null ? '—' : n.toFixed(places).replace('.', ','))
export function JuryTab({ competition }: { competition: AdminCompetition }) {
  const state = useApi((signal) => juryAdminApi.get(competition.id, signal), [competition.id])
  const [e, setE] = useState<AdminJuryEvaluation>()
  const [version, setVersion] = useState(0)
  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  const ev = e ?? state.data
  const changed = (next: AdminJuryEvaluation) => {
    setE(next)
    setVersion((v) => v + 1)
  }

  return (
    <div className="a-form fin jy" style={{ gap: 'var(--s-5)' }}>
      <Steps e={ev} />
      {!ev.exists ? (
        <SwitchPanel e={ev} onChange={changed} />
      ) : (
        <>
          <OverviewPanel e={ev} onChange={changed} />
          {ev.status !== 'Draft' && (
            <>
              <JurorsPanel e={ev} onChange={changed} />
              <PrintPanel e={ev} />
              <MatrixPanel e={ev} refreshKey={version} />
              <PaperPanel e={ev} onChange={changed} refreshKey={version} />
              <RankingPanel e={ev} refreshKey={version} />
            </>
          )}
          <AuditPanel competitionId={ev.competitionId} refreshKey={version} />
        </>
      )}
    </div>
  )
}

const STEPS = [
  'Qiymətləndirməni başlat',
  'İşlərin siyahısını dondur',
  'Münsifləri dəvət et',
  'Qiymətləndirmə',
  'Daxili reytinq',
] as const

function Steps({ e }: { e: AdminJuryEvaluation }) {
  const submitted = e.jurors.filter((j) => j.status === 'Submitted').length
  const invited = e.jurors.some((j) => j.invitation.state !== 'None')
  const at = !e.exists
    ? 0
    : e.status === 'Draft'
      ? 1
      : submitted === e.jurorCount && e.jurorCount > 0
          ? 4
          : invited || e.completedScores > 0
            ? 3
            : 2
  return (
    <ol className="fin-steps" aria-label="Münsif qiymətləndirməsinin addımları">
      {STEPS.map((s, i) => (
        <li key={s} className={i < at ? 'is-done' : i === at ? 'is-current' : undefined} aria-current={i === at ? 'step' : undefined}>
          <span className="fin-steps__n tabular">{i + 1}</span>
          {s}
        </li>
      ))}
    </ol>
  )
}

// ---- 1. Switching the format ------------------------------------------------------------------------------------

function SwitchPanel({ e, onChange }: { e: AdminJuryEvaluation; onChange: (e: AdminJuryEvaluation) => void }) {
  const [dialog, setDialog] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const create = async () => {
    setBusy(true)
    setError(undefined)
    try {
      onChange(await juryAdminApi.create(e.competitionId))
      setDialog(false)
    } catch (err) {
      setError(readErrors(err, 'Qiymətləndirmə başlamadı.').message)
    } finally {
      setBusy(false)
    }
  }
  return (
    <section className="a-panel" aria-labelledby="jy-switch">
      <div className="a-panel__head">
        <h2 id="jy-switch" className="a-panel__title">
          Bütün işlərin daxili münsif qiymətləndirməsi
        </h2>
        <Badge tone="draft">Başlanmayıb</Badge>
      </div>
      <p className="a-muted">
        Münsiflər səsvermədə iştirak edən <strong className="tabular">{e.eligibleNow}</strong> işin hər birinə 1–10 tam bal
        verir. Hamı yekunlaşdırdıqdan sonra işlər dəqiq orta bala görə 1-dən {e.eligibleNow}-ə qədər daxili reytinqdə
        sıralanır.
      </p>
      <ul className="jy-list">
        <li>Reytinq yalnız adminlər üçündür: heç bir ictimai səhifədə görünmür, mükafat və pul mükafatı vermir.</li>
        <li>Xalq səsverməsi, onun nəticələri və dondurulmuş ictimai sıralama dəyişmir.</li>
        <li>Münsiflər Final bölməsindəki münsiflərdir (eyni adlar, yeni kopiya yaranmır).</li>
        <li>Köhnə 50/50 final tarixçə kimi qalır, amma artıq bal qəbul etmir, şou göstərmir və dərc etmir.</li>
      </ul>
      {!e.hasCombinedFinal && (
        <Notice tone="warn">Münsiflər Final bölməsində saxlanılır — əvvəlcə final yaradılmalıdır.</Notice>
      )}
      <p className="a-muted">
        Admin üçün: <Link to={juryPrintHref(e.competitionId, 'analytics')}>səs analitikası kataloqu</Link> — bütün işlər
        etibarlı səslər və audit qərarları ilə (A4, münsiflərə verilmir).
      </p>
      <div className="a-actions">
        <button type="button" className="btn btn--primary" disabled={!e.hasCombinedFinal} onClick={() => setDialog(true)}>
          Qiymətləndirməni başlat
        </button>
      </div>
      {dialog && (
        <ConfirmDialog
          title="Bütün işlərin qiymətləndirməsi başlasın?"
          confirmLabel="Başlat"
          onConfirm={create}
          onCancel={() => setDialog(false)}
          busy={busy}
          error={error}
        >
          <p>
            Münsiflər bütün işləri daxili qiymətləndirəcək (<strong>InternalJuryRankingV1</strong>). Köhnə 50/50 final bundan
            sonra bal, şou və dərc üçün bağlanır; qeydləri silinmir. İctimai nəticələrə toxunulmur.
          </p>
        </ConfirmDialog>
      )}
    </section>
  )
}

// ---- 2. Overview and the roster -------------------------------------------------------------------------------------

function OverviewPanel({ e, onChange }: { e: AdminJuryEvaluation; onChange: (e: AdminJuryEvaluation) => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [dialog, setDialog] = useState(false)
  const freeze = async () => {
    setBusy(true)
    setError(undefined)
    try {
      onChange(await juryAdminApi.freezeRoster(e.competitionId))
      setDialog(false)
    } catch (err) {
      setError(readErrors(err, 'Siyahı dondurulmadı.').message)
    } finally {
      setBusy(false)
    }
  }
  const percent = e.expectedScores === 0 ? 0 : Math.round((e.completedScores / e.expectedScores) * 100)
  const submitted = e.jurors.filter((j) => j.status === 'Submitted').length
  return (
    <section className="a-panel" aria-labelledby="jy-overview">
      <div className="a-panel__head">
        <h2 id="jy-overview" className="a-panel__title">
          Münsif qiymətləndirməsi — bütün işlər
        </h2>
        <StatusBadge e={e} />
      </div>
      <dl className="jy-stats">
        <div>
          <dt>İş</dt>
          <dd className="tabular">{e.status === 'Draft' ? e.eligibleNow : e.rosterSize}</dd>
        </div>
        <div>
          <dt>Münsif</dt>
          <dd className="tabular">{e.jurorCount}</dd>
        </div>
        <div>
          <dt>Bal verilib</dt>
          <dd className="tabular">
            {e.completedScores} / {e.expectedScores}
          </dd>
        </div>
        <div>
          <dt>Yekunlaşdırıb</dt>
          <dd className="tabular">
            {submitted} / {e.jurorCount}
          </dd>
        </div>
      </dl>
      {e.status !== 'Draft' && (
        <div className="jy-bar" role="progressbar" aria-valuemin={0} aria-valuemax={e.expectedScores} aria-valuenow={e.completedScores} aria-label="Ümumi irəliləyiş">
          <span style={{ width: `${percent}%` }} />
        </div>
      )}
      <p className="a-muted">
        Format: <code>{e.format}</code>
        {e.rosterFrozenAt && <> · siyahı donduruldu: {formatDateTime(e.rosterFrozenAt)}</>}
      </p>
      {e.rosterDiffers && (
        <Notice tone="warn">
          Siyahı dondurulduqdan sonra səsvermədəki işlər dəyişib (indi {e.eligibleNow} iş uyğundur, siyahıda {e.rosterSize}).
          Münsif siyahısı dəyişmir; bu, sahibin qərarı tələb edən məsələdir.
        </Notice>
      )}
      {e.status === 'Draft' && (
        <>
          <p>
            Səsvermədə iştirak edən <strong className="tabular">{e.eligibleNow}</strong> iş (status: təqdim edilib, baxışda
            və ya təsdiqlənib; nömrəsi və aktiv nominasiyası olan) nömrə sırası ilə münsif siyahısına daxil ediləcək.
          </p>
          <div className="a-actions">
            <button type="button" className="btn btn--primary" onClick={() => setDialog(true)} disabled={e.eligibleNow === 0}>
              İşlərin siyahısını dondur
            </button>
          </div>
        </>
      )}
      {e.status === 'Open' && !e.rosterLocked && (
        <div className="a-actions">
          <button type="button" className="btn" onClick={() => setDialog(true)}>
            Siyahını yenidən dondur
          </button>
          <span className="a-muted">Yalnız heç bir bal verilməyənə qədər mümkündür.</span>
        </div>
      )}
      {dialog && (
        <ConfirmDialog
          title="İşlərin siyahısı dondurulsun?"
          confirmLabel="Dondur"
          onConfirm={freeze}
          onCancel={() => setDialog(false)}
          busy={busy}
          error={error}
        >
          <p>
            Hazırda səsvermədə olan <strong className="tabular">{e.eligibleNow}</strong> iş münsiflərin qiymətləndirəcəyi
            siyahı olur. İlk bal verildikdən sonra siyahı dəyişmir.
          </p>
        </ConfirmDialog>
      )}
    </section>
  )
}

function StatusBadge({ e }: { e: AdminJuryEvaluation }) {
  if (e.status === 'Draft') return <Badge tone="draft">Siyahı dondurulmayıb</Badge>
  return <Badge tone="review">Qiymətləndirmə açıqdır</Badge>
}

// ---- 3. Jurors and invitations ------------------------------------------------------------------------------------

const invitationLabel: Record<InvitationState, string> = {
  None: 'Dəvət verilməyib',
  Active: 'Dəvət aktivdir',
  Expired: 'Dəvətin müddəti bitib',
  Revoked: 'Dəvət ləğv olunub',
  Locked: 'Kilidlənib (səhv kod)',
}

const jurorStatusLabel = { NotStarted: 'Başlamayıb', InProgress: 'Davam edir', Submitted: 'Yekunlaşdırıb' } as const

function JurorsPanel({ e, onChange }: { e: AdminJuryEvaluation; onChange: (e: AdminJuryEvaluation) => void }) {
  const [issued, setIssued] = useState<IssuedInvitation>()
  const [error, setError] = useState<string>()
  const [newName, setNewName] = useState('')
  const [busy, setBusy] = useState(false)

  const run = async (action: () => Promise<AdminJuryEvaluation>, fallback: string) => {
    setBusy(true)
    setError(undefined)
    try {
      onChange(await action())
      return true
    } catch (err) {
      setError(readErrors(err, fallback).fields.label ?? readErrors(err, fallback).message)
      return false
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="a-panel" aria-labelledby="jy-jurors">
      <div className="a-panel__head">
        <h2 id="jy-jurors" className="a-panel__title">
          Münsiflər və dəvətlər
        </h2>
      </div>
      <p className="a-muted">
        Hər münsifə şəxsi link və ayrıca 6 rəqəmli giriş kodu verilir. Linki və kodu fərqli yollarla göndərin. Yalnız
        hash-ləri saxlanılır: link və kod yalnız verildiyi an bir dəfə göstərilir. Yeni dəvət əvvəlkini və bütün açıq
        sessiyaları bağlayır.
      </p>
      <div className="fin-scroll">
        <table className="a-table jy-jurors">
          <thead>
            <tr>
              <th scope="col">Münsif</th>
              <th scope="col">İrəliləyiş</th>
              <th scope="col">Dəvət</th>
              <th scope="col">Son giriş</th>
              <th scope="col">
                <span className="a-muted">Əməliyyat</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {e.jurors.map((j) => (
              <JurorRow
                key={j.jurorId}
                e={e}
                j={j}
                busy={busy}
                onIssued={setIssued}
                onError={setError}
                run={run}
              />
            ))}
          </tbody>
        </table>
      </div>
      {!e.rosterLocked && (
        <form
          className="jy-inline"
          onSubmit={async (ev) => {
            ev.preventDefault()
            if (await run(() => juryAdminApi.addJuror(e.competitionId, newName), 'Münsif əlavə olunmadı.')) setNewName('')
          }}
        >
          <label className="a-field">
            <span className="a-field__label">Yeni münsifin adı</span>
            <input value={newName} maxLength={60} onChange={(ev) => setNewName(ev.target.value)} />
          </label>
          <button type="submit" className="btn" disabled={busy || newName.trim().length === 0}>
            Münsif əlavə et
          </button>
        </form>
      )}
      {e.rosterLocked && <p className="a-muted">Qiymətləndirmə başlayıb: münsif tərkibi artıq dəyişmir (adlar dəyişə bilər).</p>}
      {error && <Notice tone="error">{error}</Notice>}
      {issued && <IssuedDialog issued={issued} onClose={() => setIssued(undefined)} />}
    </section>
  )
}

function JurorRow({
  e,
  j,
  busy,
  onIssued,
  onError,
  run,
}: {
  e: AdminJuryEvaluation
  j: AdminJurorProgress
  busy: boolean
  onIssued: (i: IssuedInvitation) => void
  onError: (m: string | undefined) => void
  run: (action: () => Promise<AdminJuryEvaluation>, fallback: string) => Promise<boolean>
}) {
  const [renaming, setRenaming] = useState(false)
  const [name, setName] = useState(j.label)
  const [reopening, setReopening] = useState(false)
  const [reason, setReason] = useState('')
  const [locking, setLocking] = useState(false)
  const [inviting, setInviting] = useState(false)
  const [days, setDays] = useState(14)
  const [dialogError, setDialogError] = useState<string>()
  const [dialogBusy, setDialogBusy] = useState(false)
  const total = e.rosterSize
  const percent = total === 0 ? 0 : Math.round((j.scored / total) * 100)

  const issue = async () => {
    setDialogBusy(true)
    setDialogError(undefined)
    try {
      onIssued(await juryAdminApi.issueInvitation(e.competitionId, j.jurorId, days))
      setInviting(false)
      onError(undefined)
      await run(() => juryAdminApi.get(e.competitionId), '')
    } catch (err) {
      setDialogError(readErrors(err, 'Dəvət verilmədi.').message)
    } finally {
      setDialogBusy(false)
    }
  }

  const dialogRun = async (action: () => Promise<AdminJuryEvaluation>, fallback: string, close: () => void) => {
    setDialogBusy(true)
    setDialogError(undefined)
    try {
      await run(action, fallback).then((ok) => {
        if (ok) close()
      })
    } finally {
      setDialogBusy(false)
    }
  }

  return (
    <tr>
      <td>
        {renaming ? (
          <form
            className="jy-inline jy-inline--tight"
            onSubmit={async (ev) => {
              ev.preventDefault()
              if (await run(() => juryAdminApi.renameJuror(e.competitionId, j.jurorId, name), 'Ad dəyişmədi.')) setRenaming(false)
            }}
          >
            <input aria-label="Münsifin adı" value={name} maxLength={60} onChange={(ev) => setName(ev.target.value)} autoFocus />
            <button type="submit" className="btn btn--sm" disabled={busy}>
              Saxla
            </button>
            <button type="button" className="btn btn--sm" onClick={() => setRenaming(false)}>
              İmtina
            </button>
          </form>
        ) : (
          <>
            <span className="a-table__primary">{j.label}</span>
            <button type="button" className="a-link jy-small" onClick={() => setRenaming(true)}>
              adı dəyiş
            </button>
          </>
        )}
      </td>
      <td>
        <span className={`jy-status jy-status--${j.status}`}>{jurorStatusLabel[j.status]}</span>
        <span className="jy-mini tabular">
          {j.scored} / {total}
        </span>
        <span className="jy-bar jy-bar--mini" aria-hidden="true">
          <span style={{ width: `${percent}%` }} />
        </span>
        {j.submittedAt && (
          <span className="a-table__sub">
            {formatDateTime(j.submittedAt)}
            {j.submittedByAdmin && ' · admin tərəfindən (kağız)'}
          </span>
        )}
      </td>
      <td>
        <span className="a-table__primary">{invitationLabel[j.invitation.state]}</span>
        {j.invitation.expiresAt && j.invitation.state === 'Active' && (
          <span className="a-table__sub">bitir: {formatDateTime(j.invitation.expiresAt)}</span>
        )}
        {j.invitation.failedAttempts > 0 && (
          <span className="a-table__sub tabular">səhv kod: {j.invitation.failedAttempts} / 5</span>
        )}
      </td>
      <td>
        {j.invitation.lastSignInAt ? <span className="tabular">{formatDateTime(j.invitation.lastSignInAt)}</span> : <span className="a-muted">—</span>}
        {j.invitation.identityConfirmedAt && <span className="a-table__sub">ad təsdiqlənib</span>}
      </td>
      <td className="jy-actions">
        <button type="button" className="btn btn--sm" disabled={busy} onClick={() => setInviting(true)}>
          {j.invitation.state === 'None' ? 'Dəvət ver' : 'Yeni dəvət'}
        </button>
        {j.invitation.state === 'Active' && (
          <button
            type="button"
            className="btn btn--sm"
            disabled={busy}
            onClick={() => run(() => juryAdminApi.revokeInvitation(e.competitionId, j.jurorId), 'Dəvət ləğv olunmadı.')}
          >
            Ləğv et
          </button>
        )}
        {e.status === 'Open' && j.status !== 'Submitted' && j.missing === 0 && (
          <button type="button" className="btn btn--sm" disabled={busy} onClick={() => setLocking(true)}>
            Admin adından yekunlaşdır
          </button>
        )}
        {e.status === 'Open' && j.status === 'Submitted' && (
          <button type="button" className="btn btn--sm" disabled={busy} onClick={() => setReopening(true)}>
            Yenidən aç
          </button>
        )}
        {!e.rosterLocked && e.jurorCount > 1 && (
          <button
            type="button"
            className="btn btn--sm a-btn--danger"
            disabled={busy}
            onClick={() => run(() => juryAdminApi.removeJuror(e.competitionId, j.jurorId), 'Münsif çıxarılmadı.')}
          >
            Çıxar
          </button>
        )}

        {inviting && (
          <ConfirmDialog
            title={`${j.label} üçün dəvət`}
            confirmLabel="Dəvət yarat"
            onConfirm={issue}
            onCancel={() => setInviting(false)}
            busy={dialogBusy}
            error={dialogError}
          >
            <p>
              Şəxsi link və ayrıca giriş kodu yaradılacaq və yalnız bir dəfə göstəriləcək.
              {j.invitation.state !== 'None' && ' Əvvəlki dəvət və münsifin açıq sessiyaları bağlanacaq.'}
            </p>
            <Notice tone="warn">Sahibin göstərişi: real dəvətlər hələ göndərilmir. Yalnız sınaq üçün yaradın.</Notice>
            <label className="fin-field">
              <span>Etibarlılıq müddəti (gün)</span>
              <input type="number" min={1} max={60} value={days} onChange={(ev) => setDays(Number(ev.target.value))} />
            </label>
          </ConfirmDialog>
        )}
        {locking && (
          <ConfirmDialog
            title={`${j.label} — admin adından yekunlaşdırılsın?`}
            confirmLabel="Yekunlaşdır"
            onConfirm={() => dialogRun(() => juryAdminApi.lockOnBehalf(e.competitionId, j.jurorId), 'Yekunlaşdırılmadı.', () => setLocking(false))}
            onCancel={() => setLocking(false)}
            busy={dialogBusy}
            error={dialogError}
          >
            <p>Münsifin bütün {total} işə balı var (məsələn, kağız vərəqdən daxil edilib). Yekunlaşdırdıqdan sonra ballar kilidlənir.</p>
          </ConfirmDialog>
        )}
        {reopening && (
          <ConfirmDialog
            title={`${j.label} — qiymətləndirmə yenidən açılsın?`}
            confirmLabel="Yenidən aç"
            onConfirm={() =>
              dialogRun(() => juryAdminApi.reopen(e.competitionId, j.jurorId, reason), 'Açılmadı.', () => {
                setReopening(false)
                setReason('')
              })
            }
            onCancel={() => setReopening(false)}
            busy={dialogBusy}
            error={dialogError}
            confirmDisabled={reason.trim().length < 10}
          >
            <p>Yalnız bu münsifin balları dəyişiklik üçün açılır. Səbəb audit jurnalına yazılır.</p>
            <label className="fin-field">
              <span>Səbəb (ən azı 10 simvol)</span>
              <textarea rows={3} maxLength={1000} value={reason} onChange={(ev) => setReason(ev.target.value)} />
            </label>
          </ConfirmDialog>
        )}
      </td>
    </tr>
  )
}

function IssuedDialog({ issued, onClose }: { issued: IssuedInvitation; onClose: () => void }) {
  const link = invitationUrl(issued.link)
  const [copied, setCopied] = useState<string>()
  const copy = async (what: string, text: string) => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(what)
    } catch {
      setCopied(undefined)
    }
  }
  return (
    <ConfirmDialog title={`Dəvət hazırdır — ${issued.label}`} confirmLabel="Bağla" onConfirm={onClose} onCancel={onClose} wide>
      <p>
        Bu link və kod <strong>yalnız indi</strong> göstərilir — serverdə yalnız onların hash-i saxlanılır. Linki və kodu
        münsifə ayrı-ayrı yollarla verin (məsələn, link e-poçtla, kod telefonla).
      </p>
      <div className="jy-secret">
        <span className="jy-secret__label">Link</span>
        <code className="jy-secret__value">{link}</code>
        <button type="button" className="btn btn--sm" onClick={() => copy('link', link)}>
          {copied === 'link' ? 'Kopyalandı' : 'Kopyala'}
        </button>
      </div>
      <div className="jy-secret">
        <span className="jy-secret__label">Giriş kodu</span>
        <code className="jy-secret__value jy-secret__value--code">{issued.code}</code>
        <button type="button" className="btn btn--sm" onClick={() => copy('code', issued.code.replace(/\s/g, ''))}>
          {copied === 'code' ? 'Kopyalandı' : 'Kopyala'}
        </button>
      </div>
      <p className="a-muted">Etibarlıdır: {formatDateTime(issued.expiresAt)}-dək. Kod 5 dəfə səhv yazılsa, dəvət kilidlənir.</p>
    </ConfirmDialog>
  )
}

// ---- 4. Print --------------------------------------------------------------------------------------------------

function PrintPanel({ e }: { e: AdminJuryEvaluation }) {
  return (
    <section className="a-panel fin-print" aria-labelledby="jy-print">
      <h2 id="jy-print" className="a-panel__title">
        Çap
      </h2>
      <p className="a-muted">
        Kataloq münsiflərin qiymətləndirmə formasıdır: işlər etibarlı səslərə görə, hər birində xalq səsverməsindəki yeri,
        səs sayları və hər münsifin bal xanası (M1, M2, …). Ayrıca bal vərəqi yoxdur. Müəllif, səs verənlər və audit
        qaydaları kataloqda yoxdur; çap nüsxələri yalnız münsiflərə və səlahiyyətli təşkilatçılara verilir.
      </p>
      <div className="fin-print__docs">
        <PrintDoc
          to={juryPrintHref(e.competitionId, 'catalog')}
          title="Münsif kataloqu — 4 iş / A4"
          note={`${e.rosterSize} iş, 2×2, ${e.jurorCount} münsif xanası hər işdə.`}
        />
        <PrintDoc to={juryPrintHref(e.competitionId, 'ranking')} title="Daxili reytinq — PDF" note="Bütün münsiflər yekunlaşdırdıqdan sonra. İctimai deyil." />
        <PrintDoc to={juryPrintHref(e.competitionId, 'analytics')} title="Səs analitikası — yalnız admin" note="Etibarlı səslər və audit qərarları. Münsiflərə verilmir." />
      </div>
    </section>
  )
}

function PrintDoc({ to, title, note }: { to: string; title: string; note: string }) {
  return (
    <Link to={to} className="fin-print__doc">
      <span className="fin-print__doc-title">{title}</span>
      <span className="fin-print__doc-note">{note}</span>
    </Link>
  )
}

// ---- 5. The matrix ----------------------------------------------------------------------------------------------

function MatrixPanel({ e, refreshKey }: { e: AdminJuryEvaluation; refreshKey: number }) {
  const [onlyMissing, setOnlyMissing] = useState(false)
  const [viewing, setViewing] = useState<number | null>(null)
  const [exportError, setExportError] = useState<string>()
  const state = useApi((signal) => juryAdminApi.matrix(e.competitionId, signal), [e.competitionId, refreshKey])
  const rows = useMemo(
    () => (state.data ? (onlyMissing ? state.data.rows.filter((r) => r.scores.some((s) => s === null)) : state.data.rows) : []),
    [state.data, onlyMissing],
  )

  const download = async () => {
    setExportError(undefined)
    try {
      const blob = await juryAdminApi.exportCsv(e.competitionId)
      const href = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = href
      a.download = `munsif-ballari-${e.competitionId}.csv`
      a.click()
      setTimeout(() => URL.revokeObjectURL(href), 1000)
    } catch (err) {
      setExportError(readErrors(err, 'CSV yüklənmədi.').message)
    }
  }

  return (
    <section className="a-panel" aria-labelledby="jy-matrix">
      <div className="a-panel__head">
        <h2 id="jy-matrix" className="a-panel__title">
          Bal matrisi
        </h2>
        <div className="a-actions">
          <label className="fin-check">
            <input type="checkbox" checked={onlyMissing} onChange={(ev) => setOnlyMissing(ev.target.checked)} />
            Yalnız çatışmayanlar
          </label>
          <button type="button" className="btn btn--sm" onClick={download}>
            CSV ixrac
          </button>
        </div>
      </div>
      {exportError && <Notice tone="error">{exportError}</Notice>}
      {state.status === 'loading' && <p className="a-muted">Yüklənir…</p>}
      {state.status === 'error' && <Notice tone="error">{state.error.message}</Notice>}
      {state.data && (
        <>
          <p className="a-muted">
            Orta bal yalnız bütün münsiflər yekunlaşdırdıqdan sonra göstərilir. <span className="jy-src">K</span> — kağız
            vərəqdən daxil edilib; • — münsifin qeydi var.
          </p>
          <div className="fin-scroll jy-matrix-wrap">
            <table className="a-table jy-matrix">
              <thead>
                <tr>
                  <th scope="col">№</th>
                  <th scope="col">İş</th>
                  {state.data.jurors.map((j) => (
                    <th key={j.id} scope="col" className="fin-num">
                      {j.label}
                      {j.submitted && <span className="jy-done" aria-label="yekunlaşdırıb"> ✓</span>}
                    </th>
                  ))}
                  <th scope="col" className="fin-num">
                    Orta
                  </th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => {
                  const position = state.data!.rows.indexOf(r)
                  return (
                    <tr key={r.submissionId}>
                      <td className="tabular">{position + 1}</td>
                      <td>
                        <button type="button" className="a-link jy-work" onClick={() => setViewing(position)}>
                          <span className="a-table__primary tabular">{catalogNumber(r.reference)}</span>
                          {r.title && <span className="a-table__sub">{r.title}</span>}
                        </button>
                      </td>
                      {r.scores.map((s, i) => (
                        <td key={i} className={`fin-num tabular${s === null ? ' jy-missing' : ''}`}>
                          {s ?? '—'}
                          {r.sources[i] === 'Paper' && <span className="jy-src" title="Kağız kataloqdan">K</span>}
                          {r.hasComment[i] && <span className="jy-note" title="Qeyd var">•</span>}
                        </td>
                      ))}
                      <td className="fin-num tabular">{average(r.average)}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          {viewing !== null && (
            <WorkViewer
              ids={state.data.rows.map((r) => r.submissionId)}
              index={viewing}
              onIndex={setViewing}
              onClose={() => setViewing(null)}
              detailHref={(id) => `/admin/submissions/${id}`}
            />
          )}
        </>
      )}
    </section>
  )
}

// ---- 6. Paper sheets ----------------------------------------------------------------------------------------------

function PaperPanel({
  e,
  onChange,
  refreshKey,
}: {
  e: AdminJuryEvaluation
  onChange: (e: AdminJuryEvaluation) => void
  refreshKey: number
}) {
  const open = e.status === 'Open'
  const [jurorId, setJurorId] = useState<number>(e.jurors[0]?.jurorId ?? 0)
  const roster = useApi((signal) => juryAdminApi.matrix(e.competitionId, signal), [e.competitionId, refreshKey])
  const [values, setValues] = useState<Record<number, string>>({})
  const [preview, setPreview] = useState<PaperPreview>()
  const [choices, setChoices] = useState<Record<number, 'paper' | 'existing'>>({})
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [done, setDone] = useState<string>()
  const juror = e.jurors.find((j) => j.jurorId === jurorId)

  const lines = (): PaperLine[] =>
    Object.entries(values)
      .filter(([, v]) => v.trim() !== '')
      .map(([id, v]) => ({ submissionId: Number(id), score: v.trim() }))

  const reset = () => {
    setPreview(undefined)
    setChoices({})
    setReason('')
  }

  const check = async () => {
    setBusy(true)
    setError(undefined)
    setDone(undefined)
    try {
      setPreview(await juryAdminApi.paperPreview(e.competitionId, jurorId, lines()))
      setChoices({})
    } catch (err) {
      setError(readErrors(err, 'Önizləmə alınmadı.').message)
    } finally {
      setBusy(false)
    }
  }

  const commit = async () => {
    if (!preview) return
    setBusy(true)
    setError(undefined)
    try {
      const resolutions = preview.rows
        .filter((r) => r.status === 'Conflict')
        .map((r) => ({ submissionId: r.submissionId, keep: choices[r.submissionId], seen: r.existing! }))
      const result = await juryAdminApi.paperCommit(e.competitionId, jurorId, lines(), resolutions, reason.trim() || null)
      setDone(`Saxlanıldı: ${result.saved} yeni/dəyişən bal, ${result.unchanged} eyni, ${result.conflictsResolved} ziddiyyət həll edildi. Münsifin cəmi ${result.scored} / ${result.total} balı var.`)
      setValues({})
      reset()
      onChange(await juryAdminApi.get(e.competitionId))
    } catch (err) {
      const { message, lists } = readErrors(err, 'Saxlanmadı.')
      setError(lists.lines?.join(' · ') ?? message)
    } finally {
      setBusy(false)
    }
  }

  const conflicts = preview?.rows.filter((r) => r.status === 'Conflict') ?? []
  const unresolved = conflicts.some((c) => !choices[c.submissionId])
  const canCommit = preview && preview.invalidLines.length === 0 && !unresolved && (conflicts.length === 0 || reason.trim().length >= 10)

  return (
    <section className="a-panel" aria-labelledby="jy-paper">
      <div className="a-panel__head">
        <h2 id="jy-paper" className="a-panel__title">
          Kağız kataloqdan daxil etmə
        </h2>
      </div>
      <p className="a-muted">
        Münsifin çap kataloqunda yazdığı balları burada onun adından daxil edin. Ballar onlayn ballarla eyni qeydə yazılır (münsif + iş
        üzrə bir bal). Fərqli saxlanmış bal heç vaxt səssizcə əvəz olunmur: hər ziddiyyət üçün seçim və səbəb tələb olunur.
      </p>
      {!open ? (
        <p className="a-muted">Qiymətləndirmə açıq deyil.</p>
      ) : (
        <>
          <div className="a-field jy-select">
            <label className="a-field__label" htmlFor="jy-paper-juror">
              Münsif
            </label>
            <select
              id="jy-paper-juror"
              value={jurorId}
              onChange={(ev) => {
                setJurorId(Number(ev.target.value))
                setValues({})
                reset()
              }}
            >
              {e.jurors.map((j) => (
                <option key={j.jurorId} value={j.jurorId}>
                  {j.label} — {j.scored}/{e.rosterSize}
                  {j.status === 'Submitted' ? ' (kilidli)' : ''}
                </option>
              ))}
            </select>
          </div>
          {juror?.status === 'Submitted' ? (
            <Notice tone="warn">Bu münsifin qiymətləndirməsi kilidlidir. Dəyişmək üçün əvvəlcə səbəb göstərərək yenidən açın.</Notice>
          ) : roster.data ? (
            <>
              <div className="jy-paper-grid" role="group" aria-label="Kağız kataloqdakı ballar">
                {roster.data.rows.map((r, i) => {
                  const column = roster.data!.jurors.findIndex((j) => j.id === jurorId)
                  const saved = column >= 0 ? r.scores[column] : null
                  return (
                    <label key={r.submissionId} className="jy-paper-row">
                      <span className="jy-paper-row__n tabular">{i + 1}</span>
                      <span className="jy-paper-row__ref tabular">{catalogNumber(r.reference)}</span>
                      <input
                        inputMode="numeric"
                        maxLength={3}
                        aria-label={`${catalogNumber(r.reference)} balı`}
                        value={values[r.submissionId] ?? ''}
                        placeholder={saved === null ? '' : String(saved)}
                        onChange={(ev) => {
                          setValues((all) => ({ ...all, [r.submissionId]: ev.target.value }))
                          setPreview(undefined)
                        }}
                        onKeyDown={(ev) => {
                          if (ev.key === 'Enter') {
                            ev.preventDefault()
                            const inputs = [...document.querySelectorAll<HTMLInputElement>('.jy-paper-row input')]
                            inputs[inputs.indexOf(ev.currentTarget) + 1]?.focus()
                          }
                        }}
                      />
                    </label>
                  )
                })}
              </div>
              <p className="a-muted">
                Sıra çap olunmuş kataloqun sırasıdır (hər səhifədə 4 iş); münsifin balını onun M-xanasından yazın. Boz rəqəm —
                artıq saxlanmış bal. Enter növbəti sətrə keçir. Boş sətir dəyişdirilmir.
              </p>
              <div className="a-actions">
                <button type="button" className="btn" disabled={busy || lines().length === 0} onClick={check}>
                  Yoxla (önizləmə)
                </button>
              </div>
            </>
          ) : (
            <p className="a-muted">Yüklənir…</p>
          )}
        </>
      )}
      {preview && (
        <div className="jy-preview" role="region" aria-label="Önizləmə">
          <p className="tabular">
            Yeni: <strong>{preview.new}</strong> · eyni: {preview.same} · ziddiyyət: <strong>{preview.conflicts}</strong> ·
            vərəqdə boş: {preview.missing}
          </p>
          {preview.invalidLines.length > 0 && (
            <Notice tone="error">
              Düzəldilməli sətirlər (heç nə saxlanmayacaq):
              <ul>
                {preview.invalidLines.map((l) => (
                  <li key={l}>{l}</li>
                ))}
              </ul>
            </Notice>
          )}
          {conflicts.length > 0 && (
            <div className="jy-conflicts">
              <h3 className="fin-sub">Ziddiyyətlər — hansı bal qalsın?</h3>
              {conflicts.map((c) => (
                <fieldset key={c.submissionId} className="jy-conflict">
                  <legend className="tabular">{catalogNumber(c.reference)}</legend>
                  <label className="fin-check">
                    <input
                      type="radio"
                      name={`c-${c.submissionId}`}
                      checked={choices[c.submissionId] === 'paper'}
                      onChange={() => setChoices((all) => ({ ...all, [c.submissionId]: 'paper' }))}
                    />
                    Kağız: <strong className="tabular">{c.paper}</strong>
                  </label>
                  <label className="fin-check">
                    <input
                      type="radio"
                      name={`c-${c.submissionId}`}
                      checked={choices[c.submissionId] === 'existing'}
                      onChange={() => setChoices((all) => ({ ...all, [c.submissionId]: 'existing' }))}
                    />
                    Saxlanmış ({c.existingSource === 'Paper' ? 'kağız' : 'onlayn'}): <strong className="tabular">{c.existing}</strong>
                  </label>
                </fieldset>
              ))}
              <label className="fin-field">
                <span>Səbəb (ən azı 10 simvol)</span>
                <textarea rows={2} maxLength={1000} value={reason} onChange={(ev) => setReason(ev.target.value)} />
              </label>
            </div>
          )}
          <div className="a-actions">
            <button type="button" className="btn btn--primary" disabled={busy || !canCommit} onClick={commit}>
              Saxla
            </button>
            <button type="button" className="btn" onClick={reset}>
              İmtina
            </button>
          </div>
        </div>
      )}
      {error && <Notice tone="error">{error}</Notice>}
      {done && <Notice tone="success">{done}</Notice>}
    </section>
  )
}

// ---- 7. The internal ranking ----------------------------------------------------------------------------------------

function RankingPanel({ e, refreshKey }: { e: AdminJuryEvaluation; refreshKey: number }) {
  const state = useApi((signal) => juryAdminApi.ranking(e.competitionId, signal), [e.competitionId, refreshKey])
  const [onlyTies, setOnlyTies] = useState(false)
  const [viewing, setViewing] = useState<number | null>(null)
  const [exportError, setExportError] = useState<string>()
  const r: JuryRanking | undefined = state.data
  const rows = useMemo(() => (r ? (onlyTies ? r.rows.filter((x) => x.tied) : r.rows) : []), [r, onlyTies])

  const download = async () => {
    setExportError(undefined)
    try {
      const blob = await juryAdminApi.rankingCsv(e.competitionId)
      const href = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = href
      a.download = `munsif-reytinqi-${e.competitionId}.csv`
      a.click()
      setTimeout(() => URL.revokeObjectURL(href), 1000)
    } catch (err) {
      setExportError(readErrors(err, 'CSV yüklənmədi.').message)
    }
  }

  return (
    <section className="a-panel" aria-labelledby="jy-ranking">
      <div className="a-panel__head">
        <h2 id="jy-ranking" className="a-panel__title">
          Daxili reytinq
        </h2>
        <Badge tone="review">Yalnız adminlər üçün</Badge>
      </div>
      {state.status === 'loading' && <p className="a-muted">Yüklənir…</p>}
      {state.status === 'error' && <Notice tone="error">{state.error.message}</Notice>}
      {r && !r.complete && (
        <Notice tone="info">
          Reytinq bütün münsiflər bütün işləri qiymətləndirib yekunlaşdırdıqdan sonra hesablanır. Hazırda:{' '}
          <span className="tabular">
            {r.submittedJurors} / {r.jurorCount}
          </span>{' '}
          münsif yekunlaşdırıb,{' '}
          <span className="tabular">
            {r.completedScores} / {r.expectedScores}
          </span>{' '}
          bal verilib.
        </Notice>
      )}
      {r?.complete && (
        <>
          <p className="a-muted">
            {r.rosterSize} iş {r.jurorCount} münsifin 1–10 ballarının dəqiq ortasına görə sıralanıb. Orta balı eyni olan
            işlər eyni yeri bölüşür və <strong>bərabər</strong> kimi göstərilir — aralarında heç bir seçim edilmir. Bu reytinq
            ictimai deyil və mükafat vermir.
          </p>
          <div className="a-actions">
            <span className="tabular jy-ranking-summary">
              {r.tieGroups > 0 ? `${r.tieGroups} bərabərlik qrupu` : 'Bərabərlik yoxdur'}
            </span>
            <label className="fin-check">
              <input type="checkbox" checked={onlyTies} onChange={(ev) => setOnlyTies(ev.target.checked)} />
              Yalnız bərabərliklər
            </label>
            <button type="button" className="btn btn--sm" onClick={download}>
              CSV ixrac
            </button>
            <Link to={juryPrintHref(e.competitionId, 'ranking')} className="btn btn--sm">
              PDF / çap
            </Link>
          </div>
          {exportError && <Notice tone="error">{exportError}</Notice>}
          <div className="fin-scroll jy-matrix-wrap">
            <table className="a-table jy-ranking" aria-label="Daxili reytinq">
              <thead>
                <tr>
                  <th scope="col" className="fin-num">
                    Yer
                  </th>
                  <th scope="col">İş</th>
                  <th scope="col" className="fin-num">
                    Orta bal
                  </th>
                  <th scope="col" className="fin-num">
                    Bal sayı
                  </th>
                  <th scope="col">Bərabərlik</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((x) => (
                  <tr key={x.submissionId} className={x.tied ? 'is-tied' : undefined}>
                    <td className="fin-num tabular jy-ranking__pos">{x.position}</td>
                    <td>
                      <button
                        type="button"
                        className="a-link jy-work"
                        onClick={() => setViewing(r.rows.findIndex((y) => y.submissionId === x.submissionId))}
                      >
                        <span className="a-table__primary tabular">{catalogNumber(x.reference)}</span>
                        {x.title && <span className="a-table__sub">{x.title}</span>}
                      </button>
                    </td>
                    <td className="fin-num tabular" title={`${x.scoreSum} / ${x.scoreCount} = ${average(x.average, 4)}`}>
                      <strong>{average(x.average)}</strong>
                    </td>
                    <td className="fin-num tabular">
                      {x.scoreCount} / {r.jurorCount}
                    </td>
                    <td>{x.tied && <span className="jy-tag jy-tag--tie">Bərabər · {x.tieSize} iş</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {viewing !== null && (
            <WorkViewer
              ids={r.rows.map((x) => x.submissionId)}
              index={viewing}
              onIndex={setViewing}
              onClose={() => setViewing(null)}
              detailHref={(id) => `/admin/submissions/${id}`}
            />
          )}
        </>
      )}
    </section>
  )
}

// ---- 8. The audit -------------------------------------------------------------------------------------------------

function AuditPanel({ competitionId, refreshKey }: { competitionId: number; refreshKey: number }) {
  const state = useApi((signal) => juryAdminApi.audit(competitionId, signal), [competitionId, refreshKey])
  return (
    <section className="a-panel" aria-labelledby="jy-audit">
      <h2 id="jy-audit" className="a-panel__title">
        Audit jurnalı
      </h2>
      {state.status === 'loading' && <p className="a-muted">Yüklənir…</p>}
      {state.status === 'error' && <Notice tone="error">{state.error.message}</Notice>}
      {state.data && state.data.length === 0 && <p className="a-muted">Hələ qeyd yoxdur.</p>}
      {state.data && state.data.length > 0 && (
        <ol role="list" className="a-timeline jy-audit">
          {state.data.map((x) => (
            <li key={x.id}>
              <span className="a-timeline__what">
                {auditActionLabel[x.action] ?? x.action}
                {x.summary && <span className="a-muted"> — {x.summary}</span>}
              </span>
              <span className="a-timeline__who">
                {x.userName ?? 'Münsif / sistem'}, <span className="tabular">{formatDateTime(x.createdAt)}</span>
              </span>
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}
