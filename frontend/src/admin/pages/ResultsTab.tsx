import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { WinnerBadge } from '../../components/WinnerBadge'
import { formatDateTime } from '../../lib/format'
import { azAblative, azCountOf, azGenitive, azLocative, matchesSearch, singularAz } from '../../lib/text'
import { useApi } from '../../lib/useApi'
import { placeName } from '../../lib/text'
import {
  adminApi,
  type AdminCompetition,
  type AdminPlace,
  type AdminResult,
  type AdminSubmissionRow,
  type AdminWinnerSlot,
} from '../api'
import { ConfirmDialog } from '../ConfirmDialog'
import { PreviewThumb } from '../PreviewThumb'
import { AField } from '../forms'
import { readErrors } from '../errors'
import { Badge, Notice, Stat } from '../ui'
import { AwardsPanel } from './AwardsPanel'
import { placesFromVotes, placesProblem } from './places'
import { LateVotes, SetAsideVotes } from './VotingTab'

/**
 * Results of a competition: one winner per category (settlement) — or, without categories, a first, second and third
 * place with the vote beside them. An overview with every winner chosen or replaced right here, then publishing, the
 * results banner and the announcement. Nothing here is public until "Nəticəni dərc et".
 */
export function ResultsTab({ competition, onChanged }: { competition: AdminCompetition; onChanged: () => Promise<void> }) {
  const state = useApi((signal) => adminApi.result(competition.id, signal), [competition.id])
  const [result, setResult] = useState<AdminResult>()
  // A podium changes the result's state and what publication checks: read the result again after it.
  const refresh = async () => {
    setResult(await adminApi.result(competition.id))
    await onChanged()
  }
  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  const r = result ?? state.data
  const update = async (next: AdminResult) => {
    setResult(next)
    await onChanged()
  }
  const label = r.categoryLabel ?? 'Kateqoriyalar'

  return (
    <div className="a-form" style={{ gap: 'var(--s-5)' }}>
      {r.hasCategories && (
        <div className="a-stats a-stats--results">
          <Stat value={r.categoryCount} label={singularAz(label).toLocaleLowerCase('az')} />
          <Stat value={r.winnerCount} label="qalib seçilib" />
          <Stat value={Math.max(0, r.categoryCount - r.winnerCount)} label="qalib gözləyir" />
          <Stat value={r.eligibleCount} label="təqdim edilmiş iş" />
        </div>
      )}
      <div className="a-split a-split--wide-aside">
        <div className="a-form" style={{ gap: 'var(--s-5)' }}>
          {r.hasCategories ? (
            <WinnersPanel r={r} competition={competition} onChange={update} />
          ) : (
            <PlacesPanel r={r} onChange={update} />
          )}
          <AwardsPanel competitionId={competition.id} onChanged={refresh} />
        </div>
        <div className="a-form a-split__aside" style={{ gap: 'var(--s-5)' }}>
          <PublishPanel r={r} onChange={update} />
          <CoverPanel r={r} onChange={update} />
          <VisibilityPanel r={r} />
        </div>
      </div>
    </div>
  )
}

// ---- First, second and third place (a competition without categories) -----------------------------

/**
 * The three places of a competition without categories, with the public vote beside them. The vote informs the
 * choice and never makes it: "Səs sıralamasından doldur" fills only the places the tally decides on its own, and
 * names the works that are level so the admin puts them in order (D3). The three are saved together, against the
 * places this screen showed — a stale screen is refused, never overwrites.
 */
function PlacesPanel({ r, onChange }: { r: AdminResult; onChange: (r: AdminResult) => Promise<void> }) {
  const id = useId()
  const saved = r.places.map((p) => p.winner?.submissionId ?? null)
  const [draft, setDraft] = useState<(number | null)[] | null>(null)
  const [notes, setNotes] = useState<string[]>([])
  const [confirming, setConfirming] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [showAll, setShowAll] = useState(false)
  const published = r.state === 'Published'
  const byId = useMemo(() => new Map(r.placeCandidates.map((c) => [c.submissionId, c])), [r.placeCandidates])
  const shown = draft ?? saved
  const problem = draft ? placesProblem(draft) : null
  const changed = draft !== null && draft.some((x, i) => x !== saved[i])
  const tied = new Set(
    r.placeCandidates.filter((c, _, all) => c.voteCount > 0 && all.some((o) => o !== c && o.voteRank === c.voteRank)).map((c) => c.submissionId),
  )
  const ranking = showAll ? r.placeCandidates : r.placeCandidates.slice(0, 10)

  const edit = () => {
    setDraft(saved)
    setNotes([])
    setError(undefined)
  }
  const cancel = () => {
    setDraft(null)
    setNotes([])
    setError(undefined)
  }
  const fill = () => {
    const next = placesFromVotes(r.placeCandidates, r.places.length)
    setDraft(next.places)
    setNotes(next.notes)
  }
  const save = async () => {
    if (!draft) return
    setBusy(true)
    setError(undefined)
    try {
      await onChange(await adminApi.setPlaces(r.competitionId, draft, saved, published))
      setConfirming(false)
      setDraft(null)
      setNotes([])
    } catch (err) {
      setError(readErrors(err, 'Yerlər yadda saxlanmadı.').message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="a-form" style={{ gap: 'var(--s-5)' }}>
      <section className="a-panel" aria-labelledby={`${id}-title`}>
        <div className="a-panel__head">
          <h2 id={`${id}-title`} className="a-panel__title">
            Qaliblər: 1-ci, 2-ci və 3-cü yer
          </h2>
          <span className="a-muted tabular">
            {saved.filter((x) => x !== null).length} / {saved.length}
          </span>
        </div>
        {r.voting === 'Open' && (
          <Notice tone="warn">Səsvermə hələ davam edir — sıralama dəyişə bilər. Yerləri səsvermə bitəndən sonra təyin etmək məsləhətdir.</Notice>
        )}
        {error && !confirming && <Notice tone="error">{error}</Notice>}

        {draft === null ? (
          <>
            <ul role="list" className="a-slots">
              {r.places.map((p) => (
                <PlaceRow key={p.place} place={p} votes={p.winner ? byId.get(p.winner.submissionId)?.voteCount : undefined} />
              ))}
            </ul>
            <div className="a-actions">
              <button type="button" className={`btn btn--sm${saved[0] === null ? ' btn--primary' : ''}`} onClick={edit} disabled={r.placeCandidates.length === 0}>
                {saved[0] === null ? 'Yerləri təyin et' : 'Yerləri dəyiş'}
              </button>
            </div>
          </>
        ) : (
          <>
            <p className="a-muted">
              Hər yer üçün bir iş seçin. Yerlər yuxarıdan doldurulur; bir iş yalnız bir yer tuta bilər. Bərabər səs toplamış işlərin
              sırasını siz təyin edirsiniz.
            </p>
            {notes.length > 0 && (
              <Notice tone="warn">
                <ul>
                  {notes.map((n) => (
                    <li key={n}>{n}</li>
                  ))}
                </ul>
              </Notice>
            )}
            <div className="a-places">
              {draft.map((value, i) => (
                <AField key={i} label={placeName(i + 1)}>
                  <select
                    className="input"
                    value={value ?? ''}
                    onChange={(e) => {
                      const next = [...draft]
                      next[i] = e.target.value ? Number(e.target.value) : null
                      setDraft(next)
                    }}
                  >
                    <option value="">— seçilməyib —</option>
                    {r.placeCandidates.map((c) => (
                      <option key={c.submissionId} value={c.submissionId} disabled={draft.some((x, j) => j !== i && x === c.submissionId)}>
                        {c.reference} · {c.title || 'Adsız iş'} · {c.voteCount} səs{tied.has(c.submissionId) ? ' (bərabər)' : ''}
                      </option>
                    ))}
                  </select>
                </AField>
              ))}
            </div>
            {problem && <Notice tone="error">{problem}</Notice>}
            <div className="a-actions">
              <button type="button" className="btn btn--sm" onClick={fill}>
                Səs sıralamasından doldur
              </button>
              <button type="button" className="btn btn--sm btn--primary" disabled={!changed || problem !== null} onClick={() => setConfirming(true)}>
                Yadda saxla
              </button>
              <button type="button" className="btn btn--sm btn--ghost" onClick={cancel}>
                Ləğv et
              </button>
            </div>
          </>
        )}

        {confirming && draft && (
          <ConfirmDialog
            title="Yerləri yadda saxlamaq istəyirsiniz?"
            confirmLabel="Bəli, yadda saxla"
            busy={busy}
            error={error}
            onCancel={() => setConfirming(false)}
            onConfirm={save}
          >
            {published && <PublishedWarning publicNow={r.isPublicNow} />}
            <dl className="a-kv a-kv--dialog">
              {draft.map((value, i) => {
                const c = value === null ? undefined : byId.get(value)
                return (
                  <div key={i}>
                    <dt>{placeName(i + 1)}</dt>
                    <dd>
                      {c ? (
                        <>
                          <strong>{c.participantName}</strong> — {c.title || 'Adsız iş'} <span className="tabular">{c.reference}</span> ·{' '}
                          <span className="tabular">{c.voteCount} səs</span>
                        </>
                      ) : (
                        'seçilməyib'
                      )}
                    </dd>
                  </div>
                )
              })}
            </dl>
            {!published && <p className="a-muted">Nəticə saytda yalnız siz onu dərc edəndə görünəcək.</p>}
          </ConfirmDialog>
        )}
      </section>

      <section className="a-panel a-ranked" aria-labelledby={`${id}-votes`}>
        <div className="a-panel__head">
          <h2 id={`${id}-votes`} className="a-panel__title">
            Səs sıralaması
          </h2>
          <span className="a-muted tabular">{r.placeCandidates.length} iş</span>
        </div>
        {r.voting === 'NotConfigured' ? (
          <p className="a-muted">Bu müsabiqədə səsvermə olmayıb — yerləri münsiflərin qərarı ilə seçin.</p>
        ) : (
          <>
            <p className="a-muted">Bərabər səs toplamış işlər eyni sırada göstərilir; aralarında seçimi siz edirsiniz.</p>
            <table className="a-table a-rank">
              <thead>
                <tr>
                  <th scope="col">Sıra</th>
                  <th scope="col">İş</th>
                  <th scope="col">İştirakçı</th>
                  <th scope="col">Səs</th>
                  <th scope="col">Yer</th>
                </tr>
              </thead>
              <tbody>
                {ranking.map((c) => {
                  const held = shown.indexOf(c.submissionId)
                  return (
                    <tr key={c.submissionId}>
                      <td className="tabular a-rank__place">{c.voteRank}</td>
                      <td className="a-rank__work">
                        <Link to={`/admin/submissions/${c.submissionId}`}>{c.title || 'Adsız iş'}</Link>
                        <br />
                        <span className="a-muted tabular">{c.reference}</span>
                        {tied.has(c.submissionId) && (
                          <>
                            {' '}
                            <Badge tone="review">Bərabər</Badge>
                          </>
                        )}
                      </td>
                      <td className="a-rank__who">{c.participantName}</td>
                      <td className="tabular a-rank__votes">
                        {c.voteCount}
                        {c.lateVoteCount > 0 && <LateVotes count={c.lateVoteCount} />}
                        {c.invalidatedVoteCount > 0 && <SetAsideVotes count={c.invalidatedVoteCount} />}
                      </td>
                      <td className="a-rank__extra">{held >= 0 && <WinnerBadge size="sm" label={placeName(held + 1)} />}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
            {r.placeCandidates.length > 10 && (
              <button type="button" className="a-link" onClick={() => setShowAll(!showAll)}>
                {showAll ? 'İlk 10-u göstər' : `Hamısını göstər (${r.placeCandidates.length})`}
              </button>
            )}
          </>
        )}
      </section>
    </div>
  )
}

function PlaceRow({ place: p, votes }: { place: AdminPlace; votes: number | undefined }) {
  const w = p.winner
  const name = placeName(p.place)
  return (
    <li className={`a-slot${w ? ' is-won' : ' a-slot--open'}`}>
      {w && (
        <span className="a-slot__art">
          <PreviewThumb submissionId={w.submissionId} preview={w.preview} />
        </span>
      )}
      <span className="a-slot__main">
        <span className="a-slot__name">{name}</span>
        {w ? (
          <span className="a-slot__winner">
            <WinnerBadge size="sm" label={name} />
            <span>
              <strong>{w.participantName}</strong>
              {w.title && <> — {w.title}</>}{' '}
              <Link to={`/admin/submissions/${w.submissionId}`} className="tabular a-slot__ref">
                {w.reference}
              </Link>
              {votes !== undefined && <span className="a-muted tabular"> · {votes} səs</span>}
            </span>
          </span>
        ) : (
          <span className="a-muted">Seçilməyib</span>
        )}
      </span>
    </li>
  )
}

// ---- Winners, category by category ----------------------------------------------------------------

type SlotFilter = 'all' | 'chosen' | 'missing'

function WinnersPanel({ r, competition, onChange }: { r: AdminResult; competition: AdminCompetition; onChange: (r: AdminResult) => Promise<void> }) {
  const id = useId()
  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState<SlotFilter>('all')
  const [picking, setPicking] = useState<AdminWinnerSlot>()
  const [removing, setRemoving] = useState<AdminWinnerSlot>()
  const label = r.categoryLabel ?? 'Kateqoriyalar'
  const one = singularAz(label)

  const visible = useMemo(
    () =>
      r.slots.filter(
        (s) =>
          (filter === 'all' || (filter === 'chosen') === (s.winner !== null)) &&
          matchesSearch(s.category?.name ?? '', query),
      ),
    [r.slots, filter, query],
  )
  const chosen = r.slots.filter((s) => s.winner).length
  const filters: [SlotFilter, string, number][] = [
    ['all', 'Hamısı', r.slots.length],
    ['chosen', 'Qalib seçilib', chosen],
    ['missing', 'Qalib yoxdur', r.slots.length - chosen],
  ]

  return (
    <section className="a-panel" aria-labelledby={`${id}-title`}>
      <div className="a-panel__head">
        <h2 id={`${id}-title`} className="a-panel__title">
          {r.hasCategories ? `${label} üzrə qaliblər` : 'Qalib'}
        </h2>
        {r.hasCategories && (
          <span className="a-muted tabular">
            {chosen} / {r.slots.length}
          </span>
        )}
      </div>

      {r.hasCategories && (
        <div className="a-slotbar">
          <div className="a-field">
            <label className="a-field__label" htmlFor={`${id}-q`}>
              {one} axtar
            </label>
            <input
              id={`${id}-q`}
              type="search"
              className="input"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="məs. balaxani"
              autoComplete="off"
              spellCheck={false}
            />
          </div>
          <fieldset className="a-seg">
            <legend className="visually-hidden">Göstər</legend>
            {filters.map(([value, text, count]) => (
              <label key={value} className="a-seg__option">
                <input type="radio" name={`${id}-f`} value={value} checked={filter === value} onChange={() => setFilter(value)} />
                <span>
                  {text} <span className="tabular a-seg__n">{count}</span>
                </span>
              </label>
            ))}
          </fieldset>
        </div>
      )}

      <p className="visually-hidden" aria-live="polite">
        {visible.length} nəticə
      </p>
      {visible.length === 0 ? (
        <p className="a-muted">
          “{query}” üzrə {one.toLocaleLowerCase('az')} tapılmadı.{' '}
          <button type="button" className="a-link" onClick={() => { setQuery(''); setFilter('all') }}>
            Hamısını göstər
          </button>
        </p>
      ) : (
        <ul role="list" className="a-slots">
          {visible.map((s) => (
            <SlotRow
              key={s.category?.id ?? 'overall'}
              slot={s}
              competitionId={competition.id}
              one={one}
              published={r.state === 'Published'}
              onPick={() => setPicking(s)}
              onRemove={() => setRemoving(s)}
            />
          ))}
        </ul>
      )}

      {picking && (
        <WinnerPicker
          slot={picking}
          result={r}
          onClose={() => setPicking(undefined)}
          onDone={async (next) => {
            setPicking(undefined)
            await onChange(next)
          }}
        />
      )}
      {removing?.winner && (
        <RemoveDialog
          slot={removing}
          result={r}
          onClose={() => setRemoving(undefined)}
          onDone={async (next) => {
            setRemoving(undefined)
            await onChange(next)
          }}
        />
      )}
    </section>
  )
}

function SlotRow({ slot, competitionId, one, published, onPick, onRemove }: {
  slot: AdminWinnerSlot
  competitionId: number
  one: string
  published: boolean
  onPick: () => void
  onRemove: () => void
}) {
  const w = slot.winner
  const name = slot.category?.name ?? 'Müsabiqə'
  const entries = `/admin/submissions?competitionId=${competitionId}${slot.category ? `&categoryId=${slot.category.id}` : ''}`
  return (
    <li className={`a-slot${w ? ' is-won' : ' a-slot--open'}`}>
      {w && (
        <span className="a-slot__art">
          <PreviewThumb submissionId={w.submissionId} preview={w.preview} />
        </span>
      )}
      <span className="a-slot__main">
        <span className="a-slot__name">
          {name}
          {!slot.categoryActive && <span className="a-muted"> (deaktiv)</span>}
        </span>
        {w ? (
          <span className="a-slot__winner">
            <WinnerBadge size="sm" />
            <span>
              <strong>{w.participantName}</strong>
              {w.title && <> — {w.title}</>}{' '}
              <Link to={`/admin/submissions/${w.submissionId}`} className="tabular a-slot__ref">
                {w.reference}
              </Link>
            </span>
          </span>
        ) : (
          <span className="a-muted">
            Qalib seçilməyib
            {slot.eligibleCount > 0 ? (
              <>
                , <span className="tabular">{slot.eligibleCount}</span> iş
              </>
            ) : (
              ', iş yoxdur'
            )}
          </span>
        )}
      </span>
      <span className="a-slot__actions">
        {w ? (
          <>
            <Link to={`/admin/submissions/${w.submissionId}`} className="btn btn--sm btn--ghost">
              Qalib işə bax
            </Link>
            {slot.eligibleCount > 1 && (
              <button type="button" className="btn btn--sm" onClick={onPick} aria-label={`${name}: qalibi dəyiş`}>
                Qalibi dəyiş
              </button>
            )}
            <button type="button" className="btn btn--sm btn--ghost a-btn--danger" onClick={onRemove} aria-label={`${name}: qalibi ləğv et`}>
              Qalibi ləğv et
            </button>
          </>
        ) : slot.eligibleCount > 0 ? (
          <>
            <button type="button" className="btn btn--sm btn--primary" onClick={onPick} aria-label={`${name} üzrə qalib seç`}>
              Qalib seç
            </button>
            <Link to={entries} className="btn btn--sm btn--ghost">
              Bu {azGenitive(one.toLocaleLowerCase('az'))} işləri
            </Link>
          </>
        ) : null}
      </span>
      {published && w && <span className="visually-hidden">Dərc olunub</span>}
    </li>
  )
}

const ELIGIBLE = new Set(['Submitted', 'UnderReview', 'Approved'])

/** Choose (or replace) a category's winner among its eligible works, with thumbnails — never original files. */
function WinnerPicker({ slot, result, onClose, onDone }: {
  slot: AdminWinnerSlot
  result: AdminResult
  onClose: () => void
  onDone: (r: AdminResult) => Promise<void>
}) {
  const name = slot.category?.name ?? 'Müsabiqə'
  const current = slot.winner
  const published = result.state === 'Published'
  const [entries, setEntries] = useState<AdminSubmissionRow[]>()
  const [loadError, setLoadError] = useState<string>()
  const [choice, setChoice] = useState<AdminSubmissionRow>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  useEffect(() => {
    const ctrl = new AbortController()
    adminApi
      .submissions(
        {
          competitionId: String(result.competitionId),
          categoryId: slot.category ? String(slot.category.id) : undefined,
          pageSize: '100',
        },
        ctrl.signal,
      )
      .then((page) => setEntries(page.items.filter((s) => ELIGIBLE.has(s.status) && (slot.category || !s.category))))
      .catch((err: unknown) => {
        if (!ctrl.signal.aborted) setLoadError(readErrors(err, 'İşlər yüklənmədi.').message)
      })
    return () => ctrl.abort()
  }, [result.competitionId, slot.category])

  const confirm = async () => {
    if (!choice) return
    setBusy(true)
    setError(undefined)
    try {
      const next = current
        ? await adminApi.changeWinner(result.competitionId, choice.id, current.submissionId, published)
        : await adminApi.selectWinner(result.competitionId, choice.id, published)
      await onDone(next)
    } catch (err) {
      setError(readErrors(err, 'Qalib seçilmədi.').message)
      setBusy(false)
    }
  }

  return (
    <ConfirmDialog
      title={current ? `${name} üzrə qalibi dəyişmək` : `${name} üzrə qalib seçin`}
      confirmLabel={current ? 'Qalibi dəyiş' : 'Qalib seç'}
      onConfirm={confirm}
      onCancel={onClose}
      busy={busy}
      confirmDisabled={!choice}
      error={error}
    >
      {published && <PublishedWarning publicNow={result.isPublicNow} />}
      {current && (
        <p>
          Hazırkı qalib: <strong>{current.participantName}</strong> — {current.title}{' '}
          <span className="tabular">{current.reference}</span>
        </p>
      )}
      {loadError && <Notice tone="error">{loadError}</Notice>}
      {!entries && !loadError && <p className="a-muted">İşlər yüklənir…</p>}
      {entries && entries.length === 0 && <p className="a-muted">{name} üzrə qalib seçilə bilən iş yoxdur.</p>}
      {entries && entries.length > 0 && (
        <fieldset className="a-pick">
          <legend className="visually-hidden">{name} üzrə işlər</legend>
          {entries.map((s) => {
            const isCurrent = current?.submissionId === s.id
            return (
              <label key={s.id} className={`a-pick__option${isCurrent ? ' is-current' : ''}`}>
                <input
                  type="radio"
                  name="winner-choice"
                  value={s.id}
                  disabled={isCurrent}
                  checked={choice?.id === s.id}
                  onChange={() => setChoice(s)}
                />
                <span className="a-pick__art">
                  <PreviewThumb submissionId={s.id} preview={s.preview} pending="Hazırlanır" />
                </span>
                <span className="a-pick__text">
                  <strong>{s.title || 'Adsız iş'}</strong>
                  <span>{s.participant.name}</span>
                  <span className="tabular a-muted">
                    {s.referenceNumber}
                    {isCurrent && ' — hazırkı qalib'}
                  </span>
                </span>
              </label>
            )
          })}
        </fieldset>
      )}
    </ConfirmDialog>
  )
}

function RemoveDialog({ slot, result, onClose, onDone }: {
  slot: AdminWinnerSlot
  result: AdminResult
  onClose: () => void
  onDone: (r: AdminResult) => Promise<void>
}) {
  const w = slot.winner!
  const name = slot.category?.name ?? 'Müsabiqə'
  const published = result.state === 'Published'
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  return (
    <ConfirmDialog
      title={`${name} üzrə qalibi ləğv etmək istəyirsiniz?`}
      confirmLabel="Bəli, ləğv et"
      busy={busy}
      error={error}
      onCancel={onClose}
      onConfirm={async () => {
        setBusy(true)
        setError(undefined)
        try {
          await onDone(await adminApi.removeWinner(result.competitionId, w.submissionId, published))
        } catch (err) {
          setError(readErrors(err, 'Ləğv edilmədi.').message)
          setBusy(false)
        }
      }}
    >
      {published && <PublishedWarning publicNow={result.isPublicNow} />}
      <p>
        <strong>{w.participantName}</strong> — {w.title} <span className="tabular">{w.reference}</span>
      </p>
      <p className="a-muted">İşin özü və statusu dəyişmir; {name} sadəcə qalibsiz qalır.</p>
    </ConfirmDialog>
  )
}

/** A change to a published result: public at once — or, while the results are hidden, once they are visible again. */
function PublishedWarning({ publicNow }: { publicNow: boolean }) {
  return (
    <Notice tone="warn">
      {publicNow
        ? 'Nəticələr artıq dərc olunub: dəyişiklik saytda dərhal görünəcək.'
        : 'Nəticələr dərc olunub (hazırda saytda görünmür): dəyişiklik nəticələr yenidən görünəndə saytda olacaq.'}
    </Notice>
  )
}

// ---- Publishing -------------------------------------------------------------------------------------

function PublishPanel({ r, onChange }: { r: AdminResult; onChange: (r: AdminResult) => Promise<void> }) {
  const [dialog, setDialog] = useState<'publish' | 'unpublish' | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [announcement, setAnnouncement] = useState(r.announcement ?? '')
  const [saved, setSaved] = useState<string>()
  const published = r.state === 'Published'
  const canPublish = r.state === 'Prepared' && r.publishProblems.length === 0
  const one = singularAz(r.categoryLabel ?? 'Kateqoriyalar').toLocaleLowerCase('az')
  const missing = r.categoryCount - r.winnerCount
  // "59 qəsəbədən 17-si üzrə qalib seçilib. 42 qəsəbədə qalib yoxdur."
  const counts = `${r.categoryCount} ${azAblative(one)} ${azCountOf(r.winnerCount)} üzrə qalib seçilib.`
  const gaps = missing > 0 ? ` ${missing} ${azLocative(one)} qalib yoxdur.` : ''

  const run = async (action: () => Promise<AdminResult>, done: () => void) => {
    setBusy(true)
    setError(undefined)
    try {
      await onChange(await action())
      done()
    } catch (err) {
      const { message, lists } = readErrors(err, 'Alınmadı.')
      setError(lists.result?.join(' ') ?? message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="a-panel" aria-labelledby="pub-title">
      <div className="a-panel__head">
        <h2 id="pub-title" className="a-panel__title">
          Nəticələr
        </h2>
        {published ? (
          <Badge tone="live">Dərc olunub</Badge>
        ) : (
          <Badge tone="draft">{r.state === 'Prepared' ? 'Hazırlanır' : 'Qalib yoxdur'}</Badge>
        )}
      </div>

      {r.hasCategories && (
        <p className="a-muted tabular">
          {counts}
          {gaps}
        </p>
      )}

      {published ? (
        <>
          <p className="a-muted">
            Dərc etdi: {r.publishedBy ?? '—'}, <span className="tabular">{formatDateTime(r.publishedAt)}</span>
          </p>
          {!r.isPublicNow && (
            <Notice tone="warn">Nəticə dərc olunub, amma hazırda saytda görünmür: müsabiqə yenidən qəbul açıb və ya saytdan götürülüb.</Notice>
          )}
          {r.isPublicNow && (
            <p className="a-inline">
              <a href={`/competitions/${r.competitionSlug}/results`} target="_blank" rel="noreferrer">
                Nəticə səhifəsi
              </a>
              <a href={`/competitions/${r.competitionSlug}/gallery`} target="_blank" rel="noreferrer">
                Qalereya
              </a>
            </p>
          )}
          <p className="a-muted">
            Qaliblərdə dəyişiklik edə bilərsiniz — hər dəyişiklik əvvəlcə təsdiq istəyir
            {r.isPublicNow ? ' və saytda dərhal görünür.' : ' və nəticələr yenidən saytda görünəndə görünəcək.'}
          </p>
        </>
      ) : (
        <>
          <p className="a-muted">Qaliblər seçilsə də nəticə dərc olunana qədər saytda heç kim onları görmür.</p>
          {r.state !== 'NoWinner' && r.publishProblems.length > 0 && (
            <Notice tone="warn">
              Dərc etmək hələ mümkün deyil:
              <ul>
                {r.publishProblems.map((p) => (
                  <li key={p}>{p}</li>
                ))}
              </ul>
            </Notice>
          )}
        </>
      )}

      {error && !dialog && <Notice tone="error">{error}</Notice>}

      <AField label="Elan mətni (istəyə bağlı)" hint="Nəticə səhifəsinin başında görünür.">
        <textarea
          className="input"
          rows={3}
          maxLength={2000}
          value={announcement}
          onChange={(e) => {
            setAnnouncement(e.target.value)
            setSaved(undefined)
          }}
        />
      </AField>
      <div className="a-actions">
        <button
          type="button"
          className="btn btn--sm"
          disabled={busy || announcement === (r.announcement ?? '')}
          onClick={() => run(() => adminApi.resultAnnouncement(r.competitionId, announcement), () => setSaved('Mətn yadda saxlanıldı.'))}
        >
          Mətni yadda saxla
        </button>
        {saved && <span className="a-muted">{saved}</span>}
      </div>

      <div className="a-actions a-publish-actions">
        {published ? (
          <button type="button" className="btn btn--sm" onClick={() => setDialog('unpublish')}>
            Dərcdən çıxar
          </button>
        ) : (
          <button type="button" className="btn btn--primary" disabled={!canPublish} onClick={() => setDialog('publish')}>
            Nəticəni dərc et
          </button>
        )}
      </div>

      {dialog === 'publish' && (
        <ConfirmDialog
          title="Mövcud nəticələri dərc etmək istəyirsiniz?"
          confirmLabel="Bəli, dərc et"
          busy={busy}
          error={error}
          onCancel={() => setDialog(null)}
          onConfirm={() => run(() => adminApi.publishResult(r.competitionId), () => setDialog(null))}
        >
          {r.hasCategories ? (
            <p>
              <strong>{counts}</strong>
              {missing > 0 && `${gaps} Saytda onlar üçün “Qalib hələ müəyyən edilməyib” yazılacaq.`}
            </p>
          ) : (
            <ul className="a-list">
              {r.places
                .filter((p) => p.winner)
                .map((p) => (
                  <li key={p.place}>
                    {placeName(p.place)}: <strong>{p.winner!.participantName}</strong> — {p.winner!.title}
                  </li>
                ))}
            </ul>
          )}
          <p>Qaliblərin adı və soyadı nəticə səhifəsində göstəriləcək. Digər iştirakçıların adı gizli qalır.</p>
          <p className="a-muted">Qalereyada rədd edilməmiş {r.eligibleCount} iş görünəcək.</p>
        </ConfirmDialog>
      )}
      {dialog === 'unpublish' && (
        <ConfirmDialog
          title="Nəticəni dərcdən çıxarmaq istəyirsiniz?"
          confirmLabel="Bəli, dərcdən çıxar"
          busy={busy}
          error={error}
          onCancel={() => setDialog(null)}
          onConfirm={() => run(() => adminApi.unpublishResult(r.competitionId), () => setDialog(null))}
        >
          <p>Nəticə səhifəsi və qalereya saytdan götürüləcək. Seçilmiş qaliblər saxlanılır.</p>
        </ConfirmDialog>
      )}
    </section>
  )
}

// ---- Results banner ---------------------------------------------------------------------------------

/** "Nəticə cover şəkli": the banner of the public results pages, previewed before it is saved. */
function CoverPanel({ r, onChange }: { r: AdminResult; onChange: (r: AdminResult) => Promise<void> }) {
  const input = useRef<HTMLInputElement>(null)
  const [picked, setPicked] = useState<{ file: File; url: string }>()
  const [progress, setProgress] = useState<number>()
  const [error, setError] = useState<string>()
  const [confirmRemove, setConfirmRemove] = useState(false)

  useEffect(() => () => {
    if (picked) URL.revokeObjectURL(picked.url)
  }, [picked])

  const save = () => {
    if (!picked) return
    setError(undefined)
    setProgress(0)
    adminApi
      .uploadResultCover(r.competitionId, picked.file, setProgress)
      .promise.then(async (next) => {
        setPicked(undefined)
        await onChange(next)
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return
        const { message, fields } = readErrors(err, 'Şəkil yüklənmədi.')
        setError(fields.cover ?? message)
      })
      .finally(() => setProgress(undefined))
  }

  const shown = picked?.url ?? r.coverImage
  return (
    <section className="a-panel" aria-labelledby="rcover-title">
      <h2 id="rcover-title" className="a-panel__title">
        Nəticə cover şəkli
      </h2>
      <p className="a-muted">
        Nəticələr səhifəsinin başında və nəticələr siyahısında göstərilir (məs. təbrik posteri). Qalib işin şəkli avtomatik
        götürülmür. PNG, JPG və ya WEBP; metadata silinir.
      </p>
      <div className={`a-cover${shown ? '' : ' a-cover--empty'}`}>
        {shown ? <img src={shown} alt={picked ? 'Seçilmiş şəklin önizləməsi' : 'Hazırkı nəticə cover şəkli'} /> : <span>Cover şəkli yoxdur</span>}
        {picked && <span className="a-cover__flag">Önizləmə — hələ yadda saxlanmayıb</span>}
      </div>
      {error && <Notice tone="error">{error}</Notice>}
      {progress !== undefined && <p className="a-muted tabular">Yüklənir… {Math.round(progress * 100)}%</p>}
      <input
        ref={input}
        type="file"
        accept=".png,.jpg,.jpeg,.webp,image/png,image/jpeg,image/webp"
        className="visually-hidden"
        aria-label="Nəticə cover şəklini seçin"
        onChange={(e) => {
          const file = e.target.files?.[0]
          if (file) setPicked({ file, url: URL.createObjectURL(file) })
          setError(undefined)
          e.target.value = ''
        }}
      />
      <div className="a-actions">
        {picked ? (
          <>
            <button type="button" className="btn btn--sm btn--primary" onClick={save} disabled={progress !== undefined}>
              Yadda saxla
            </button>
            <button type="button" className="btn btn--sm btn--ghost" onClick={() => setPicked(undefined)} disabled={progress !== undefined}>
              Ləğv et
            </button>
          </>
        ) : (
          <>
            <button type="button" className="btn btn--sm" onClick={() => input.current?.click()}>
              {r.coverImage ? 'Şəkli dəyiş' : 'Şəkil seç'}
            </button>
            {r.coverImage && (
              <button type="button" className="btn btn--sm btn--ghost a-btn--danger" onClick={() => setConfirmRemove(true)}>
                Şəkli sil
              </button>
            )}
          </>
        )}
      </div>
      {confirmRemove && (
        <ConfirmDialog
          title="Nəticə cover şəklini silmək istəyirsiniz?"
          confirmLabel="Bəli, sil"
          onCancel={() => setConfirmRemove(false)}
          onConfirm={async () => {
            setConfirmRemove(false)
            try {
              await onChange(await adminApi.removeResultCover(r.competitionId))
            } catch (err) {
              setError(readErrors(err, 'Şəkil silinmədi.').message)
            }
          }}
        >
          <p>Nəticələr səhifəsində layihənin örtük şəkli (varsa) göstəriləcək.</p>
        </ConfirmDialog>
      )}
    </section>
  )
}

// ---- What the public sees ---------------------------------------------------------------------------

function VisibilityPanel({ r }: { r: AdminResult }) {
  return (
    <section className="a-panel" aria-labelledby="vis-title">
      <h2 id="vis-title" className="a-panel__title">
        Nəticədən sonra saytda nə görünür
      </h2>
      <ul className="a-list">
        <li>Qaliblərin adı və soyadı (yalnız qaliblərin)</li>
        <li>İstinad nömrəsi{r.categoryLabel ? ` və ${singularAz(r.categoryLabel).toLocaleLowerCase('az')}` : ''}</li>
        {r.publicFields.map((f) => (
          <li key={f}>{f}</li>
        ))}
      </ul>
      {!r.hasPublicFileField && (
        <Notice tone="warn">
          Heç bir fayl sahəsi ictimai deyil — nəticələrdə və qalereyada işlərin şəkli görünməyəcək. <Link to="?tab=form">Təqdimat formasında</Link>{' '}
          fayl sahəsini «nəticədən sonra ictimai» kimi qeyd edin.
        </Notice>
      )}
      <p className="a-muted">
        Qalib olmayan iştirakçıların adı, heç kimin e-maili, telefonu, faylların orijinal adı, daxili qeydlər və moderasiya statusu
        göstərilmir. Rədd edilmiş işlər qalereyaya düşmür.
      </p>
      {r.assetsPending > 0 && (
        <Notice>
          <span className="tabular">{r.assetsPending}</span> fayl üçün təhlükəsiz nüsxə hazırlanır. Hazır olana qədər saytda “hazırlanır” yazısı
          görünür.
        </Notice>
      )}
      {r.assetFailures.length > 0 && (
        <Notice tone="warn">
          Bu faylların təhlükəsiz nüsxəsi yaradılmadı — saytda orijinal yox, yer tutucu göstəriləcək:
          <ul>
            {r.assetFailures.map((f) => (
              <li key={f.fileId}>
                <Link to={`/admin/submissions/${f.submissionId}`} className="tabular">
                  {f.reference}
                </Link>
              </li>
            ))}
          </ul>
        </Notice>
      )}
    </section>
  )
}
