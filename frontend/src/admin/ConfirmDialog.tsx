import { useEffect, useId, useRef, type ReactNode } from 'react'
import { createPortal } from 'react-dom'

/**
 * Modal confirmation for decisions that change what the public sees (winner, results). Focus moves into the
 * dialog (to "cancel", the safe choice), stays there, and returns to the opener on close; Escape cancels.
 */
export function ConfirmDialog({
  title,
  children,
  confirmLabel,
  onConfirm,
  onCancel,
  busy = false,
  confirmDisabled = false,
  error,
  tone = 'primary',
  wide = false,
}: {
  title: string
  children: ReactNode
  confirmLabel: string
  onConfirm: () => void
  onCancel: () => void
  busy?: boolean
  /** "danger" for something that cannot be undone: the confirm button is red, not the usual ink. */
  tone?: 'primary' | 'danger'
  /** The confirm button waits for a choice inside the dialog (cancel stays available). */
  confirmDisabled?: boolean
  error?: string
  /** A wider box, for a choice that needs room (e.g. the pages of a PDF side by side). */
  wide?: boolean
}) {
  const id = useId()
  const dialog = useRef<HTMLDivElement>(null)
  const cancel = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null
    // Without scrolling: a long dialog (on a phone, a sheet) opens at its title, not at the button.
    cancel.current?.focus({ preventScroll: true })
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.body.style.overflow = overflow
      opener?.focus?.()
    }
  }, [])

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape' && !busy) {
      e.stopPropagation()
      onCancel()
      return
    }
    if (e.key !== 'Tab' || !dialog.current) return
    const focusable = [...dialog.current.querySelectorAll<HTMLElement>('button:not([disabled]), a[href], textarea, input')]
    if (focusable.length === 0) return
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault()
      last.focus()
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault()
      first.focus()
    }
  }

  return createPortal(
    <div className="a-modal" onKeyDown={onKeyDown}>
      <div className="a-modal__backdrop" onClick={busy ? undefined : onCancel} aria-hidden="true" />
      <div
        ref={dialog}
        className={`a-modal__box${wide ? ' a-modal__box--wide' : ''}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-t`}
        aria-describedby={`${id}-b`}
      >
        <h2 id={`${id}-t`} className="a-modal__title">
          {title}
        </h2>
        <div id={`${id}-b`} className="a-modal__body">
          {children}
        </div>
        {error && (
          <p className="a-notice a-notice--error" role="alert">
            {error}
          </p>
        )}
        <div className="a-modal__actions">
          <button ref={cancel} type="button" className="btn btn--ghost" onClick={onCancel} disabled={busy}>
            Ləğv et
          </button>
          <button
            type="button"
            className={tone === 'danger' ? 'btn a-btn--danger-solid' : 'btn btn--primary'}
            onClick={onConfirm}
            disabled={busy || confirmDisabled}
          >
            {busy ? 'Gözləyin…' : confirmLabel}
          </button>
        </div>
      </div>
    </div>,
    document.body,
  )
}
