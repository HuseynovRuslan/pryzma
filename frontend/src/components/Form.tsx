import { useId, useState, type InputHTMLAttributes, type ReactNode } from 'react'

interface FieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id'> {
  label: string
  error?: string
  hint?: ReactNode
}

export function TextField({ label, error, hint, ...input }: FieldProps) {
  const id = useId()
  const describedBy = [hint ? `${id}-hint` : null, error ? `${id}-err` : null].filter(Boolean).join(' ') || undefined
  return (
    <div className="field">
      <label htmlFor={id} className="field__label">
        {label}
      </label>
      <input
        id={id}
        className="input"
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        {...input}
      />
      {hint && (
        <p id={`${id}-hint`} className="field__hint">
          {hint}
        </p>
      )}
      {error && <FieldError id={`${id}-err`} message={error} />}
    </div>
  )
}

export function PasswordField({ label, error, hint, ...input }: FieldProps) {
  const id = useId()
  const [visible, setVisible] = useState(false)
  const describedBy = [hint ? `${id}-hint` : null, error ? `${id}-err` : null].filter(Boolean).join(' ') || undefined
  return (
    <div className="field">
      <label htmlFor={id} className="field__label">
        {label}
      </label>
      <div className="input-wrap">
        <input
          id={id}
          className="input"
          type={visible ? 'text' : 'password'}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy}
          {...input}
        />
        <button
          type="button"
          className="input-wrap__toggle"
          aria-pressed={visible}
          aria-controls={id}
          onClick={() => setVisible((v) => !v)}
        >
          {visible ? 'Gizlət' : 'Göstər'}
        </button>
      </div>
      {hint && (
        <p id={`${id}-hint`} className="field__hint">
          {hint}
        </p>
      )}
      {error && <FieldError id={`${id}-err`} message={error} />}
    </div>
  )
}

export function FieldError({ id, message }: { id?: string; message: string }) {
  return (
    <p id={id} className="field__error">
      {message}
    </p>
  )
}

export function SubmitButton({ busy, children, className = '' }: { busy: boolean; children: ReactNode; className?: string }) {
  return (
    <button type="submit" className={`btn btn--primary btn--lg ${className}`} disabled={busy} aria-busy={busy}>
      {busy && <span className="btn__spinner" aria-hidden="true" />}
      {children}
    </button>
  )
}
