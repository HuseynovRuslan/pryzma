import { useState } from 'react'
import { Link } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDateTime } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { adminApi, type VotingLaunchCompetition, type VotingLaunchResult } from '../api'
import { ConfirmDialog } from '../ConfirmDialog'
import { readErrors } from '../errors'
import { Empty, Notice, PageHeader } from '../ui'

/** The input's own format (local time, minutes): a week from now at 20:00, the default close. */
function defaultEnd(now = new Date()): string {
  const d = new Date(now.getTime() + 7 * 86_400_000)
  d.setHours(20, 0, 0, 0)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

const ready = (c: VotingLaunchCompetition) => c.problems.length === 0

/**
 * "Səsverməni başlat": every competition that can be voted on, what its ballot will hold, and one button that opens
 * the chosen ones now, each on its own page, until the same moment. The site announces whatever is open by itself,
 * so nothing else has to be switched on. Before pressing it, the works are put right on the submissions screen:
 * duplicates rejected, the picture each card shows chosen.
 */
export default function VotingLaunchPage() {
  useTitle('Səsverməni başlat')
  const state = useApi((signal) => adminApi.votingLaunch(signal), [])
  const [chosen, setChosen] = useState<Set<number> | null>(null)
  const [endsAt, setEndsAt] = useState(defaultEnd)
  const [asking, setAsking] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [fieldError, setFieldError] = useState<string>()
  const [done, setDone] = useState<VotingLaunchResult | null>(null)

  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />

  const all = state.data.competitions
  // Until the admin touches a box, every competition that is ready is chosen.
  const selected = chosen ?? new Set(all.filter(ready).map((c) => c.id))
  const picked = all.filter((c) => selected.has(c.id) && ready(c))
  const toggle = (id: number) => {
    const next = new Set(selected)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    setChosen(next)
  }
  const end = endsAt ? new Date(endsAt) : null
  const endValid = end !== null && !Number.isNaN(end.getTime())

  const launch = async () => {
    if (!end) return
    setBusy(true)
    setError(undefined)
    setFieldError(undefined)
    try {
      const result = await adminApi.launchVoting({ competitionIds: picked.map((c) => c.id), endsAt: end.toISOString() })
      setDone(result)
      setAsking(false)
      setChosen(null)
      state.retry()
    } catch (err) {
      const { message, fields } = readErrors(err, 'Səsvermə başladılmadı. Bir az sonra yenidən cəhd edin.')
      setFieldError(fields.endsAt)
      setError(fields.endsAt ?? message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <PageHeader crumbs={[{ label: 'İcmal', to: '/admin' }, { label: 'Səsvermə' }]} title="Səsverməni başlat" />
      <div className="a-form" style={{ gap: 'var(--s-5)' }}>
        {done && (
          <Notice tone="success">
            <p>
              <strong>Səsvermə başladı.</strong> Bitir: {formatDateTime(done.endsAt)}. Sayt bütün səhifələrdə elan göstərir.
            </p>
            <ul className="a-launch__links">
              {done.competitions.map((c) => (
                <li key={c.id}>
                  <a href={`/competitions/${c.slug}/voting`} target="_blank" rel="noreferrer">
                    {c.title}
                  </a>{' '}
                  — səsvermədə {c.onBallot} iş{c.nominationCreated ? ' (“Bütün işlər” nominasiyası yaradıldı)' : ''}
                </li>
              ))}
            </ul>
          </Notice>
        )}

        <Notice tone="warn">
          Başlatmazdan əvvəl <Link to="/admin/submissions">Təkliflər</Link> bölməsində təkrar göndərilmiş işləri rədd edin və
          hər işin kartında görünəcək şəkli yoxlayın: səsvermə başlayandan sonra iş köçürülə bilmir.
        </Notice>

        {all.length === 0 && <Empty title="Səsverməyə hazır müsabiqə yoxdur" />}

        {all.map((c) => (
          <section key={c.id} className="a-panel a-launch" aria-labelledby={`launch-${c.id}`}>
            <label className="check a-launch__pick">
              <input
                type="checkbox"
                checked={selected.has(c.id) && ready(c)}
                disabled={!ready(c)}
                onChange={() => toggle(c.id)}
                aria-describedby={`launch-${c.id}-facts`}
              />
              <span id={`launch-${c.id}`} className="a-panel__title">
                {c.title}
              </span>
            </label>
            <p id={`launch-${c.id}-facts`} className="a-muted">
              {c.projectName} · göndərilmiş iş: {c.finalWorks} · indi səsvermədə: {c.onBallot}
              {c.votingState === 'Open' && c.votingEndsAt && <> · səsvermə gedir, bitir: {formatDateTime(c.votingEndsAt)}</>}
            </p>
            {c.createsNomination && (
              <p>
                Nominasiya yoxdur: başladanda “Bütün işlər” nominasiyası yaradılacaq və {c.finalWorks} iş ora yerləşəcək.
              </p>
            )}
            {c.problems.map((p) => (
              <Notice key={p} tone="error">
                {p}
              </Notice>
            ))}
            {c.warnings.map((w) => (
              <Notice key={w} tone="warn">
                {w}
              </Notice>
            ))}
            <p className="a-launch__links">
              <Link to={`/admin/submissions?competitionId=${c.id}`}>İşlərə bax</Link>
              {c.votingState === 'Open' && (
                <>
                  {' · '}
                  <a href={`/competitions/${c.slug}/voting`} target="_blank" rel="noreferrer">
                    Səsvermə səhifəsi
                  </a>
                </>
              )}
            </p>
          </section>
        ))}

        {all.some(ready) && (
          <div className="a-panel">
            <label className="a-field">
              <span className="a-field__label">Səsvermə nə vaxt bitsin?</span>
              <input
                className="input"
                type="datetime-local"
                value={endsAt}
                onChange={(e) => setEndsAt(e.target.value)}
                aria-invalid={fieldError ? true : undefined}
              />
              <span className="field__hint">Seçilən bütün müsabiqələr indi başlayır və bu vaxt birlikdə bitir.</span>
            </label>
            {error && !asking && <Notice tone="error">{error}</Notice>}
            <div className="a-actions">
              <button
                type="button"
                className="btn btn--primary"
                disabled={picked.length === 0 || !endValid}
                onClick={() => {
                  setError(undefined)
                  setAsking(true)
                }}
              >
                Səsverməni başlat
              </button>
            </div>
          </div>
        )}
      </div>

      {asking && end && (
        <ConfirmDialog
          title="Səsvermə indi başlasın?"
          confirmLabel="Bəli, başlat"
          busy={busy}
          error={error}
          onConfirm={launch}
          onCancel={() => setAsking(false)}
        >
          <p>Bu müsabiqələrdə səsvermə dərhal açılacaq və sayt bunu bütün səhifələrdə elan edəcək:</p>
          <ul>
            {picked.map((c) => (
              <li key={c.id}>
                <strong>{c.title}</strong> — {c.createsNomination ? c.finalWorks : c.onBallot} iş
              </li>
            ))}
          </ul>
          <p>Bitir: {formatDateTime(end.toISOString())}.</p>
        </ConfirmDialog>
      )}
    </>
  )
}
