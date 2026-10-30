import { useId, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { azAccusative, matchesSearch } from '../../lib/text'
import './GalleryFilter.css'

interface Props {
  /** The competition's own name for its categories ("Qəsəbələr", "Nominasiyalar"…). */
  label: string
  categories: { slug: string; name: string; count: number }[]
  selected: string | null
  total: number
  /** The query parameter the choice travels in — `category` in the gallery, `nomination` on the ballot. */
  param?: string
  /** Wording for the "everything" chip, when "Bütün {label}" does not read well. */
  allLabel?: string
  /**
   * On a phone, open a few rows deep and put a button under them for the rest. For a list long enough that a
   * reader scrolls past it to reach what they came for; the gallery leaves it off and keeps its own box.
   */
  collapsible?: boolean
}

const SEARCH_THRESHOLD = 8

/** How many chips a phone opens with, counting the "everything" one. GalleryFilter.css hides the rest. */
const COLLAPSED_CHIPS = 6

/**
 * Category filter for the gallery: links (shareable, back-button friendly), a tolerant search for long lists
 * (mastaga → Maştağa, suvelan → Şüvəlan) and counts — never a 59-item dropdown.
 */
export function GalleryFilter({
  label,
  categories,
  selected,
  total,
  param = 'category',
  allLabel,
  collapsible = false,
}: Props) {
  const id = useId()
  const [params] = useSearchParams()
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  const visible = useMemo(() => categories.filter((c) => matchesSearch(c.name, query)), [categories, query])
  const lower = label.toLocaleLowerCase('az')
  // Nothing to ask for when everything already fits in the rows a phone opens with.
  const hasMore = collapsible && visible.length + 1 > COLLAPSED_CHIPS

  // Every other parameter on the address survives the choice — a search term, above all — and paging starts again.
  const href = (slug: string | null) => {
    const next = new URLSearchParams(params)
    next.delete('page')
    if (slug === null) next.delete(param)
    else next.set(param, slug)
    const qs = next.toString()
    return qs ? `?${qs}` : '?'
  }

  return (
    <nav
      className={`gfilter${collapsible ? ' gfilter--collapsible' : ''}${hasMore && !open ? ' is-collapsed' : ''}`}
      aria-label={`${label} üzrə süz`}
    >
      {categories.length > SEARCH_THRESHOLD && (
        <div className="gfilter__search">
          <label htmlFor={`${id}-q`} className="gfilter__label">
            {label} arasında axtar
          </label>
          <input
            id={`${id}-q`}
            type="search"
            className="input"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="məs. mastaga"
            autoComplete="off"
            spellCheck={false}
            enterKeyHint="search"
          />
          <p className="gfilter__count" aria-live="polite">
            {query ? `${visible.length} nəticə` : `${categories.length} ${lower} üzrə iş var`}
          </p>
        </div>
      )}
      <ul role="list" id={`${id}-chips`} className="gfilter__chips">
        <li>
          <Link to={href(null)} replace preventScrollReset className="gchip" aria-current={selected === null ? 'true' : undefined}>
            {allLabel ?? `Bütün ${lower}`}
            <span className="gchip__n tabular">{total}</span>
          </Link>
        </li>
        {visible.map((c) => (
          <li key={c.slug}>
            <Link
              to={href(c.slug)}
              replace
              preventScrollReset
              className="gchip"
              aria-current={selected === c.slug ? 'true' : undefined}
            >
              {c.name}
              <span className="gchip__n tabular">{c.count}</span>
            </Link>
          </li>
        ))}
      </ul>
      {hasMore && (
        <button
          type="button"
          className="gfilter__toggle btn btn--sm"
          aria-expanded={open}
          aria-controls={`${id}-chips`}
          onClick={() => setOpen((v) => !v)}
        >
          {open ? 'Daha az göstər' : `Bütün ${azAccusative(lower)} göstər`}
        </button>
      )}
      {visible.length === 0 && (
        <p className="gfilter__none">
          “{query}” üzrə nəticə yoxdur.{' '}
          <button type="button" className="btn btn--sm btn--ghost" onClick={() => setQuery('')}>
            Axtarışı təmizlə
          </button>
        </p>
      )}
    </nav>
  )
}
