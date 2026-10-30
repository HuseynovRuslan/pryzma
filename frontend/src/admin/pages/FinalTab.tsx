import { useState } from 'react'
import { Link } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDateTime } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { adminApi, type AdminCompetition, type AdminFinal, type FinalRow, type FinalTieGroup, type JuryFinalist } from '../api'
import { ConfirmDialog } from '../ConfirmDialog'
import { readErrors } from '../errors'
import { parseMark } from '../finalShow'
import { juryAdminApi } from '../juryApi'
import { ImageThumb } from '../PreviewThumb'
import { allWorksPrintHref, BLIND_INSTRUCTION, CRITERIA, finalistsPrintHref, leaderboardPrintHref } from '../print'
import { previewLabel } from '../printPictures'
import { Badge, Notice } from '../ui'
import { WorkViewer } from '../WorkViewer'
import './final.css'

/**
 * The jury final of a competition (D13), as the admin runs it, in order: confirm the ten (after the integrity audit),
 * print the jury's sheets, type in the jury's marks, lock them, read the combined result, settle a tie at a prize by
 * the jury's recorded order, run the show — and only then publish the official places.
 *
 * While the marks are being typed in nothing of the public vote is on the screen — no votes, no public rank — so the
 * people entering the jury's judgement are not shown the crowd's (the server sends none of it until the lock).
 */
export function FinalTab({ competition }: { competition: AdminCompetition }) {
  const state = useApi((signal) => adminApi.final(competition.id, signal), [competition.id])
  const format = useApi((signal) => juryAdminApi.get(competition.id, signal), [competition.id])
  const [final, setFinal] = useState<AdminFinal>()
  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  const f = final ?? state.data

  return (
    <div className="a-form fin" style={{ gap: 'var(--s-5)' }}>
      {format.data?.exists && (
        <Notice tone="warn">
          Bu müsabiqədə münsiflər bütün işləri daxili qiymətləndirir. Aşağıdakı köhnə 50/50 Top 10 finalı yalnız tarixçə
          kimi saxlanılır: bal, şou və dərc burada bağlıdır. Xalq səsverməsinin nəticəsi “Nəticələr” bölməsindədir.{' '}
          <Link to={`/admin/competitions/${competition.id}?tab=jury`}>Münsiflər bölməsinə keç</Link>
        </Notice>
      )}
      <Steps f={f} />
      {f.stage === null ? (
        <FreezePanel f={f} onChange={setFinal} />
      ) : (
        <>
          <FrozenPanel f={f} onChange={setFinal} />
          {f.stage === 'Frozen' ? <JuryPanel f={f} onChange={setFinal} /> : <ResultPanel f={f} onChange={setFinal} />}
        </>
      )}
    </div>
  )
}

const STEPS = [
  'Top 10-u təsdiqlə',
  'Münsif vərəqlərini çap et',
  'Balları daxil et',
  'Qiymətləndirməni kilidlə',
  'Hesablamaya bax, finalı göstər',
  'Rəsmi nəticələri dərc et',
] as const

function Steps({ f }: { f: AdminFinal }) {
  const at = f.stage === null ? 0 : f.stage === 'Frozen' ? (f.missingMarks === 0 ? 3 : 2) : f.stage === 'JuryLocked' ? 4 : 6
  return (
    <ol className="fin-steps" aria-label="Finalın addımları">
      {STEPS.map((s, i) => (
        <li key={s} className={i < at ? 'is-done' : i === at ? 'is-current' : undefined} aria-current={i === at ? 'step' : undefined}>
          <span className="fin-steps__n tabular">{i + 1}</span>
          {s}
        </li>
      ))}
    </ol>
  )
}

// ---- 1. Freezing the ten ------------------------------------------------------------------------------------------

function FreezePanel({ f, onChange }: { f: AdminFinal; onChange: (f: AdminFinal) => void }) {
  const p = f.preview!
  const [prizes, setPrizes] = useState(f.prizes.map(String))
  const [audited, setAudited] = useState(false)
  const [dialog, setDialog] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const numbers = prizes.map((x) => Number(x))
  const prizesOk = numbers.every((n) => Number.isInteger(n) && n >= 0)
  const can = p.problems.length === 0 && audited && prizesOk

  const freeze = async () => {
    setBusy(true)
    setError(undefined)
    try {
      onChange(await adminApi.freezeFinal(f.competitionId, numbers))
      setDialog(false)
    } catch (err) {
      const { message, lists } = readErrors(err, 'Top 10 təsdiqlənmədi.')
      setError(lists.final?.join(' ') ?? message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="a-panel" aria-labelledby="fin-freeze">
      <div className="a-panel__head">
        <h2 id="fin-freeze" className="a-panel__title">
          Finalçılar — etibarlı səslərə görə Top 10
        </h2>
        <Badge tone="draft">Təsdiqlənməyib</Badge>
      </div>
      <p className="a-muted">
        Səsvermə bağlandıqdan və final bütövlük auditi tamamlandıqdan sonra ən çox <strong>etibarlı</strong> səs alan on
        iş finalçı kimi dondurulur. Bundan sonra səs dəyişiklikləri finala təsir etmir. Etibarsız sayılmış səslər heç yerdə
        sayılmır.
      </p>
      <p className="tabular">
        Etibarlı səslər: <strong>{p.validVotes}</strong> · etibarsız sayılmış: {p.invalidatedVotes} · səs vəziyyətinin
        izi: <code>{p.votesFingerprint.slice(0, 12)}…</code>
      </p>
      <table className="a-table fin-table">
        <thead>
          <tr>
            <th scope="col">Yer</th>
            <th scope="col">İş</th>
            <th scope="col" className="fin-num">
              Etibarlı səs
            </th>
          </tr>
        </thead>
        <tbody>
          {p.top.map((c) => (
            <tr key={c.submissionId}>
              <td className="tabular">{c.rank}</td>
              <td>
                <span className="a-table__primary tabular">{c.reference}</span>
                {c.title && <span className="a-table__sub">{c.title}</span>}
              </td>
              <td className="fin-num tabular">{c.validVotes}</td>
            </tr>
          ))}
          {p.next && (
            <tr className="fin-table__next">
              <td className="tabular">{p.next.rank}</td>
              <td>
                <span className="a-table__primary tabular">{p.next.reference}</span>
                <span className="a-table__sub">Top 10-dan kənarda — sərhəddəki fərq üçün</span>
              </td>
              <td className="fin-num tabular">{p.next.validVotes}</td>
            </tr>
          )}
        </tbody>
      </table>

      {p.problems.length > 0 && (
        <Notice tone="warn">
          Hələ təsdiqləmək olmur:
          <ul>
            {p.problems.map((x) => (
              <li key={x}>{x}</li>
            ))}
          </ul>
        </Notice>
      )}

      <fieldset className="fin-prizes">
        <legend>Mükafatlar (AZN)</legend>
        {['1-ci yer', '2-ci yer', '3-cü yer'].map((label, i) => (
          <label key={label}>
            {label}
            <input
              type="number"
              min={0}
              step={1}
              inputMode="numeric"
              value={prizes[i]}
              onChange={(e) => setPrizes((all) => all.map((x, k) => (k === i ? e.target.value : x)))}
            />
          </label>
        ))}
      </fieldset>
      <label className="fin-check">
        <input type="checkbox" checked={audited} onChange={(e) => setAudited(e.target.checked)} />
        Final bütövlük auditi tamamlanıb və təsdiqlənib
      </label>
      <div className="a-actions">
        <button type="button" className="btn btn--primary" disabled={!can} onClick={() => setDialog(true)}>
          Top 10-u təsdiqlə
        </button>
      </div>
      {error && !dialog && <Notice tone="error">{error}</Notice>}

      {dialog && (
        <ConfirmDialog
          title="Top 10 dondurulsun?"
          confirmLabel="Təsdiqlə və dondur"
          onConfirm={freeze}
          onCancel={() => setDialog(false)}
          busy={busy}
          error={error}
        >
          <p>
            Bu on iş, hazırkı etibarlı səs sayları, xalq yerləri və xalları ilə birlikdə dondurulur. Münsif balı daxil
            edildikdən sonra bu siyahı dəyişdirilə bilməz. Rəsmi nəticə yazılmır.
          </p>
          <p className="tabular">
            Mükafatlar: {numbers[0]} / {numbers[1]} / {numbers[2]} AZN
          </p>
        </ConfirmDialog>
      )}
    </section>
  )
}

// ---- The frozen record --------------------------------------------------------------------------------------------

function FrozenPanel({ f, onChange }: { f: AdminFinal; onChange: (f: AdminFinal) => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [dialog, setDialog] = useState(false)
  const [audited, setAudited] = useState(false)
  const [why, setWhy] = useState('')

  const refreeze = async () => {
    setBusy(true)
    setError(undefined)
    try {
      onChange(await adminApi.freezeFinal(f.competitionId, f.prizes, why.trim()))
      setDialog(false)
    } catch (err) {
      const { message, lists } = readErrors(err, 'Yenidən təsdiqlənmədi.')
      setError(lists.final?.join(' ') ?? message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="a-panel" aria-labelledby="fin-frozen">
      <div className="a-panel__head">
        <h2 id="fin-frozen" className="a-panel__title">
          Finalçılar təsdiqlənib
        </h2>
        <StageBadge f={f} />
      </div>
      <p className="a-muted">
        <span className="tabular">{formatDateTime(f.frozenAt)}</span> · {f.frozenBy ?? '—'} · etibarlı səslər{' '}
        <span className="tabular">{f.frozenValidVotes}</span>, etibarsız sayılmış{' '}
        <span className="tabular">{f.frozenInvalidatedVotes}</span> · iz <code>{f.frozenVotesFingerprint?.slice(0, 12)}…</code>
      </p>
      <p className="tabular">Mükafatlar: {f.prizes.join(' / ')} AZN</p>
      {f.votesChangedSinceFreeze && (
        <Notice tone="warn">
          Dondurulmadan sonra səs vəziyyəti dəyişib (səs etibarsız sayılıb və ya bərpa olunub). Final dondurulmuş
          vəziyyətlə qalır.
        </Notice>
      )}
      <div className="fin-print" role="group" aria-labelledby="fin-print-title">
        <h3 id="fin-print-title" className="fin-sub">
          Çap
        </h3>
        <p className="a-muted">
          Hamısı 4 iş / A4, şəkil və müəlliflə. PDF-də kart 2-ci səhifəni göstərir; bütün səhifələr istəyə görə əlavə
          olunur.
        </p>
        <div className="fin-print__docs">
          <PrintDoc to={finalistsPrintHref(f.competitionId)} title="Münsif paketi — Top 10" note="Münsiflərə verilir: səs sayı və xalq sıralaması yoxdur." />
          <PrintDoc to={allWorksPrintHref(f.competitionId)} title="Bütün işləri çap et" note="Rəhbərlik üçün baxış materialı." />
          <PrintDoc to={leaderboardPrintHref(f.competitionId, 'top10')} title="Top 10 xalq nəticəsi" note="Dondurulmuş snapshot — ayrıca sənəd." />
          <PrintDoc to={leaderboardPrintHref(f.competitionId, 'full')} title="Tam səs sıralaması" note="Etibarlı səslər — ayrıca sənəd." />
        </div>
      </div>
      <div className="a-actions">
        {f.stage !== 'Frozen' && (
          <a href={`/admin/final-show/${f.competitionId}`} target="_blank" rel="noreferrer" className="btn">
            Final şousunu aç
          </a>
        )}
        {f.canRefreeze && (
          <button type="button" className="btn" onClick={() => setDialog(true)}>
            Yenidən təsdiqlə
          </button>
        )}
      </div>
      {dialog && (
        <ConfirmDialog
          title="Top 10 yenidən dondurulsun?"
          confirmLabel="Yenidən dondur"
          onConfirm={refreeze}
          onCancel={() => setDialog(false)}
          busy={busy}
          error={error}
          confirmDisabled={!audited || why.trim().length < 10}
        >
          <p>Siyahı hazırkı etibarlı səslərdən yenidən götürülür. Hələ heç bir münsif balı daxil edilməyib.</p>
          <p className="a-muted">
            Əvvəlki Top 10 silinmir: səsləri, yerləri və xalları ilə birlikdə səbəbi audit jurnalına yazılır.
          </p>
          <label className="fin-field">
            <span>Əvvəlki siyahı niyə əvəz olunur?</span>
            <textarea rows={3} maxLength={300} value={why} onChange={(e) => setWhy(e.target.value)} />
          </label>
          <label className="fin-check">
            <input type="checkbox" checked={audited} onChange={(e) => setAudited(e.target.checked)} />
            Final bütövlük auditi tamamlanıb və təsdiqlənib
          </label>
        </ConfirmDialog>
      )}
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

function StageBadge({ f }: { f: AdminFinal }) {
  if (f.stage === 'Published') return <Badge tone="live">Rəsmi nəticə dərc olunub</Badge>
  if (f.stage === 'JuryLocked') return <Badge tone="good">Qiymətləndirmə kilidlənib</Badge>
  return <Badge tone="review">Münsif qiymətləndirməsi gedir</Badge>
}

// ---- 3–4. The jury's marks ----------------------------------------------------------------------------------------

type Draft = Record<number, [string, string, string]>

const markText = (n: number | null) => (n === null ? '' : String(n).replace('.', ','))

const sumText = (n: number) => String(n).replace('.', ',')

function JuryPanel({ f, onChange }: { f: AdminFinal; onChange: (f: AdminFinal) => void }) {
  const [jurorId, setJurorId] = useState(f.jurors[0]?.id)
  const juror = f.jurors.find((j) => j.id === jurorId) ?? f.jurors[0]
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [dialog, setDialog] = useState(false)
  const max = f.jurors.length * 30

  const run = async (action: () => Promise<AdminFinal>, fallback: string) => {
    setBusy(true)
    setError(undefined)
    try {
      const next = await action()
      onChange(next)
      return next
    } catch (err) {
      const { message, lists } = readErrors(err, fallback)
      setError(lists.jury?.join(' ') ?? message)
      return null
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="a-panel" aria-labelledby="fin-jury">
      <div className="a-panel__head">
        <h2 id="fin-jury" className="a-panel__title">
          Münsif balları
        </h2>
        <span className="a-muted tabular">
          {f.missingMarks === 0 ? 'Bütün ballar daxil edilib' : `${f.missingMarks} bal gözlənilir`}
        </span>
      </div>
      <p className="fin-blind" role="note">
        {BLIND_INSTRUCTION}
      </p>
      <p className="a-muted">
        Hər münsifin kağız vərəqindəki balları ayrıca daxil edin: hər meyar 0–10, 0,5 addımla. Xalq səsverməsinin
        nəticələri bu ekranda göstərilmir.
      </p>

      <div className="fin-jurors" role="tablist" aria-label="Münsiflər">
        {f.jurors.map((j) => (
          <button
            key={j.id}
            type="button"
            role="tab"
            aria-selected={j.id === juror?.id}
            className={`fin-jurors__tab${j.id === juror?.id ? ' is-active' : ''}`}
            onClick={() => setJurorId(j.id)}
          >
            {j.label}
            {f.finalists.every((x) => x.marks.find((m) => m.jurorId === j.id)?.complete) && (
              <span className="fin-jurors__done" aria-label="tamamlanıb">
                ✓
              </span>
            )}
          </button>
        ))}
        <button type="button" className="btn btn--sm" disabled={busy} onClick={() => run(() => adminApi.addJuror(f.competitionId), 'Münsif əlavə olunmadı.')}>
          Münsif əlavə et
        </button>
      </div>

      {juror && (
        <JurorName
          key={`name-${juror.id}`}
          label={juror.label}
          busy={busy}
          onSave={(label) => run(() => adminApi.renameJuror(f.competitionId, juror.id, label), 'Ad saxlanmadı.')}
        />
      )}

      {juror && (
        <JurorSheet
          key={juror.id}
          f={f}
          jurorId={juror.id}
          label={juror.label}
          canRemove={!juror.hasMarks && f.jurors.length > 1}
          busy={busy}
          onSave={(marks) => run(() => adminApi.saveJuryMarks(f.competitionId, juror.id, marks), 'Ballar saxlanmadı.')}
          onRemove={() => run(() => adminApi.removeJuror(f.competitionId, juror.id), 'Münsif silinmədi.')}
        />
      )}

      <h3 className="fin-sub">Cəmi (bütün münsiflər)</h3>
      <table className="a-table fin-table">
        <thead>
          <tr>
            <th scope="col">İş</th>
            {f.jurors.map((j) => (
              <th key={j.id} scope="col" className="fin-num">
                {j.label}
              </th>
            ))}
            <th scope="col" className="fin-num">
              Cəm / {max}
            </th>
          </tr>
        </thead>
        <tbody>
          {f.finalists.map((x) => (
            <tr key={x.submissionId}>
              <td>
                <span className="a-table__primary tabular">{x.reference}</span>
              </td>
              {x.marks.map((m) => (
                <td key={m.jurorId} className="fin-num tabular">
                  {m.complete ? `${sumText(m.subtotal)} / 30` : <span className="a-muted">natamam</span>}
                </td>
              ))}
              <td className="fin-num tabular">
                <strong>{sumText(x.juryTotal)}</strong>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <div className="a-actions">
        <button type="button" className="btn btn--primary" disabled={busy || f.missingMarks > 0} onClick={() => setDialog(true)}>
          Münsif qiymətləndirməsini kilidlə
        </button>
      </div>
      {error && !dialog && <Notice tone="error">{error}</Notice>}
      {dialog && (
        <ConfirmDialog
          title="Münsif qiymətləndirməsi kilidlənsin?"
          confirmLabel="Kilidlə"
          onConfirm={async () => {
            if (await run(() => adminApi.lockJury(f.competitionId), 'Kilidlənmədi.')) setDialog(false)
          }}
          onCancel={() => setDialog(false)}
          busy={busy}
          error={error}
        >
          <p>
            Kilidləndikdən sonra ballar dəyişdirilə bilməz; dəyişmək üçün kilid səbəb göstərilərək açılmalıdır və bu
            auditdə qeyd olunur. Kilidləndikdən sonra yekun hesablama və final şousu açılır.
          </p>
        </ConfirmDialog>
      )}
    </section>
  )
}

/**
 * The juror's name as the admin's screens and printed sheets show it — "Münsif 2" until the admin types the juror's own
 * name. Only the label changes; the marks stay where they are. The public pages count the jurors and never name them.
 */
function JurorName({ label, busy, onSave }: { label: string; busy: boolean; onSave: (label: string) => Promise<AdminFinal | null> }) {
  const [value, setValue] = useState(label)
  const clean = value.trim().replace(/\s+/g, ' ')
  return (
    <form
      className="fin-juror-name"
      onSubmit={(e) => {
        e.preventDefault()
        if (clean && clean !== label) void onSave(clean)
      }}
    >
      <label className="fin-juror-name__field">
        <span className="a-muted">Münsifin adı</span>
        <input className="input" value={value} maxLength={60} onChange={(e) => setValue(e.target.value)} />
      </label>
      <button type="submit" className="btn btn--sm" disabled={busy || !clean || clean === label}>
        Adı saxla
      </button>
    </form>
  )
}

function JurorSheet({
  f,
  jurorId,
  label,
  canRemove,
  busy,
  onSave,
  onRemove,
}: {
  f: AdminFinal
  jurorId: number
  label: string
  canRemove: boolean
  busy: boolean
  onSave: (marks: { submissionId: number; idea: number | null; execution: number | null; relevance: number | null }[]) => Promise<AdminFinal | null>
  onRemove: () => void
}) {
  const initial = (): Draft =>
    Object.fromEntries(
      f.finalists.map((x) => {
        const m = x.marks.find((k) => k.jurorId === jurorId)
        return [x.submissionId, [markText(m?.idea ?? null), markText(m?.execution ?? null), markText(m?.relevance ?? null)]]
      }),
    )
  const [draft, setDraft] = useState<Draft>(initial)
  const [saved, setSaved] = useState(false)
  // The work open in the viewer (its row), for reading every page before marking — never with its vote.
  const [viewing, setViewing] = useState<number | null>(null)
  const parsed = f.finalists.map((x) => ({ x, marks: draft[x.submissionId].map(parseMark) }))
  const invalid = parsed.some((r) => r.marks.includes('invalid'))
  const dirty = JSON.stringify(draft) !== JSON.stringify(initial())

  const save = async () => {
    const next = await onSave(
      parsed.map(({ x, marks }) => ({
        submissionId: x.submissionId,
        idea: marks[0] as number | null,
        execution: marks[1] as number | null,
        relevance: marks[2] as number | null,
      })),
    )
    setSaved(next !== null)
  }

  const set = (submissionId: number, k: number, value: string) => {
    setSaved(false)
    setDraft((d) => ({
      ...d,
      [submissionId]: d[submissionId].map((v, i) => (i === k ? value : v)) as [string, string, string],
    }))
  }

  return (
    <div className="fin-sheet" role="tabpanel" aria-label={label}>
      {/*
        One grid for the header and every row (--fin-cols in final.css): the work, the three marks, the sum. Each mark
        column is centred, so a box sits under the centre of its heading on every row, whatever the heading's length.
      */}
      <div className="fin-marks" role="table" aria-label={`${label}: ballar`}>
        <div className="fin-marks__row fin-marks__head" role="row">
          <span className="fin-marks__work" role="columnheader">
            İş
          </span>
          {CRITERIA.map((c) => (
            <span key={c} className="fin-marks__col" role="columnheader">
              {c} <span className="fin-marks__of">/ 10</span>
            </span>
          ))}
          <span className="fin-marks__col" role="columnheader">
            Cəm <span className="fin-marks__of">/ 30</span>
          </span>
        </div>
        {parsed.map(({ x, marks }, row) => {
          const total = marks.every((m) => typeof m === 'number') ? (marks as number[]).reduce((a, b) => a + b, 0) : null
          return (
            <div key={x.submissionId} className="fin-marks__row" role="row">
              <div className="fin-marks__work" role="rowheader">
                <FinalistLabel x={x} onView={() => setViewing(row)} />
              </div>
              {CRITERIA.map((c, k) => (
                <label key={c} className="fin-marks__cell" role="cell">
                  <span className="fin-marks__label">{c}</span>
                  <span className="fin-marks__box">
                    <input
                      className={`fin-mark${marks[k] === 'invalid' ? ' is-invalid' : ''}`}
                      inputMode="decimal"
                      autoComplete="off"
                      aria-label={`${label}: ${x.reference}, ${c}`}
                      aria-invalid={marks[k] === 'invalid' || undefined}
                      value={draft[x.submissionId][k]}
                      onChange={(e) => set(x.submissionId, k, e.target.value)}
                      onKeyDown={(e) => {
                        // ↑ / ↓ move a mark by half a point, within 0–10 — a sheet typed in with the keyboard alone.
                        if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return
                        e.preventDefault()
                        const now = marks[k]
                        const from = typeof now === 'number' ? now : e.key === 'ArrowUp' ? -0.5 : 10.5
                        const next = Math.min(10, Math.max(0, from + (e.key === 'ArrowUp' ? 0.5 : -0.5)))
                        set(x.submissionId, k, markText(next))
                      }}
                    />
                    <span className="fin-marks__unit">/ 10</span>
                  </span>
                </label>
              ))}
              <div className="fin-marks__cell" role="cell">
                <span className="fin-marks__label">Cəm</span>
                <span className="fin-marks__box">
                  <output className="fin-mark fin-mark--total tabular" aria-label={`${label}: ${x.reference}, cəm`}>
                    {total === null ? '—' : sumText(total)}
                  </output>
                  <span className="fin-marks__unit">/ 30</span>
                </span>
              </div>
            </div>
          )
        })}
      </div>
      {viewing !== null && (
        <WorkViewer
          ids={f.finalists.map((x) => x.submissionId)}
          index={viewing}
          onIndex={setViewing}
          onClose={() => setViewing(null)}
        />
      )}
      {invalid && <Notice tone="error">Bal 0 ilə 10 arasında, 0,5 addımla olmalıdır (məsələn 7 və ya 7,5).</Notice>}
      <div className="a-actions">
        <button type="button" className="btn btn--primary" disabled={busy || invalid || !dirty} onClick={save}>
          {label} — balları yadda saxla
        </button>
        {saved && !dirty && <span className="a-muted">Saxlanıldı.</span>}
        {canRemove && (
          <button type="button" className="btn" disabled={busy} onClick={onRemove}>
            {label} — sil
          </button>
        )}
      </div>
    </div>
  )
}

/**
 * A finalist on the jury's sheet: its picture (a PDF by the page that stands for it), reference and title, and "Tam
 * materiala bax" — every picture and every page, so the work is judged whole and not by its thumbnail. The viewer
 * shows the work only: no vote, no rank.
 */
function FinalistLabel({ x, onView }: { x: JuryFinalist; onView: () => void }) {
  const label = previewLabel(x.image)
  return (
    <span className="fin-work">
      {x.image && (
        // The same action as the link below, for the mouse; the keyboard reaches it through the link.
        <button type="button" className="fin-work__open" tabIndex={-1} aria-hidden="true" onClick={onView}>
          <ImageThumb submissionId={x.submissionId} image={x.image} className="fin-work__thumb" />
        </button>
      )}
      <span>
        <span className="a-table__primary tabular">{x.reference}</span>
        {x.title && <span className="a-table__sub">{x.title}</span>}
        {x.author && <span className="a-table__sub fin-work__author">Müəllif: {x.author}</span>}
        <span className="fin-work__more">
          <button type="button" className="a-link" onClick={onView} aria-label={`${x.reference}: tam materiala bax`}>
            Tam materiala bax
          </button>
          {label && <span className="a-muted tabular"> · {label}</span>}
        </span>
      </span>
    </span>
  )
}

// ---- 5–6. The combined result, the show, the publish ----------------------------------------------------------

const TIE_NOTE: Record<FinalRow['tie'], string | null> = {
  None: null,
  SharedPlace: 'Bərabər — yer bölünür',
  DecidedByJury: 'Münsiflərin qərarı ilə',
  NeedsJuryDecision: 'Münsif qərarı tələb olunur',
}

function ResultPanel({ f, onChange }: { f: AdminFinal; onChange: (f: AdminFinal) => void }) {
  const c = f.calculation!
  const published = f.stage === 'Published'
  const [dialog, setDialog] = useState<'publish' | 'unlock' | null>(null)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  const run = async (action: () => Promise<AdminFinal>, fallback: string) => {
    setBusy(true)
    setError(undefined)
    try {
      onChange(await action())
      setDialog(null)
    } catch (err) {
      const { message, lists } = readErrors(err, fallback)
      setError(lists.final?.join(' ') ?? lists.reason?.join(' ') ?? message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="a-panel" aria-labelledby="fin-result">
      <div className="a-panel__head">
        <h2 id="fin-result" className="a-panel__title">
          Yekun hesablama
        </h2>
        <span className="a-muted">
          Kilidlədi: {f.juryLockedBy ?? '—'}, <span className="tabular">{formatDateTime(f.juryLockedAt)}</span>
        </span>
      </div>
      <p className="a-muted">
        Yekun xal = xalq xalı + münsif xalı (hər ikisi 10 → 1). Bərabərlikdə əvvəl xam münsif balı, sonra etibarlı səs
        sayı. Münsiflər: {c.jurorCount}, ən çox bal {c.juryMax}.
      </p>
      <div className="fin-scroll">
        <table className="a-table fin-table fin-result">
          <thead>
            <tr>
              <th scope="col">Yer</th>
              <th scope="col">İş</th>
              <th scope="col" className="fin-num">
                Etibarlı səs
              </th>
              <th scope="col" className="fin-num">
                Xalq yeri
              </th>
              <th scope="col" className="fin-num">
                Xalq xalı
              </th>
              <th scope="col" className="fin-num">
                Münsif balı
              </th>
              <th scope="col" className="fin-num">
                Münsif yeri
              </th>
              <th scope="col" className="fin-num">
                Münsif xalı
              </th>
              <th scope="col" className="fin-num">
                Yekun xal
              </th>
              <th scope="col" className="fin-num">
                Mükafat
              </th>
            </tr>
          </thead>
          <tbody>
            {c.rows.map((r) => (
              <tr key={r.submissionId} className={r.place !== null && r.place <= 3 ? 'fin-result__top' : undefined}>
                <td className="tabular">{r.place ?? `${r.rank}?`}</td>
                <td>
                  <span className="a-table__primary tabular">{r.reference}</span>
                  {r.title && <span className="a-table__sub">{r.title}</span>}
                  {TIE_NOTE[r.tie] && <span className={`fin-tie fin-tie--${r.tie}`}>{TIE_NOTE[r.tie]}</span>}
                </td>
                <td className="fin-num tabular">{r.validVotes}</td>
                <td className="fin-num tabular">{r.publicRank}</td>
                <td className="fin-num tabular">{r.publicPoints}</td>
                <td className="fin-num tabular">
                  {sumText(r.juryTotal)} / {c.juryMax}
                </td>
                <td className="fin-num tabular">{r.juryRank}</td>
                <td className="fin-num tabular">{r.juryPoints}</td>
                <td className="fin-num tabular">
                  <strong>{r.finalScore}</strong>
                </td>
                <td className="fin-num tabular">{r.prizeAzn ? `${r.prizeAzn} AZN` : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {c.unresolvedTies.map((t) => (
        <TieBreakForm key={t.works.map((w) => w.submissionId).join('-')} f={f} tie={t} onChange={onChange} />
      ))}
      {c.tieBreaks.length > 0 && (
        <div>
          <h3 className="fin-sub">Münsiflərin bərabərlik qərarları</h3>
          <ul className="fin-decisions">
            {c.tieBreaks.map((t) => (
              <li key={`${t.decidedAt}-${t.references.join()}`}>
                <span className="tabular">{t.references.join(' > ')}</span> — {t.reason}{' '}
                <span className="a-muted">
                  ({t.decidedBy ?? '—'}, <span className="tabular">{formatDateTime(t.decidedAt)}</span>)
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {published ? (
        <Notice tone="success">
          Rəsmi nəticə bu finaldan dərc olunub: {f.publishedBy ?? '—'}, <span className="tabular">{formatDateTime(f.publishedAt)}</span>.
          Final artıq dəyişdirilmir.
        </Notice>
      ) : (
        <>
          {c.publishProblems.length > 0 && (
            <Notice tone="warn">
              Rəsmi nəticə hələ dərc oluna bilməz:
              <ul>
                {c.publishProblems.map((x) => (
                  <li key={x}>{x}</li>
                ))}
              </ul>
            </Notice>
          )}
          <div className="a-actions">
            <button type="button" className="btn" disabled={busy} onClick={() => setDialog('unlock')}>
              Kilidi aç
            </button>
            <button
              type="button"
              className="btn btn--primary"
              disabled={busy || c.publishProblems.length > 0}
              onClick={() => setDialog('publish')}
            >
              Rəsmi nəticələri dərc et
            </button>
          </div>
        </>
      )}
      {error && !dialog && <Notice tone="error">{error}</Notice>}

      {dialog === 'publish' && (
        <ConfirmDialog
          title="Rəsmi nəticələr dərc olunsun?"
          confirmLabel="Dərc et"
          onConfirm={() => run(() => adminApi.publishFinal(f.competitionId), 'Dərc olunmadı.')}
          onCancel={() => setDialog(null)}
          busy={busy}
          error={error}
        >
          <p>1–3-cü yerlər bu hesablamadan rəsmi nəticəyə yazılır və dərhal saytda görünür:</p>
          <ol>
            {[1, 2, 3].map((p) => {
              const r = c.rows.find((x) => x.place === p)
              return r ? (
                <li key={p} className="tabular">
                  {r.reference} — {r.finalScore} xal, {r.prizeAzn} AZN
                </li>
              ) : null
            })}
          </ol>
          <p>Müəlliflərin adları və hər yerin xal izahı ictimai olacaq. Bundan sonra final dəyişdirilmir.</p>
        </ConfirmDialog>
      )}
      {dialog === 'unlock' && (
        <ConfirmDialog
          title="Münsif qiymətləndirməsinin kilidi açılsın?"
          confirmLabel="Kilidi aç"
          onConfirm={() => run(() => adminApi.unlockJury(f.competitionId, reason), 'Kilid açılmadı.')}
          onCancel={() => setDialog(null)}
          busy={busy}
          error={error}
          confirmDisabled={reason.trim().length < 5}
        >
          <p>Səbəb auditdə qeyd olunur.</p>
          <label className="fin-field">
            Səbəb
            <textarea rows={3} value={reason} onChange={(e) => setReason(e.target.value)} />
          </label>
        </ConfirmDialog>
      )}
    </section>
  )
}

/**
 * Works level on every rule where it touches a prize: the jurors decide their order, and the admin records exactly
 * that, with the jury's reason. Nothing here orders them by itself — the list starts in reference order only so it
 * has an order to start from.
 */
function TieBreakForm({ f, tie, onChange }: { f: AdminFinal; tie: FinalTieGroup; onChange: (f: AdminFinal) => void }) {
  const [order, setOrder] = useState(tie.works)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const move = (i: number, by: -1 | 1) =>
    setOrder((list) => {
      const next = [...list]
      ;[next[i], next[i + by]] = [next[i + by], next[i]]
      return next
    })

  const record = async () => {
    setBusy(true)
    setError(undefined)
    try {
      onChange(await adminApi.recordTieBreak(f.competitionId, order.map((w) => w.submissionId), reason))
    } catch (err) {
      const { message, fields } = readErrors(err, 'Qərar qeyd olunmadı.')
      setError(fields.reason ?? message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="fin-tiebreak" role="group" aria-label="Münsif qərarı tələb olunur">
      <h3 className="fin-sub">Münsif qərarı tələb olunur — {tie.rank}-ci yerdən</h3>
      <p className="a-muted">
        Bu işlər yekun xal, xam münsif balı və etibarlı səs sayı üzrə tam bərabərdir və mükafat yerinə toxunur. Münsiflərin
        verdiyi sıranı və səbəbi qeyd edin. Qərar qeyd olunana qədər Top 3 açılışı və dərc bağlıdır.
      </p>
      <ol className="fin-order">
        {order.map((w, i) => (
          <li key={w.submissionId}>
            <span className="tabular">{w.reference}</span> {w.title && <span className="a-muted">{w.title}</span>}
            <span className="fin-order__moves">
              <button type="button" className="btn btn--sm" disabled={i === 0} onClick={() => move(i, -1)} aria-label={`${w.reference} yuxarı`}>
                ↑
              </button>
              <button
                type="button"
                className="btn btn--sm"
                disabled={i === order.length - 1}
                onClick={() => move(i, 1)}
                aria-label={`${w.reference} aşağı`}
              >
                ↓
              </button>
            </span>
          </li>
        ))}
      </ol>
      <label className="fin-field">
        Münsiflərin səbəbi
        <textarea rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
      </label>
      <div className="a-actions">
        <button type="button" className="btn btn--primary" disabled={busy || reason.trim().length < 5} onClick={record}>
          Münsif qərarını qeyd et
        </button>
      </div>
      {error && <Notice tone="error">{error}</Notice>}
    </div>
  )
}
