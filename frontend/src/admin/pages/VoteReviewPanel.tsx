import { useId, useMemo, useRef, useState } from 'react'
import { ApiError } from '../../api/client'
import { formatDateTime } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { workNumber } from '../../pages/voting/lib'
import { ConfirmDialog } from '../ConfirmDialog'
import { adminApi, type AdminReviewVote, type AdminWorkEvidence, type AdminWorkReview, type VoteEvidence } from '../api'
import { Badge, Notice } from '../ui'

/** The server's own floor for a reason (VoteReview.MinReasonLength). */
export const MIN_REASON = 10

const evidenceLabel: Record<VoteEvidence, string> = {
  PhoneCluster: 'Telefon klasteri',
  CoordinatedIdentity: 'R2 · koordinasiyalı kimlik',
  DisposableEmailDuringVoting: 'D14 · müvəqqəti e-poçt',
  Weak: 'Yalnız şübhə',
  None: 'Əlamət yoxdur',
}

type Filter = 'all' | 'cluster' | 'invalidated'

/** The evidence a vote may be set aside on: a phone cluster, or R2 / D14 while the server has them switched on. */
const isGround = (v: AdminReviewVote) =>
  v.evidence === 'PhoneCluster' || v.evidence === 'CoordinatedIdentity' || v.evidence === 'DisposableEmailDuringVoting'

/**
 * One work's votes, under review (D12). The evidence in numbers first — never a phone, an e-mail or an address: a
 * repeated phone is "T1 · 77 hesab" — then every vote with its signs, and the one action this screen has:
 * "Seçilmiş səsləri etibarsız say", or the reverse, always for votes an admin picked and always with a reason.
 *
 * Only votes of a phone cluster (a phone on 3+ voting accounts) can be picked to set aside; the server refuses
 * anything else too, a repeated name and the author's phone included — those are review signs. A group can be
 * picked at once ("T1 qrupunu seç") because reviewing seventy-seven identical accounts one box at a time helps
 * nobody — but it is still a person choosing a group they have looked at, never "everything suspicious".
 */
export function VoteReviewPanel({
  competitionId,
  submissionId,
  onClose,
  onChanged,
}: {
  competitionId: number
  submissionId: number
  onClose: () => void
  /** A decision went through: the counts around this panel are out of date. */
  onChanged: () => void
}) {
  const [version, setVersion] = useState(0)
  const state = useApi((signal) => adminApi.voteReview(competitionId, submissionId, signal), [competitionId, submissionId, version])
  // Kept across a refresh, so the panel does not blink empty after a decision.
  const kept = useRef<AdminWorkReview | null>(null)
  if (state.status === 'success') kept.current = state.data
  const review = state.status === 'success' ? state.data : kept.current?.evidence.submissionId === submissionId ? kept.current : null

  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [filter, setFilter] = useState<Filter>('cluster')
  const [action, setAction] = useState<'invalidate' | 'restore' | null>(null)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | undefined>()
  const [done, setDone] = useState<string | null>(null)
  const titleId = useId()

  const votes = useMemo(() => review?.votes ?? [], [review])
  const shown = votes.filter((v) =>
    filter === 'all' ? true : filter === 'invalidated' ? v.invalidatedAt !== null : isGround(v) && v.invalidatedAt === null,
  )
  const pickable = (v: AdminReviewVote) => v.invalidatedAt !== null || isGround(v)
  const picked = votes.filter((v) => selected.has(v.voteId))
  const toSetAside = picked.filter((v) => v.invalidatedAt === null)
  const toRestore = picked.filter((v) => v.invalidatedAt !== null)
  const mixed = toSetAside.length > 0 && toRestore.length > 0

  // Phone clusters among the votes still counted, largest first: one button each. A name group gets one only for its
  // R2 votes while R2 is switched on — otherwise names are review signs.
  const groups = useMemo(() => {
    const byLabel = new Map<string, { label: string; ids: number[] }>()
    const add = (label: string, id: number) => {
      const g = byLabel.get(label) ?? { label, ids: [] }
      g.ids.push(id)
      byLabel.set(label, g)
    }
    for (const v of votes) {
      if (v.invalidatedAt !== null) continue
      if (v.evidence === 'PhoneCluster' && v.phoneGroup && v.phoneGroupSize >= 3) add(v.phoneGroup, v.voteId)
      else if (v.evidence === 'CoordinatedIdentity' && v.nameGroup) add(`${v.nameGroup} (R2)`, v.voteId)
    }
    return [...byLabel.values()].sort((a, b) => b.ids.length - a.ids.length)
  }, [votes])

  const toggle = (id: number) =>
    setSelected((s) => {
      const next = new Set(s)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const decide = async () => {
    if (!action || !review) return
    const ids = (action === 'invalidate' ? toSetAside : toRestore).map((v) => v.voteId)
    setBusy(true)
    setError(undefined)
    try {
      const res =
        action === 'invalidate'
          ? await adminApi.invalidateVotes(competitionId, ids, reason.trim())
          : await adminApi.restoreVotes(competitionId, ids, reason.trim())
      setDone(
        action === 'invalidate'
          ? `${res.changed} səs etibarsız sayıldı. Saytdakı reytinq artıq onları saymır.`
          : `${res.changed} səs bərpa olundu və yenidən sayılır.`,
      )
      setAction(null)
      setReason('')
      setSelected(new Set())
      setVersion((n) => n + 1)
      onChanged()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Qərar saxlanmadı. Yenidən cəhd edin.')
    } finally {
      setBusy(false)
    }
  }

  if (state.status === 'error' && !review)
    return (
      <section className="a-panel a-review">
        <Notice tone="error">{state.error.message}</Notice>
      </section>
    )
  if (!review) return <section className="a-panel a-review" aria-busy="true" />

  const e = review.evidence
  const work = `${workNumber(e.reference)}${e.title ? ` — ${e.title}` : ''}`
  const count = action === 'invalidate' ? toSetAside.length : toRestore.length

  return (
    <section className="a-panel a-review" aria-labelledby={titleId}>
      <header className="a-panel__head a-review__head">
        <h3 id={titleId} className="a-panel__title">
          Səslərin yoxlanışı: {work}
        </h3>
        <button type="button" className="btn btn--sm btn--ghost" onClick={onClose}>
          Bağla
        </button>
      </header>

      <Evidence e={e} />

      <p className="a-muted a-review__policy">
        {e.coordinatedIdentityEnabled ? (
          <>
            Yalnız <strong>telefon klasterinə</strong> (səsverənin telefonu müsabiqədə üç və daha çox səsverən hesabda
            istifadə olunub) və ya <strong>R2 koordinasiyalı kimlik</strong> qaydasına (səsvermə zamanı açılmış hesab,
            eyni ad-soyad çox səsverən hesabda, onların kifayət qədəri eyni IP-dən açılıb) aid səs etibarsız sayıla bilər.
            Təkrar ad, müəllifin telefonu, eyni IP, vaxt və oxşar e-mail tək-tək yalnız yoxlama əlamətidir.
          </>
        ) : (
          <>
            Yalnız <strong>telefon klasterinə</strong> aid səs etibarsız sayıla bilər: səsverənin telefonu müsabiqədə üç və
            daha çox səsverən hesabda istifadə olunub. Təkrar ad, müəllifin telefonu, eyni IP, vaxt və oxşar e-mail yalnız
            yoxlama əlamətidir.
          </>
        )}{' '}
        Səslər silinmir: siyahıda qalır, sayılmır və səbəb yazılaraq
        bərpa oluna bilər. Hər qərar audit jurnalına yazılır. İşin özü cəzalandırılmır.
      </p>

      {done && (
        <p className="a-notice a-notice--success" role="status">
          {done}
        </p>
      )}

      <div className="a-review__tools">
        <div className="a-review__filter" role="group" aria-label="Göstər">
          {(
            [
              ['cluster', e.coordinatedIdentityEnabled ? 'Sübutlu, sayılan' : 'Telefon klasteri, sayılan'],
              ['invalidated', 'Etibarsız sayılmış'],
              ['all', 'Hamısı'],
            ] as const
          ).map(([key, label]) => (
            <button
              key={key}
              type="button"
              className={`btn btn--sm${filter === key ? '' : ' btn--ghost'}`}
              aria-pressed={filter === key}
              onClick={() => setFilter(key)}
            >
              {label}
            </button>
          ))}
        </div>
        {groups.length > 0 && (
          <div className="a-review__groups">
            {groups.map((g) => (
              <button
                key={g.label}
                type="button"
                className="btn btn--sm btn--ghost"
                onClick={() => setSelected((s) => new Set([...s, ...g.ids]))}
              >
                {g.label} qrupunu seç <span className="tabular">({g.ids.length})</span>
              </button>
            ))}
          </div>
        )}
      </div>

      {shown.length === 0 ? (
        <p className="a-muted">Bu filtrdə səs yoxdur.</p>
      ) : (
        <ul className="a-review__list" role="list">
          {shown.map((v) => (
            <VoteRow key={v.voteId} vote={v} checked={selected.has(v.voteId)} pickable={pickable(v)} onToggle={() => toggle(v.voteId)} />
          ))}
        </ul>
      )}

      <div className="a-review__bar">
        <span className="tabular">{selected.size} səs seçilib</span>
        {selected.size > 0 && (
          <button type="button" className="btn btn--sm btn--ghost" onClick={() => setSelected(new Set())}>
            Seçimi təmizlə
          </button>
        )}
        <span className="a-review__actions">
          <button
            type="button"
            className="btn btn--sm a-btn--danger-solid"
            disabled={toSetAside.length === 0 || mixed}
            onClick={() => {
              setError(undefined)
              setAction('invalidate')
            }}
          >
            Seçilmiş səsləri etibarsız say
          </button>
          <button
            type="button"
            className="btn btn--sm"
            disabled={toRestore.length === 0 || mixed}
            onClick={() => {
              setError(undefined)
              setAction('restore')
            }}
          >
            Seçilmiş səsləri bərpa et
          </button>
        </span>
        {mixed && <span className="a-muted">Bir qərarda ya etibarsız saymaq, ya da bərpa etmək olar.</span>}
      </div>

      {action && (
        <ConfirmDialog
          title={action === 'invalidate' ? 'Seçilmiş səsləri etibarsız say' : 'Seçilmiş səsləri bərpa et'}
          confirmLabel={action === 'invalidate' ? `${count} səsi etibarsız say` : `${count} səsi bərpa et`}
          tone={action === 'invalidate' ? 'danger' : 'primary'}
          busy={busy}
          error={error}
          confirmDisabled={reason.trim().length < MIN_REASON}
          onConfirm={decide}
          onCancel={() => {
            setAction(null)
            setError(undefined)
          }}
        >
          <dl className="a-review__confirm">
            <dt>İş</dt>
            <dd>{work}</dd>
            <dt>Səs sayı</dt>
            <dd className="tabular">{count}</dd>
            <dt>Nəticə</dt>
            <dd>
              {action === 'invalidate'
                ? 'Səslər silinmir. Saytdakı reytinqdə və saylarda nəzərə alınmır; səsverən yenidən səs verə bilmir. İstənilən vaxt səbəb yazılaraq bərpa oluna bilər.'
                : 'Səslər yenidən sayılır və saytdakı reytinqə qayıdır.'}
            </dd>
          </dl>
          <label className="a-field">
            <span className="a-field__label">Səbəb (məcburi)</span>
            <textarea
              className="input"
              rows={3}
              maxLength={500}
              value={reason}
              onChange={(ev) => setReason(ev.target.value)}
              placeholder={
                action === 'invalidate'
                  ? 'Məsələn: Eyni telefonla açılmış 77 hesab, eyni ad-soyad, ardıcıl qeydiyyat.'
                  : 'Məsələn: Yoxlama göstərdi ki, hesab ayrıca şəxsə məxsusdur.'
              }
            />
            <span className="a-muted">Ən azı {MIN_REASON} simvol. Audit jurnalına yazılır.</span>
          </label>
        </ConfirmDialog>
      )}
    </section>
  )
}

function Evidence({ e }: { e: AdminWorkEvidence }) {
  const span = (a: string | null, b: string | null) => (a && b ? `${formatDateTime(a)} — ${formatDateTime(b)}` : '—')
  const rows: [string, string, string?][] = [
    ['Xam səs', String(e.rawVotes)],
    ['Etibarlı', String(e.validVotes)],
    ['Etibarsız sayılıb', String(e.invalidatedVotes)],
    ['Telefon klasterində (sayılan)', String(e.phoneClusterVotes)],
    [
      'R2 koordinasiyalı kimlik (sayılan)',
      `${e.coordinatedIdentityVotes ?? 0} səs`,
      e.coordinatedIdentityEnabled ? 'etibarsız sayıla bilər' : 'qayda aktiv deyil — yalnız əlamət',
    ],
    ['Təkrarlanan telefonla', `${e.repeatedPhoneVotes} səs`, e.largestPhoneGroup ? `ən böyük qrup ${e.largestPhoneGroup} hesab` : undefined],
    ['Təkrarlanan ad-soyadla', `${e.repeatedNameVotes} səs`, e.largestNameGroup ? `ən böyük qrup ${e.largestNameGroup} hesab` : undefined],
    ['Eyni IP', `ən böyük qrup ${e.largestIpGroup} hesab`, 'tək başına sübut deyil'],
    ['Müəllifin IP-si ilə', `${e.authorIpVotes} səs`, 'tək başına sübut deyil'],
    ['Müəllifin telefonu ilə', `${e.authorPhoneVotes} səs`],
    ['Yeni hesablardan', `${e.newAccountVotes} səs`],
    ['Qeydiyyatdan səsə (median)', e.medianSecondsToVote === null ? '—' : `${e.medianSecondsToVote} saniyə`],
    ['15 dəqiqədə ən çox yeni hesab səsi', String(e.largestBurst15Min)],
    ['Hesablar açılıb', span(e.firstSignUp, e.lastSignUp)],
    ['Səslər verilib', span(e.firstVote, e.lastVote)],
  ]
  return (
    <dl className="a-review__facts">
      {rows.map(([label, value, note]) => (
        <div key={label} className="a-review__fact">
          <dt>{label}</dt>
          <dd className="tabular">
            {value}
            {note && <span className="a-muted"> · {note}</span>}
          </dd>
        </div>
      ))}
    </dl>
  )
}

function VoteRow({
  vote: v,
  checked,
  pickable,
  onToggle,
}: {
  vote: AdminReviewVote
  checked: boolean
  pickable: boolean
  onToggle: () => void
}) {
  const id = useId()
  const groups = [
    v.phoneGroup && `${v.phoneGroup} · telefon, ${v.phoneGroupSize} hesab`,
    v.nameGroup && `${v.nameGroup} · ad, ${v.nameGroupSize} hesab`,
    v.ipGroup && `${v.ipGroup} · IP, ${v.ipGroupSize} hesab`,
    v.coordinatedIdentity && 'R2',
  ].filter(Boolean)
  return (
    <li className={`a-review__vote${v.invalidatedAt ? ' is-invalidated' : ''}`}>
      <input
        id={id}
        type="checkbox"
        className="a-review__check"
        checked={checked}
        disabled={!pickable}
        onChange={onToggle}
        aria-label={`${v.voterName}, ${v.score}%`}
      />
      <span className="a-review__score">
        <Badge tone={isGround(v) ? 'bad' : v.score >= 30 ? 'review' : 'closed'}>{v.score}%</Badge>
        <span className="a-review__evidence">{evidenceLabel[v.evidence]}</span>
      </span>
      <label htmlFor={id} className="a-review__who">
        <span className="a-table__primary">{v.voterName}</span>
        <span className="a-table__sub tabular">
          açılıb {formatDateTime(v.signedUpAt)}, səs {formatDateTime(v.votedAt)}
        </span>
      </label>
      <span className="a-review__tags">
        {groups.map((g) => (
          <span key={g as string} className="a-review__tag">
            {g}
          </span>
        ))}
      </span>
      {v.reasons.length > 0 && (
        <ul className="a-reasons a-review__why">
          {v.reasons.map((r) => (
            <li key={r}>{r}</li>
          ))}
        </ul>
      )}
      {v.invalidatedAt && (
        <p className="a-review__status">
          Etibarsız sayılıb {formatDateTime(v.invalidatedAt)}
          {v.invalidatedBy ? `, ${v.invalidatedBy}` : ''}: {v.invalidationReason}
        </p>
      )}
    </li>
  )
}
