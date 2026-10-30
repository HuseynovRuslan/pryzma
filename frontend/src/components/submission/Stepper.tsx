import { ordinal } from '../../lib/format'

export interface Step {
  key: string
  label: string
}

/** Full labels on wide screens; on phones a compact "Addım 2 / 4" line over segment bars. */
export function Stepper({ steps, current }: { steps: Step[]; current: number }) {
  return (
    <nav className="stepper2" aria-label="Təqdimat addımları">
      <p className="stepper2__compact" aria-hidden="true">
        <span className="tabular">
          Addım {current + 1} / {steps.length}
        </span>
        <strong>{steps[current]?.label}</strong>
      </p>
      <ol role="list" className="stepper2__list">
        {steps.map((s, i) => {
          const state = i < current ? 'done' : i === current ? 'current' : 'todo'
          return (
            <li key={s.key} className={`stepper2__item is-${state}`} aria-current={i === current ? 'step' : undefined}>
              <span className="stepper2__n tabular">{ordinal(i + 1)}</span>
              <span className="stepper2__label">
                {s.label}
                {state === 'done' && <span className="visually-hidden"> (tamamlanıb)</span>}
              </span>
            </li>
          )
        })}
      </ol>
    </nav>
  )
}
