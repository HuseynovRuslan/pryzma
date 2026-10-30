import { useState } from 'react'
import { adminApi, type SubmissionMoveOptions, type SubmissionMoved } from './api'
import { ConfirmDialog } from './ConfirmDialog'
import { readErrors } from './errors'
import { Notice } from './ui'

/**
 * "Başqa müsabiqəyə köçür": a work an entrant sent to the wrong competition, moved to the right one. It keeps its
 * reference, answers and files; the server says which competitions can take it and why the others cannot, and refuses
 * a work that already has votes, jury scores or a prize where it is.
 */
export function MovePanel({ submissionId, onMoved }: { submissionId: number; onMoved: (moved: SubmissionMoved) => void }) {
  const [options, setOptions] = useState<SubmissionMoveOptions | null>(null)
  const [loading, setLoading] = useState(false)
  const [target, setTarget] = useState<number | null>(null)
  const [asking, setAsking] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  const open = async () => {
    setLoading(true)
    setError(undefined)
    try {
      setOptions(await adminApi.submissionMoveOptions(submissionId))
    } catch (err) {
      setError(readErrors(err, 'Köçürmə imkanları yüklənmədi.').message)
    } finally {
      setLoading(false)
    }
  }

  const chosen = options?.targets.find((t) => t.id === target) ?? null

  const move = async () => {
    if (!chosen) return
    setBusy(true)
    setError(undefined)
    try {
      const moved = await adminApi.moveSubmission(submissionId, chosen.id)
      setAsking(false)
      setOptions(null)
      setTarget(null)
      onMoved(moved)
    } catch (err) {
      setError(readErrors(err, 'Təklif köçürülmədi.').message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="a-panel" aria-labelledby="move-title">
      <h2 id="move-title" className="a-panel__title">
        Başqa müsabiqəyə köçür
      </h2>
      <p className="a-muted">
        İştirakçı işi səhv müsabiqəyə göndəribsə. İstinad nömrəsi, status, cavablar və fayllar saxlanılır; cavablar yeni
        müsabiqənin formasında eyni açarlı sahələrə keçir.
      </p>
      {error && !asking && <Notice tone="error">{error}</Notice>}
      {!options && (
        <div className="a-actions">
          <button type="button" className="btn btn--sm" onClick={open} disabled={loading}>
            {loading ? 'Yüklənir…' : 'Köçürməyə başla'}
          </button>
        </div>
      )}
      {options && options.blockers.length > 0 && (
        <Notice tone="error">
          Bu təklifi köçürmək olmaz: {options.blockers.join(' ')}
        </Notice>
      )}
      {options && options.blockers.length === 0 && (
        <>
          <label className="a-field">
            <span className="a-field__label">Hansı müsabiqəyə?</span>
            <select
              className="input"
              value={target ?? ''}
              onChange={(e) => setTarget(e.target.value ? Number(e.target.value) : null)}
            >
              <option value="">Seçin</option>
              {options.targets.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.title}
                  {t.problems.length > 0 ? ' — uyğun deyil' : ''}
                </option>
              ))}
            </select>
          </label>
          {chosen && chosen.problems.length > 0 && <Notice tone="warn">{chosen.problems.join(' ')}</Notice>}
          <div className="a-actions">
            <button
              type="button"
              className="btn btn--sm btn--primary"
              disabled={!chosen || chosen.problems.length > 0}
              onClick={() => setAsking(true)}
            >
              Köçür
            </button>
            <button type="button" className="btn btn--sm btn--ghost" onClick={() => setOptions(null)}>
              Ləğv et
            </button>
          </div>
        </>
      )}
      {asking && chosen && options && (
        <ConfirmDialog
          title="Təklif köçürülsün?"
          confirmLabel="Bəli, köçür"
          busy={busy}
          error={error}
          onConfirm={move}
          onCancel={() => setAsking(false)}
        >
          <p>
            <strong>{options.reference}</strong>: {options.competitionTitle} → <strong>{chosen.title}</strong>
          </p>
          <p>İstinad nömrəsi, cavablar və fayllar dəyişmir. Köçürmə tarixçəyə yazılır.</p>
        </ConfirmDialog>
      )}
    </section>
  )
}
