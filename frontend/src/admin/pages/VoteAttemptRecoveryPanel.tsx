import { useState } from 'react'
import { useApi } from '../../lib/useApi'
import { formatDateTime } from '../../lib/format'
import { ApiError } from '../../api/client'
import { adminApi } from '../api'
import { ConfirmDialog } from '../ConfirmDialog'
import { Notice } from '../ui'

/**
 * Only server-recorded, authenticated vote attempts which have no Vote row are offered.
 * The list is NOT an audit browser; administrative recovery is logged internally by the API.
 */
export function VoteAttemptRecoveryPanel({
  competitionId,
  onChanged,
}: {
  competitionId: number
  onChanged: () => void
}) {
  const [version, setVersion] = useState(0)
  const response = useApi(
    (signal) => adminApi.recoveryCandidates(competitionId, signal),
    [competitionId, version],
  )
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [confirm, setConfirm] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [done, setDone] = useState<string>()

  // No audit section and no interface change when there is nothing to reconcile.
  if (response.status === 'loading') return null
  if (response.status === 'error') return null
  const candidates = response.data.items
  if (candidates.length === 0 && !done) return null

  const recover = async () => {
    if (!selected.size) return
    setBusy(true)
    setError(undefined)
    try {
      const result = await adminApi.recoverMissingVotes(competitionId, [...selected])
      setDone(`${result.recovered} qeydə alınmamış səs bərpa edildi.`)
      setSelected(new Set())
      setConfirm(false)
      setVersion((n) => n + 1)
      onChanged()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Bərpa mümkün olmadı. Səhifəni yeniləyin.')
      setVersion((n) => n + 1)
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="a-panel">
      <div className="a-panel__head">
        <h3 className="a-panel__title">Bazaya düşməmiş səslər</h3>
      </div>
      <p className="a-muted">
        Yalnız serverdə təsdiqlənmiş səs cəhdləri göstərilir.
        Eyni istifadəçinin başqa səsi varsa, bu siyahıya düşmür.
      </p>
      {done && <Notice tone="info">{done}</Notice>}
      {candidates.length > 0 && (
        <>
          <ul className="a-review__list" role="list">
            {candidates.map((c) => (
              <li key={c.attemptId}>
                <label className="a-field" style={{ flexDirection: 'row', alignItems: 'center', gap: 'var(--s-3)' }}>
                  <input
                    type="checkbox"
                    checked={selected.has(c.attemptId)}
                    onChange={() => setSelected((prev) => {
                      const next = new Set(prev)
                      if (next.has(c.attemptId)) next.delete(c.attemptId)
                      else next.add(c.attemptId)
                      return next
                    })}
                  />
                  <span>{c.reference} · {formatDateTime(c.attemptedAt)}</span>
                </label>
              </li>
            ))}
          </ul>
          <button className="btn btn--primary" type="button" disabled={selected.size === 0}
            onClick={() => setConfirm(true)}>
            Seçilmiş {selected.size} səsi bərpa et
          </button>
        </>
      )}
      {confirm && (
        <ConfirmDialog
          title="Səslərin bərpası"
          confirmLabel={`${selected.size} səsi bərpa et`}
          busy={busy}
          error={error}
          onConfirm={recover}
          onCancel={() => { setConfirm(false); setError(undefined) }}
        >
          <p>
            Yalnız serverdə qeydə alınmış, lakin səs cədvəlində olmayan
            {selected.size} səs cəhdi yoxlanılıb bərpa ediləcək.
            Bu əməliyyat daxili jurnalda qeyd olunur.
          </p>
        </ConfirmDialog>
      )}
    </section>
  )
}
