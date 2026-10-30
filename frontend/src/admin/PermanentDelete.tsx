import { useState } from 'react'
import type { DeleteImpact } from './api'
import { ConfirmDialog } from './ConfirmDialog'
import { readErrors } from './errors'
import { AField } from './forms'

/** The word an admin types before anything with dependants is destroyed. */
export const CONFIRM_WORD = 'SİL'

/** Why an account that voted stays — the server's own refusal (PermanentDeletion.VotedAccount), word for word. */
export const VOTED_ACCOUNT = 'Bu hesab səsvermədə iştirak etdiyi üçün həmişəlik silinə bilməz.'

interface Props {
  /** "layihəni", "müsabiqəni", "təklifi" — what the sentences are about. */
  noun: string
  /** Counts the server works out; asked for when the dialog opens, never guessed here. */
  loadImpact: () => Promise<DeleteImpact>
  onDelete: () => Promise<unknown>
  /** Runs after the server confirms — normally navigating away from what no longer exists. */
  onDeleted: () => void
}

/**
 * The destructive half of the admin's "Silmək" panel. Nothing happens on the first click: the dialog opens, the
 * server is asked what would go, and the numbers it answers are shown. Where anything at all depends on the
 * entity, the button stays disabled until the admin types SİL. Nothing is removed from the screen on hope —
 * only the server's answer moves the page on; a failure leaves everything where it was and says why.
 */
export function PermanentDelete({ noun, loadImpact, onDelete, onDeleted }: Props) {
  const [open, setOpen] = useState(false)
  const [impact, setImpact] = useState<DeleteImpact>()
  const [typed, setTyped] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  const ask = async () => {
    setOpen(true)
    setImpact(undefined)
    setTyped('')
    setError(undefined)
    try {
      setImpact(await loadImpact())
    } catch (e) {
      setError(readErrors(e, 'Məlumatı yükləmək alınmadı.').message)
    }
  }

  const close = () => {
    setOpen(false)
    setImpact(undefined)
    setTyped('')
    setError(undefined)
  }

  const confirm = async () => {
    setBusy(true)
    setError(undefined)
    try {
      await onDelete()
      onDeleted()
    } catch (e) {
      setError(readErrors(e, 'Silmək alınmadı.').message)
    } finally {
      setBusy(false)
    }
  }

  const needsWord = impact?.requiresTypedConfirmation ?? true
  const lines = impact ? countLines(impact) : []

  return (
    <>
      <button type="button" className="btn btn--sm a-btn--danger" onClick={ask}>
        Həmişəlik sil
      </button>
      <p className="a-muted">Geri qaytarmaq mümkün deyil. Məlumat lazım ola bilərsə, {noun} arxivləşdirin.</p>

      {open && (
        <ConfirmDialog
          title="Həmişəlik sil?"
          confirmLabel="Həmişəlik sil"
          tone="danger"
          busy={busy}
          confirmDisabled={!impact || (needsWord && typed.trim().toLocaleUpperCase('az') !== CONFIRM_WORD)}
          error={error}
          onCancel={close}
          onConfirm={confirm}
        >
          {!impact && !error && <p>Yoxlanılır…</p>}
          {impact && (
            <div className="a-delete">
              <p>
                <strong>{impact.title}</strong>
                {impact.context && <span className="a-muted"> — {impact.context}</span>}
              </p>
              {lines.length > 0 ? (
                <>
                  <p>Bununla birlikdə silinəcək:</p>
                  <ul className="a-delete__list">
                    {lines.map((line) => (
                      <li key={line}>{line}</li>
                    ))}
                  </ul>
                </>
              ) : (
                <p>Buna bağlı başqa məlumat yoxdur.</p>
              )}
              <p className="a-delete__warn">Bu əməliyyat geri qaytarıla bilməz.</p>
              <p className="a-muted">İştirakçıların hesabları silinmir.</p>
              {needsWord && (
                <AField label={`Təsdiq üçün ${CONFIRM_WORD} yazın`}>
                  <input
                    className="input"
                    value={typed}
                    onChange={(e) => setTyped(e.target.value)}
                    disabled={busy}
                    autoComplete="off"
                  />
                </AField>
              )}
            </div>
          )}
        </ConfirmDialog>
      )}
    </>
  )
}

/** Only what there actually is: a zero is not worth a line in a dialog someone is reading carefully. */
function countLines(i: DeleteImpact): string[] {
  const parts: [number, string][] = [
    [i.competitions, 'müsabiqə'],
    [i.submissions, 'təklif'],
    [i.categories, 'kateqoriya'],
    [i.fields, 'forma sahəsi'],
    [i.answers, 'cavab'],
    [i.files, 'yüklənmiş fayl'],
    [i.results, 'nəticə'],
    [i.winners, 'qalib qeydi'],
    [i.notes, 'admin qeydi'],
  ]
  return parts.filter(([n]) => n > 0).map(([n, label]) => `${n} ${label}`)
}
