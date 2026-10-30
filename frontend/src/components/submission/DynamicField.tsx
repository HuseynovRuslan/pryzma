import { useId } from 'react'
import type { FormField } from '../../api/types'
import { FieldError } from '../Form'

interface Props {
  field: FormField
  value: string | null
  error?: string
  onChange: (value: string | null) => void
  onBlur?: () => void
}

/** Renders one admin-defined field. File fields are handled by FileField. */
export function DynamicField({ field, value, error, onChange, onBlur }: Props) {
  const id = useId()
  const hintId = field.helpText ? `${id}-hint` : undefined
  const errId = error ? `${id}-err` : undefined
  const describedBy = [hintId, errId].filter(Boolean).join(' ') || undefined
  const text = value ?? ''
  const common = {
    id,
    name: field.key,
    'aria-invalid': error ? true : undefined,
    'aria-describedby': describedBy,
    'aria-required': field.isRequired || undefined,
    onBlur,
  }

  if (field.fieldType === 'Checkbox') {
    return (
      <div className="dfield">
        <label className="check">
          <input
            type="checkbox"
            {...common}
            checked={value === 'true'}
            onChange={(e) => onChange(e.target.checked ? 'true' : 'false')}
          />
          <span>
            {field.label}
            {!field.isRequired && <span className="dfield__optional"> (istəyə bağlı)</span>}
          </span>
        </label>
        {field.helpText && <p id={hintId} className="field__hint">{field.helpText}</p>}
        {error && <FieldError id={errId} message={error} />}
      </div>
    )
  }

  const counter =
    field.maxLength && (field.fieldType === 'ShortText' || field.fieldType === 'LongText') ? (
      <span className={`dfield__count tabular${text.length > field.maxLength ? ' is-over' : ''}`} aria-live="off">
        {text.length} / {field.maxLength}
      </span>
    ) : null

  return (
    <div className="dfield">
      <div className="dfield__head">
        <label htmlFor={id} className="dfield__label">
          {field.label}
          {!field.isRequired && <span className="dfield__optional"> (istəyə bağlı)</span>}
        </label>
        {counter}
      </div>
      {field.helpText && (
        <p id={hintId} className="field__hint">
          {field.helpText}
        </p>
      )}

      {field.fieldType === 'LongText' ? (
        <textarea
          {...common}
          className="input dfield__textarea"
          rows={5}
          placeholder={field.placeholder ?? undefined}
          value={text}
          onChange={(e) => onChange(e.target.value)}
        />
      ) : field.fieldType === 'Select' ? (
        <select {...common} className="input dfield__select" value={text} onChange={(e) => onChange(e.target.value || null)}>
          <option value="">Seçin</option>
          {field.options?.map((o) => (
            <option key={o} value={o}>
              {o}
            </option>
          ))}
        </select>
      ) : (
        <input
          {...common}
          className="input"
          type={field.fieldType === 'Date' ? 'date' : field.fieldType === 'Url' ? 'url' : 'text'}
          inputMode={field.fieldType === 'Number' ? 'decimal' : field.fieldType === 'Url' ? 'url' : undefined}
          placeholder={field.placeholder ?? undefined}
          value={text}
          onChange={(e) => onChange(e.target.value)}
          enterKeyHint="next"
        />
      )}
      {error && <FieldError id={errId} message={error} />}
    </div>
  )
}
