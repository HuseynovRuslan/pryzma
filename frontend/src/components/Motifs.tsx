import type { CSSProperties } from 'react'
import './Motifs.css'

/** The buta outline: a rounded body, a tip that bends over — drawn in a 100 × 140 box. */
const BUTA_OUTER = 'M50 136 C24 136 10 116 10 92 C10 64 30 48 46 38 C58 30 64 18 60 4 C80 18 90 44 90 76 C90 112 72 136 50 136 Z'
const BUTA_INNER = 'M50 124 C31 124 22 109 22 92 C22 72 36 60 50 51 C60 45 67 36 69 26 C76 38 79 56 79 76 C79 104 67 124 50 124 Z'
const BUTA_EYE = 'M44 104 C36 104 32 97 34 90 C36 83 44 80 50 84 C55 88 54 97 48 100'

/**
 * A single buta, as an outline — the one Azerbaijani ornament MEYDAN uses, and sparingly: large, thin, cropped,
 * never tiled. Decorative only.
 */
export function Buta({ className, eye = true }: { className?: string; eye?: boolean }) {
  return (
    <svg className={className} viewBox="0 0 100 140" aria-hidden="true" focusable="false">
      <g fill="none" stroke="currentColor" strokeLinecap="round" vectorEffect="non-scaling-stroke">
        <path d={BUTA_OUTER} vectorEffect="non-scaling-stroke" />
        <path d={BUTA_INNER} vectorEffect="non-scaling-stroke" />
        {eye && <path d={BUTA_EYE} vectorEffect="non-scaling-stroke" />}
      </g>
    </svg>
  )
}

/** Deterministic 32-bit hash, so a project's fallback never changes between visits. */
function hash(text: string): number {
  let h = 2166136261
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  return h >>> 0
}

/**
 * The visual of a project that has no cover yet: its colour field and one buta outline — fine-lined, at a natural size,
 * cropped by the right edge. Its placement follows the project's slug, so projects still differ. A real cover always
 * replaces it.
 */
export function ProjectFallback({ seed }: { seed: string }) {
  const h = hash(seed) || 1
  const style = { '--motif-shift': `${(h % 13) - 6}%`, '--motif-flip': h & 1 ? -1 : 1 } as CSSProperties
  return (
    <span className="motif" style={style} aria-hidden="true">
      <Buta className="motif__buta" eye={false} />
    </span>
  )
}
