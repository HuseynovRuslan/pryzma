import { useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import type { CompetitionPhase, CompetitionStatus, ProjectStatus, SubmissionStatus } from '../api/types'
import { formatDateTime } from '../lib/format'
import { useApi } from '../lib/useApi'
import { adminApi } from './api'
import { auditActionLabel, competitionStatusAdmin, phaseAdmin, projectStatusAdmin, submissionStatusAdmin } from './labels'

export function PageHeader({
  crumbs,
  title,
  meta,
  actions,
}: {
  crumbs?: { label: string; to?: string }[]
  title: string
  meta?: ReactNode
  actions?: ReactNode
}) {
  return (
    <header className="a-head">
      <div className="a-head__text">
        {crumbs && (
          <nav aria-label="Harada olduğunuz" className="a-crumbs">
            <ol role="list">
              {crumbs.map((c, i) => (
                <li key={`${c.label}-${i}`}>{c.to ? <Link to={c.to}>{c.label}</Link> : <span aria-current="page">{c.label}</span>}</li>
              ))}
            </ol>
          </nav>
        )}
        <h1 className="a-head__title">{title}</h1>
        {meta && <div className="a-head__meta">{meta}</div>}
      </div>
      {actions && <div className="a-head__actions">{actions}</div>}
    </header>
  )
}

type Tone = 'draft' | 'live' | 'closed' | 'archived' | 'review' | 'good' | 'bad' | 'demo'

export function Badge({ tone, children }: { tone: Tone; children: ReactNode }) {
  return (
    <span className={`a-badge a-badge--${tone}`}>
      <span className="a-badge__dot" aria-hidden="true" />
      {children}
    </span>
  )
}

/** Marks demo content (`seed-demo`): visible to admins only, never on the public site. */
export function DemoBadge() {
  return <Badge tone="demo">Nümunə</Badge>
}

/** "Məlumat" filter: everything, real data only, or demo content only. Values match the API's `demo` parameter. */
export function DemoFilter({ value, onChange }: { value: string | undefined; onChange: (value: string | undefined) => void }) {
  return (
    <label className="a-field">
      <span className="a-field__label">Məlumat</span>
      <select className="input" value={value ?? ''} onChange={(e) => onChange(e.target.value || undefined)}>
        <option value="">Hamısı</option>
        <option value="false">Real</option>
        <option value="true">Nümunə məlumat</option>
      </select>
    </label>
  )
}

const projectTone: Record<ProjectStatus, Tone> = { Draft: 'draft', Published: 'live', Archived: 'archived' }
const competitionTone: Record<CompetitionStatus, Tone> = { Draft: 'draft', Published: 'live', Closed: 'closed', Archived: 'archived' }
const submissionTone: Record<SubmissionStatus, Tone> = {
  Draft: 'draft',
  Submitted: 'live',
  UnderReview: 'review',
  Approved: 'good',
  Rejected: 'bad',
  Winner: 'good',
  Withdrawn: 'archived',
}

export const ProjectStatusBadge = ({ status }: { status: ProjectStatus }) => (
  <Badge tone={projectTone[status]}>{projectStatusAdmin[status]}</Badge>
)
export const CompetitionStatusBadge = ({ status }: { status: CompetitionStatus }) => (
  <Badge tone={competitionTone[status]}>{competitionStatusAdmin[status]}</Badge>
)
export const SubmissionStatusBadge = ({ status }: { status: SubmissionStatus }) => (
  <Badge tone={submissionTone[status]}>{submissionStatusAdmin[status]}</Badge>
)
export const PhaseNote = ({ phase }: { phase: CompetitionPhase }) => (
  <span className={`a-phase a-phase--${phase.toLowerCase()}`}>{phaseAdmin[phase]}</span>
)

/** Two-step button for destructive or public-facing actions: first click asks, second click does. */
export function ConfirmButton({
  label,
  confirmLabel,
  onConfirm,
  tone = 'default',
  disabled,
}: {
  label: string
  confirmLabel: string
  onConfirm: () => Promise<unknown> | void
  tone?: 'default' | 'danger' | 'primary'
  disabled?: boolean
}) {
  const [asking, setAsking] = useState(false)
  const [busy, setBusy] = useState(false)
  const cls = `btn btn--sm${tone === 'danger' ? ' a-btn--danger' : tone === 'primary' ? ' btn--primary' : ''}`
  if (!asking)
    return (
      <button type="button" className={cls} onClick={() => setAsking(true)} disabled={disabled}>
        {label}
      </button>
    )
  return (
    <span className="a-confirm" role="group" aria-label={confirmLabel}>
      <button
        type="button"
        className={`btn btn--sm ${tone === 'danger' ? 'a-btn--danger-solid' : 'btn--primary'}`}
        disabled={busy}
        onClick={async () => {
          setBusy(true)
          try {
            await onConfirm()
          } finally {
            setBusy(false)
            setAsking(false)
          }
        }}
      >
        {confirmLabel}
      </button>
      <button type="button" className="btn btn--sm btn--ghost" onClick={() => setAsking(false)}>
        Ləğv et
      </button>
    </span>
  )
}

export function Notice({ tone = 'info', children }: { tone?: 'info' | 'error' | 'success' | 'warn'; children: ReactNode }) {
  return (
    <div className={`a-notice a-notice--${tone}`} role={tone === 'error' ? 'alert' : 'status'}>
      {children}
    </div>
  )
}

export function Stat({ value, label, to }: { value: number; label: string; to?: string }) {
  const body = (
    <>
      <span className="a-stat__value tabular">{value}</span>
      <span className="a-stat__label">{label}</span>
    </>
  )
  return to ? (
    <Link to={to} className="a-stat a-stat--link">
      {body}
    </Link>
  ) : (
    <div className="a-stat">{body}</div>
  )
}

/** Audit trail for one entity: who did what, when. */
export function AuditList({ entityType, entityId, refreshKey }: { entityType: string; entityId: number; refreshKey?: unknown }) {
  const state = useApi((signal) => adminApi.audit(entityType, entityId, signal), [entityType, entityId, refreshKey])
  if (state.status === 'loading') return <p className="a-muted">Yüklənir…</p>
  if (state.status === 'error') return <Notice tone="error">{state.error.message}</Notice>
  if (state.data.length === 0) return <p className="a-muted">Hələ qeyd yoxdur.</p>
  return (
    <ol role="list" className="a-timeline">
      {state.data.map((e) => (
        <li key={e.id}>
          <span className="a-timeline__what">
            {auditActionLabel[e.action] ?? e.action}
            {e.summary && <span className="a-muted"> — {e.summary}</span>}
          </span>
          <span className="a-timeline__who">
            {e.userName ?? 'Sistem'}, <span className="tabular">{formatDateTime(e.createdAt)}</span>
          </span>
        </li>
      ))}
    </ol>
  )
}

export function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="a-empty">
      <p className="a-empty__title">{title}</p>
      {children}
    </div>
  )
}
