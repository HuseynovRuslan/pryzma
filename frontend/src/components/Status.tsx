import type { CompetitionPhase } from '../api/types'
import { phaseLabel } from '../lib/format'

export function PhaseStatus({ phase, onAccent = false }: { phase: CompetitionPhase; onAccent?: boolean }) {
  return (
    <span className={`status status--${phase.toLowerCase()}${onAccent ? ' status--on-accent' : ''}`}>
      <span className="status__glyph" aria-hidden="true" />
      {phaseLabel[phase]}
    </span>
  )
}
