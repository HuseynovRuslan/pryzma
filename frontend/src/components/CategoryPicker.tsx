import { forwardRef, useId, useMemo, useState } from 'react'
import type { Category } from '../api/types'
import { matchesSearch } from '../lib/text'
import { ordinal } from '../lib/format'
import { Check } from './Icons'
import './CategoryPicker.css'

interface Props {
  label: string
  categories: Category[]
  value: string | null
  onChange: (slug: string) => void
  error?: string
  disabled?: boolean
}

const SEARCH_THRESHOLD = 8

/**
 * Generic category selector: settlements today, genres or nominations tomorrow. Native radios underneath,
 * so arrow keys and screen readers work as a real radio group. Numbers show the official list order.
 */
export const CategoryPicker = forwardRef<HTMLFieldSetElement, Props>(function CategoryPicker(
  { label, categories, value, onChange, error, disabled = false },
  ref,
) {
  const id = useId()
  const [query, setQuery] = useState('')

  const numbered = useMemo(() => categories.map((c, i) => ({ ...c, n: i + 1 })), [categories])
  const visible = useMemo(() => numbered.filter((c) => matchesSearch(c.name, query)), [numbered, query])

  return (
    <fieldset
      ref={ref}
      className="picker"
      aria-describedby={error ? `${id}-err` : undefined}
      aria-invalid={error ? true : undefined}
      tabIndex={-1}
      disabled={disabled}
    >
      <legend className="visually-hidden">{label}</legend>

      {categories.length > SEARCH_THRESHOLD && (
        <div className="picker__search">
          <label htmlFor={`${id}-q`} className="visually-hidden">
            {label} arasında axtar
          </label>
          <span className="picker__search-icon" aria-hidden="true" />
          <input
            id={`${id}-q`}
            type="search"
            className="input picker__search-input"
            placeholder={`${label} arasında axtar`}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            autoComplete="off"
            autoCorrect="off"
            spellCheck={false}
            enterKeyHint="search"
          />
          <p className="picker__count" aria-live="polite">
            {query ? `${visible.length} nəticə` : `Cəmi ${categories.length}`}
          </p>
        </div>
      )}

      {error && (
        <p id={`${id}-err`} className="field__error picker__error" role="alert">
          {error}
        </p>
      )}

      {visible.length === 0 ? (
        <div className="picker__none">
          <p>“{query}” üzrə nəticə tapılmadı. Yazılışı yoxlayın və ya axtarışı təmizləyin.</p>
          <button type="button" className="btn btn--sm" onClick={() => setQuery('')}>
            Axtarışı təmizlə
          </button>
        </div>
      ) : (
        <ul role="list" className="picker__grid">
          {visible.map((c) => {
            const checked = c.slug === value
            return (
              <li key={c.id}>
                <label className={`picker__option${checked ? ' is-checked' : ''}`}>
                  <input
                    type="radio"
                    name={`${id}-category`}
                    value={c.slug}
                    checked={checked}
                    onChange={() => onChange(c.slug)}
                  />
                  <span className="picker__n tabular" aria-hidden="true">
                    {ordinal(c.n)}
                  </span>
                  <span className="picker__name">{c.name}</span>
                  {c.description && <span className="picker__desc">{c.description}</span>}
                  <Check size={16} className="picker__mark" />
                </label>
              </li>
            )
          })}
        </ul>
      )}
    </fieldset>
  )
})
