/**
 * "Qalib" mark: ink plate with the MEYDAN turned square in saffron — the only place the result uses the signal colour.
 * A placed work of a competition without categories carries its place on the same plate ("2-ci yer").
 */
export function WinnerBadge({ size = 'md', label = 'Qalib' }: { size?: 'sm' | 'md' | 'lg'; label?: string }) {
  return (
    <span className={`winner-badge winner-badge--${size}`}>
      <span className="winner-badge__glyph" aria-hidden="true" />
      {label}
    </span>
  )
}
