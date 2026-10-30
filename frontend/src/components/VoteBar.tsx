import './VoteBar.css'

/**
 * A work's public vote as one bar: green for its valid votes, red for the votes an approved audit decision set aside.
 * Its length is the work's votes against `max` (the most-voted work's, in a list); without `max` the bar is full and
 * shows only the share. Nothing else is drawn — no "pending review" state is recorded anywhere, so none is shown.
 */
export function VoteBar({ valid, invalid, max, className }: { valid: number; invalid: number; max?: number; className?: string }) {
  const total = valid + invalid
  const width = total === 0 ? 0 : max ? Math.max((total / Math.max(max, total)) * 100, 2) : 100
  return (
    <span
      className={`vbar${className ? ` ${className}` : ''}`}
      role="img"
      aria-label={`${valid} etibarlı səs${invalid > 0 ? `, ${invalid} etibarsız sayılıb` : ''}`}
    >
      <span className="vbar__fill" style={{ width: `${width}%` }}>
        {valid > 0 && <span className="vbar__valid" style={{ flexGrow: valid }} />}
        {invalid > 0 && <span className="vbar__invalid" style={{ flexGrow: invalid }} />}
      </span>
    </span>
  )
}

/** The bar's key: what green and red mean. */
export function VoteBarKey({ className }: { className?: string }) {
  return (
    <span className={`vbar-key${className ? ` ${className}` : ''}`}>
      <span className="vbar-key__item">
        <span className="vbar-key__swatch vbar-key__swatch--valid" aria-hidden="true" />
        etibarlı səs
      </span>
      <span className="vbar-key__item">
        <span className="vbar-key__swatch vbar-key__swatch--invalid" aria-hidden="true" />
        audit qərarı ilə etibarsız sayılıb
      </span>
    </span>
  )
}
