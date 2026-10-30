import { useId, useState } from 'react'
import { formatDateTime } from '../../lib/format'
import { placeName } from '../../lib/text'
import { useApi } from '../../lib/useApi'
import { catalogNumber } from '../../pages/voting/lib'
import { adminApi, type AdminAwardPodium, type AdminAwards, type AdminPlaceCandidate, type AwardCategory } from '../api'
import { ConfirmDialog } from '../ConfirmDialog'
import { readErrors } from '../errors'
import { Badge, Notice } from '../ui'
import './awards-admin.css'

const PODIUMS: { category: AwardCategory; path: 'jury' | 'public'; title: string; unit: string }[] = [
  { category: 'Jury', path: 'jury', title: 'Münsiflərin seçimi', unit: 'bal' },
  { category: 'Public', path: 'public', title: 'Xalqın seçimi', unit: 'səs' },
]

interface Row {
  submissionId: number | null
  participantName: string
  points: string
}

/**
 * The two independent podiums of the result (owner, 16 October 2026) — the jury's choice with the points of its own
 * protocol, the public's choice with the valid votes — three places each, saved whole and checked by the server. They
 * are never combined and no overall winner is derived; both go public only with "Nəticəni dərc et".
 */
export function AwardsPanel({ competitionId, onChanged }: { competitionId: number; onChanged: () => Promise<void> }) {
  const state = useApi((signal) => adminApi.awards(competitionId, signal), [competitionId])
  const [data, setData] = useState<AdminAwards>()
  if (state.status === 'loading') return <p className="a-muted">Yüklənir…</p>
  if (state.status === 'error') return <Notice tone="error">{state.error.message}</Notice>
  const a = data ?? state.data
  const update = async (next: AdminAwards) => {
    setData(next)
    await onChanged()
  }

  return (
    <section className="a-panel" aria-labelledby="awards-title">
      <div className="a-panel__head">
        <h2 id="awards-title" className="a-panel__title">
          İki kateqoriyalı nəticə
        </h2>
        <Badge tone={a.resultPublished ? 'live' : 'draft'}>{a.resultPublished ? 'Dərc olunub' : 'Dərc olunmayıb'}</Badge>
      </div>
      <p className="a-muted">
        Münsiflərin seçimi və xalqın seçimi ayrıca göstərilir: birləşdirilmir, ümumi qalib müəyyən edilmir, mükafat
        göstərilmir. Hər podium üç yerdir. İctimai səhifədə yalnız “Nəticəni dərc et”dən sonra görünür.
      </p>
      {PODIUMS.map((p) => (
        <PodiumEditor key={p.category} podium={p} awards={a} onSaved={update} />
      ))}
    </section>
  )
}

function PodiumEditor({
  podium,
  awards,
  onSaved,
}: {
  podium: (typeof PODIUMS)[number]
  awards: AdminAwards
  onSaved: (a: AdminAwards) => Promise<void>
}) {
  const id = useId()
  const saved: AdminAwardPodium = podium.category === 'Jury' ? awards.jury : awards.public
  const isPublic = podium.category === 'Public'
  const [rows, setRows] = useState<Row[] | null>(null)
  const [source, setSource] = useState('')
  const [confirming, setConfirming] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const byId = new Map(awards.candidates.map((c) => [c.submissionId, c]))
  const current = [1, 2, 3].map((place) => saved.places.find((x) => x.place === place)?.submissionId ?? null)

  const edit = () => {
    setRows(
      [1, 2, 3].map((place) => {
        const x = saved.places.find((p) => p.place === place)
        return { submissionId: x?.submissionId ?? null, participantName: x?.participantName ?? '', points: x ? String(x.points) : '' }
      }),
    )
    setSource(saved.source ?? '')
    setError(undefined)
  }
  const fillFromVotes = () => {
    setRows(awards.candidates.slice(0, 3).map((c) => ({ submissionId: c.submissionId, participantName: c.participantName, points: String(c.voteCount) })))
  }
  const choose = (i: number, submissionId: number | null) =>
    setRows((all) =>
      all!.map((r, k) => {
        if (k !== i) return r
        const c = submissionId ? byId.get(submissionId) : undefined
        return {
          submissionId,
          participantName: r.participantName && r.submissionId === submissionId ? r.participantName : (c?.participantName ?? ''),
          points: isPublic ? (c ? String(c.voteCount) : '') : r.points,
        }
      }),
    )
  const save = async () => {
    if (!rows) return
    setBusy(true)
    setError(undefined)
    try {
      const places = rows.map((r) => ({ submissionId: r.submissionId ?? 0, participantName: r.participantName, points: Number(r.points) }))
      await onSaved(await adminApi.setAwards(awards.competitionId, podium.path, places, current, isPublic ? null : source, awards.resultPublished))
      setRows(null)
      setConfirming(false)
    } catch (err) {
      setError(readErrors(err, 'Yadda saxlanmadı.').message)
      setConfirming(false)
    } finally {
      setBusy(false)
    }
  }
  const clear = async () => {
    setBusy(true)
    setError(undefined)
    try {
      await onSaved(await adminApi.clearAwards(awards.competitionId, podium.path))
    } catch (err) {
      setError(readErrors(err, 'Silinmədi.').message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="a-award" aria-labelledby={`${id}-title`} role="group">
      <div className="a-panel__head">
        <h3 id={`${id}-title`} className="fin-sub">
          {podium.title}
        </h3>
        {!rows && (
          <span className="a-actions">
            <button type="button" className="btn btn--sm" onClick={edit} disabled={isPublic && awards.voting !== 'Closed'}>
              {saved.places.length > 0 ? 'Dəyiş' : 'Yerləri qeyd et'}
            </button>
            {saved.places.length > 0 && !awards.resultPublished && (
              <button type="button" className="btn btn--sm a-btn--danger" onClick={clear} disabled={busy}>
                Sil
              </button>
            )}
          </span>
        )}
      </div>
      {isPublic && awards.voting !== 'Closed' && <p className="a-muted">Xalqın seçimi səsvermə başa çatdıqdan sonra qeyd olunur.</p>}

      {!rows ? (
        saved.places.length === 0 ? (
          <p className="a-muted">Hələ qeyd olunmayıb.</p>
        ) : (
          <>
            <ol className="a-award__list" role="list">
              {saved.places.map((x) => (
                <li key={x.place}>
                  <span className="a-award__place">{placeName(x.place)}</span>
                  <span className="tabular">{catalogNumber(x.reference)}</span> — {x.title}
                  <span className="a-award__name">
                    {x.participantName}
                    {x.accountName.trim() !== x.participantName && <span className="a-muted"> (hesabda: {x.accountName})</span>}
                  </span>
                  <strong className="tabular">
                    {x.points} {podium.unit}
                  </strong>
                  {isPublic && x.validVotes !== x.points && (
                    <span className="a-award__stale"> İndi {x.validVotes} etibarlı səs — yeniləyin.</span>
                  )}
                </li>
              ))}
            </ol>
            <p className="a-muted">
              Mənbə: {saved.source}
              {saved.recordedAt && ` · ${formatDateTime(saved.recordedAt)}${saved.recordedBy ? ` · ${saved.recordedBy}` : ''}`}
            </p>
          </>
        )
      ) : (
        <div className="a-form">
          {isPublic && (
            <button type="button" className="btn btn--sm" onClick={fillFromVotes}>
              Etibarlı səs sıralamasından doldur
            </button>
          )}
          {rows.map((r, i) => (
            <fieldset key={i} className="a-award__row">
              <legend>{placeName(i + 1)}</legend>
              <label className="a-field">
                <span className="a-field__label">İş</span>
                <select
                  value={r.submissionId ?? ''}
                  onChange={(e) => choose(i, e.target.value ? Number(e.target.value) : null)}
                  aria-label={`${placeName(i + 1)} — iş`}
                >
                  <option value="">Seçin…</option>
                  {awards.candidates.map((c: AdminPlaceCandidate) => (
                    <option key={c.submissionId} value={c.submissionId}>
                      {catalogNumber(c.reference)} — {c.title} ({c.voteCount} səs)
                    </option>
                  ))}
                </select>
              </label>
              <label className="a-field">
                <span className="a-field__label">Elan olunan ad</span>
                <input
                  className="input"
                  value={r.participantName}
                  maxLength={120}
                  onChange={(e) => setRows((all) => all!.map((x, k) => (k === i ? { ...x, participantName: e.target.value } : x)))}
                  aria-label={`${placeName(i + 1)} — elan olunan ad`}
                />
              </label>
              <label className="a-field">
                <span className="a-field__label">{isPublic ? 'Etibarlı səs (serverdən)' : 'Bal'}</span>
                <input
                  className="input tabular"
                  inputMode="numeric"
                  value={r.points}
                  readOnly={isPublic}
                  onChange={(e) => setRows((all) => all!.map((x, k) => (k === i ? { ...x, points: e.target.value } : x)))}
                  aria-label={`${placeName(i + 1)} — ${isPublic ? 'etibarlı səs' : 'bal'}`}
                />
              </label>
            </fieldset>
          ))}
          {!isPublic && (
            <label className="a-field">
              <span className="a-field__label">Balların mənbəyi (ictimai deyil)</span>
              <input className="input" value={source} maxLength={300} placeholder="məs. Kağız protokol" onChange={(e) => setSource(e.target.value)} />
            </label>
          )}
          {error && <Notice tone="error">{error}</Notice>}
          <div className="a-actions">
            <button
              type="button"
              className="btn btn--primary"
              disabled={busy}
              onClick={() => (awards.resultPublished ? setConfirming(true) : void save())}
            >
              {busy ? 'Saxlanılır…' : 'Yadda saxla'}
            </button>
            <button type="button" className="btn" disabled={busy} onClick={() => setRows(null)}>
              Ləğv et
            </button>
          </div>
        </div>
      )}
      {!rows && error && <Notice tone="error">{error}</Notice>}
      {confirming && (
        <ConfirmDialog
          title="Dərc olunmuş nəticə dəyişir"
          confirmLabel="Bəli, dəyiş"
          busy={busy}
          onCancel={() => setConfirming(false)}
          onConfirm={save}
        >
          <p>Nəticə artıq dərc olunub: bu dəyişiklik saytda dərhal görünəcək.</p>
        </ConfirmDialog>
      )}
    </div>
  )
}
