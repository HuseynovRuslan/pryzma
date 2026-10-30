import { cloneElement, useId, type ReactElement, type ReactNode } from 'react'

/** Label + control + hint + error, wired for screen readers. The child control receives id/aria props. */
export function AField({
  label,
  hint,
  error,
  children,
}: {
  label: string
  hint?: ReactNode
  error?: string
  children: ReactElement<Record<string, unknown>>
}) {
  const id = useId()
  const hintId = hint ? `${id}-hint` : undefined
  const errorId = error ? `${id}-error` : undefined
  const control = cloneElement(children, {
    id,
    'aria-invalid': error ? true : undefined,
    'aria-describedby': [hintId, errorId].filter(Boolean).join(' ') || undefined,
  })
  return (
    <div className="a-field">
      <label htmlFor={id} className="a-field__label">
        {label}
      </label>
      {control}
      {hint && (
        <p id={hintId} className="a-field__hint">
          {hint}
        </p>
      )}
      {error && (
        <p id={errorId} className="a-field__error">
          {error}
        </p>
      )}
    </div>
  )
}
