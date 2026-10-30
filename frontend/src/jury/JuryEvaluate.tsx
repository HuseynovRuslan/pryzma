import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, Navigate, useNavigate, useParams } from 'react-router-dom'
import { ApiError } from '../api/client'
import { VoteBar, VoteBarKey } from '../components/VoteBar'
import { formatDateTime } from '../lib/format'
import { placeName } from '../lib/text'
import { useTitle } from '../lib/useTitle'
import { catalogNumber } from '../pages/voting/lib'
import { juryApi, juryImageUrl, type JurorDashboard, type JurorPublicVote, type JurorSession, type JurorWork, type JurorWorkItem } from './api'
import { JuryShell } from './JuryShell'

export const SCORES = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] as const

/** What the juror is asked, at the top of the list and of every work. */
export const JUROR_INSTRUCTION =
  'Hər işə 1-dən 10-a qədər tam bal verin. İşlər xalq səsverməsinin etibarlı səslərinə görə sıralanıb; yer və səs sayları məlumat üçündür.'

/** "1-ci yer" — and, when level works share it, how many. */
const placeText = (v: JurorPublicVote) => `${placeName(v.position)}${v.tieSize > 1 ? ` · bərabər (${v.tieSize} iş)` : ''}`

/** The signed-in, confirmed juror — or the way back to the start. */
function useJuror() {
  const [session, setSession] = useState<JurorSession | null | undefined>(undefined)
  useEffect(() => {
    const controller = new AbortController()
    juryApi
      .session(controller.signal)
      .then(setSession)
      .catch(() => {
        if (!controller.signal.aborted) setSession(null)
      })
    return () => controller.abort()
  }, [])
  return session
}

/** /jury/evaluate and /jury/evaluate/:submissionId. */
export function JuryEvaluatePage() {
  const { submissionId } = useParams()
  const session = useJuror()
  if (session === undefined)
    return (
      <JuryShell>
        <p className="jr-muted jr-pad">Yüklənir…</p>
      </JuryShell>
    )
  if (session === null || !session.confirmed) return <Navigate to="/jury/start" replace />
  return (
    <JuryShell session={session}>
      {submissionId ? <WorkView key={submissionId} submissionId={Number(submissionId)} /> : <Dashboard />}
    </JuryShell>
  )
}

// ---- The list of works -----------------------------------------------------------------------------------------

type Filter = 'all' | 'todo' | 'done'

function Dashboard() {
  const navigate = useNavigate()
  const [d, setD] = useState<JurorDashboard>()
  const [error, setError] = useState<string>()
  const [filter, setFilter] = useState<Filter>('all')
  const [confirming, setConfirming] = useState(false)
  const [busy, setBusy] = useState(false)
  useTitle(d ? `${d.competitionTitle} — münsif` : 'Münsif paneli')

  useEffect(() => {
    const controller = new AbortController()
    juryApi
      .dashboard(controller.signal)
      .then(setD)
      .catch((err: unknown) => {
        if (controller.signal.aborted) return
        if (err instanceof ApiError && err.status === 401) navigate('/jury/start', { replace: true })
        else setError(err instanceof ApiError ? err.message : 'Siyahı yüklənmədi.')
      })
    return () => controller.abort()
  }, [navigate])

  const shown = useMemo(
    () => d?.works.filter((w) => (filter === 'all' ? true : filter === 'todo' ? w.score === null : w.score !== null)) ?? [],
    [d, filter],
  )

  if (error)
    return (
      <p className="jr-error jr-pad" role="alert">
        {error}
      </p>
    )
  if (!d) return <p className="jr-muted jr-pad">Yüklənir…</p>

  const next = d.works.find((w) => w.score === null)
  const percent = d.total === 0 ? 0 : Math.round((d.scored / d.total) * 100)
  // Every bar against the most-voted work, as on the printed catalog.
  const max = Math.max(1, ...d.works.map((w) => w.vote.validVotes + w.vote.invalidatedVotes))

  const submit = async () => {
    setBusy(true)
    setError(undefined)
    try {
      setD(await juryApi.submit())
      setConfirming(false)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Yekunlaşdırmaq alınmadı.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="jr-dash">
      <section className="jr-hero" aria-labelledby="jr-hero-title">
        <p className="jr-eyebrow">{d.competitionTitle}</p>
        <h1 id="jr-hero-title" className="jr-hero__title">
          Bütün işlərin qiymətləndirilməsi
        </h1>
        <p className="jr-hero__text">{JUROR_INSTRUCTION}</p>
        <div className="jr-progress" aria-label="İrəliləyiş">
          <div className="jr-progress__numbers">
            <span className="jr-progress__big tabular">{d.scored}</span>
            <span className="jr-progress__of tabular">/ {d.total} iş qiymətləndirilib</span>
          </div>
          <div
            className="jr-progress__bar"
            role="progressbar"
            aria-valuemin={0}
            aria-valuemax={d.total}
            aria-valuenow={d.scored}
            aria-valuetext={`${d.scored} / ${d.total}`}
          >
            <span style={{ width: `${percent}%` }} />
          </div>
        </div>
        {d.locked ? (
          <p className="jr-locked" role="status">
            Qiymətləndirməniz {d.submittedAt ? formatDateTime(d.submittedAt) : ''} yekunlaşdırılıb və kilidlənib. Təşəkkür
            edirik! Dəyişiklik lazım olsa, təşkilatçıya müraciət edin.
          </p>
        ) : !d.open ? (
          <p className="jr-locked" role="status">
            Qiymətləndirmə hazırda açıq deyil.
          </p>
        ) : (
          <div className="jr-hero__actions">
            {next ? (
              <Link to={`/jury/evaluate/${next.submissionId}`} className="btn btn--primary">
                {d.scored === 0 ? 'Qiymətləndirməyə başla' : 'Növbəti qiymətləndirilməmiş iş'}
              </Link>
            ) : (
              <button type="button" className="btn btn--primary" onClick={() => setConfirming(true)}>
                Qiymətləndirməni yekunlaşdır
              </button>
            )}
            {next && d.remaining > 0 && (
              <span className="jr-muted tabular">
                Qalıb: {d.remaining} iş
              </span>
            )}
          </div>
        )}
        {confirming && (
          <div className="jr-confirm" role="alertdialog" aria-labelledby="jr-confirm-title" aria-describedby="jr-confirm-text">
            <h2 id="jr-confirm-title" className="jr-confirm__title">
              Qiymətləndirməni yekunlaşdırırsınız?
            </h2>
            <p id="jr-confirm-text">
              {d.total} işin hamısına bal vermisiniz. Yekunlaşdırdıqdan sonra ballar kilidlənir; dəyişmək üçün təşkilatçıya
              müraciət etməli olacaqsınız.
            </p>
            <div className="jr-hero__actions">
              <button type="button" className="btn btn--primary" onClick={submit} disabled={busy}>
                {busy ? 'Göndərilir…' : 'Bəli, yekunlaşdır'}
              </button>
              <button type="button" className="btn" onClick={() => setConfirming(false)} disabled={busy}>
                Geri
              </button>
            </div>
          </div>
        )}
      </section>

      <div className="jr-filter" role="group" aria-label="Göstər">
        {(
          [
            ['all', `Hamısı (${d.total})`],
            ['todo', `Qiymətləndirilməyib (${d.remaining})`],
            ['done', `Qiymətləndirilib (${d.scored})`],
          ] as const
        ).map(([key, label]) => (
          <button key={key} type="button" className="jr-chip" aria-pressed={filter === key} onClick={() => setFilter(key)}>
            {label}
          </button>
        ))}
      </div>

      <p className="jr-key jr-muted">
        Xalq səsverməsi: <VoteBarKey />
      </p>

      {shown.length === 0 ? (
        <p className="jr-muted jr-pad">Bu siyahıda iş yoxdur.</p>
      ) : (
        <ol className="jr-grid" role="list">
          {shown.map((w) => (
            <li key={w.submissionId}>
              <WorkCard w={w} max={max} />
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}

function WorkCard({ w, max }: { w: JurorWorkItem; max: number }) {
  return (
    <Link to={`/jury/evaluate/${w.submissionId}`} className={`jr-card${w.score !== null ? ' is-scored' : ''}`}>
      <span className="jr-card__art">
        {w.image ? (
          <img src={juryImageUrl(w.submissionId, w.image.fileId, w.image.page ?? 1, true)} alt="" loading="lazy" decoding="async" />
        ) : (
          <span className="jr-card__none">Şəkil yoxdur</span>
        )}
      </span>
      <span className="jr-card__body">
        <span className="jr-card__head tabular">
          <span className="jr-card__pos">{placeName(w.vote.position)}</span>
          {w.vote.tieSize > 1 && <span className="jr-card__tie">bərabər</span>}
        </span>
        <span className="jr-card__ref tabular">{catalogNumber(w.reference)}</span>
        <span className="jr-card__title">{w.title ?? 'Adsız iş'}</span>
        <span className="jr-card__votes tabular">
          {w.vote.validVotes} etibarlı{w.vote.invalidatedVotes > 0 && ` · ${w.vote.invalidatedVotes} etibarsız`}
        </span>
        <VoteBar valid={w.vote.validVotes} invalid={w.vote.invalidatedVotes} max={max} className="jr-card__bar" />
      </span>
      <span className={`jr-card__score tabular${w.score === null ? ' is-empty' : ''}`} aria-label={w.score === null ? 'Bal verilməyib' : `Balınız: ${w.score}`}>
        {w.score ?? '—'}
      </span>
    </Link>
  )
}

// ---- One work ---------------------------------------------------------------------------------------------------

type SaveState =
  | { kind: 'idle' }
  | { kind: 'saving' }
  | { kind: 'saved'; at: Date }
  | { kind: 'conflict'; message: string }
  | { kind: 'error'; message: string }

const COMMENT_DELAY = 900

function WorkView({ submissionId }: { submissionId: number }) {
  const navigate = useNavigate()
  const [work, setWork] = useState<JurorWork>()
  const [loadError, setLoadError] = useState<string>()
  const [score, setScore] = useState<number | null>(null)
  const [comment, setComment] = useState('')
  const [save, setSave] = useState<SaveState>({ kind: 'idle' })
  const [shown, setShown] = useState(0)
  const [progress, setProgress] = useState<{ scored: number; total: number }>()
  const version = useRef<number | null>(null)
  const chain = useRef<Promise<unknown>>(Promise.resolve())
  const commentTimer = useRef<number | undefined>(undefined)
  const savedComment = useRef('')
  useTitle(work ? `${catalogNumber(work.reference)} — münsif` : 'Münsif paneli')

  const load = useCallback(
    (signal?: AbortSignal) =>
      juryApi
        .work(submissionId, signal)
        .then((w) => {
          setWork(w)
          setScore(w.score)
          setComment(w.comment ?? '')
          savedComment.current = w.comment ?? ''
          version.current = w.version
          setSave({ kind: 'idle' })
        })
        .catch((err: unknown) => {
          if (signal?.aborted) return
          if (err instanceof ApiError && err.status === 401) navigate('/jury/start', { replace: true })
          else setLoadError(err instanceof ApiError ? err.message : 'İş yüklənmədi.')
        }),
    [submissionId, navigate],
  )

  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => {
      controller.abort()
      window.clearTimeout(commentTimer.current)
    }
  }, [load])

  /** Saves one at a time, each on the version the one before it returned — never two racing each other. */
  const persist = useCallback(
    (nextScore: number, nextComment: string) => {
      setSave({ kind: 'saving' })
      chain.current = chain.current.then(async () => {
        try {
          const text = nextComment.trim() === '' ? null : nextComment.trim()
          const r = await juryApi.score(submissionId, nextScore, text, version.current)
          version.current = r.version
          savedComment.current = r.comment ?? ''
          setProgress({ scored: r.scored, total: r.total })
          setSave({ kind: 'saved', at: new Date() })
        } catch (err) {
          if (err instanceof ApiError && err.status === 409)
            setSave({ kind: 'conflict', message: err.message })
          else if (err instanceof ApiError && err.status === 401) navigate('/jury/start', { replace: true })
          else setSave({ kind: 'error', message: err instanceof ApiError ? err.message : 'Yadda saxlanmadı.' })
        }
      })
    },
    [submissionId, navigate],
  )

  const choose = useCallback(
    (value: number) => {
      if (!work || work.locked) return
      window.clearTimeout(commentTimer.current)
      setScore(value)
      persist(value, comment)
    },
    [work, comment, persist],
  )

  const onComment = (text: string) => {
    setComment(text)
    window.clearTimeout(commentTimer.current)
    if (score === null || work?.locked) return
    commentTimer.current = window.setTimeout(() => persist(score, text), COMMENT_DELAY)
  }

  const flushComment = () => {
    window.clearTimeout(commentTimer.current)
    if (score !== null && !work?.locked && comment.trim() !== savedComment.current.trim()) persist(score, comment)
  }

  // The latest note flush, for moving on from the keyboard without re-binding the listener on every keystroke.
  const flushCommentRef = useRef(flushComment)
  useEffect(() => {
    flushCommentRef.current = flushComment
  })
  const go = useCallback(
    (id: number | null | undefined) => {
      if (!id) return
      flushCommentRef.current()
      navigate(`/jury/evaluate/${id}`)
    },
    [navigate],
  )

  // Keys: 1–9 and 0 (10) score the work; ← → move along the list. Not while typing a note.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null
      if (e.altKey || e.ctrlKey || e.metaKey) return
      if (target && (target.tagName === 'TEXTAREA' || target.tagName === 'INPUT' || target.isContentEditable)) return
      if (/^[0-9]$/.test(e.key)) {
        e.preventDefault()
        choose(e.key === '0' ? 10 : Number(e.key))
      } else if (e.key === 'ArrowRight') {
        e.preventDefault()
        go(work?.nextId)
      } else if (e.key === 'ArrowLeft') {
        e.preventDefault()
        go(work?.previousId)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [choose, go, work?.nextId, work?.previousId])

  if (loadError)
    return (
      <div className="jr-pad">
        <p className="jr-error" role="alert">
          {loadError}
        </p>
        <Link to="/jury/evaluate" className="btn">
          Siyahıya qayıt
        </Link>
      </div>
    )
  if (!work) return <p className="jr-muted jr-pad">Yüklənir…</p>

  const pages = work.material.length > 0 ? work.material : work.image ? [{ fileId: work.image.fileId, kind: 'image' as const, page: work.image.page ?? 1, pages: 1 }] : []
  const current = pages[Math.min(shown, Math.max(0, pages.length - 1))]

  return (
    <div className="jr-work">
      <nav className="jr-work__nav" aria-label="İşlər arasında">
        <Link to="/jury/evaluate" className="jr-link" onClick={flushComment}>
          ← Bütün işlər
        </Link>
        <span className="jr-work__count tabular">
          {work.index} / {work.total}
          {progress && (
            <span className="jr-muted">
              {' '}
              · qiymətləndirilib {progress.scored}
            </span>
          )}
        </span>
        <span className="jr-work__step">
          <button type="button" className="btn" onClick={() => go(work.previousId)} disabled={!work.previousId} aria-keyshortcuts="ArrowLeft">
            ← Əvvəlki
          </button>
          <button type="button" className="btn" onClick={() => go(work.nextId)} disabled={!work.nextId} aria-keyshortcuts="ArrowRight">
            Növbəti →
          </button>
        </span>
      </nav>

      <div className="jr-work__layout">
        <section className="jr-viewer" aria-label="İşin materialı">
          <figure className="jr-viewer__stage">
            {current ? (
              <a href={juryImageUrl(work.submissionId, current.fileId, current.page)} target="_blank" rel="noreferrer" title="Tam ölçüdə aç">
                <img
                  src={juryImageUrl(work.submissionId, current.fileId, current.page)}
                  alt={`${catalogNumber(work.reference)} — ${current.kind === 'pdf' ? `PDF səhifə ${current.page}` : 'şəkil'}`}
                  decoding="async"
                />
              </a>
            ) : (
              <span className="jr-viewer__none">Bu işin ictimai şəkli yoxdur</span>
            )}
            {current && (
              <figcaption className="jr-viewer__caption tabular">
                {current.kind === 'pdf' ? `PDF · səhifə ${current.page} / ${current.pages}` : 'Şəkil'} · material {Math.min(shown, pages.length - 1) + 1} / {pages.length}
              </figcaption>
            )}
          </figure>
          {pages.length > 1 && (
            <ol className="jr-strip" role="list" aria-label="Materialın səhifələri">
              {pages.map((m, i) => (
                <li key={`${m.fileId}-${m.page}`}>
                  <button
                    type="button"
                    className="jr-strip__item"
                    aria-current={i === shown ? 'true' : undefined}
                    aria-label={m.kind === 'pdf' ? `PDF səhifə ${m.page}` : `Şəkil ${i + 1}`}
                    onClick={() => setShown(i)}
                  >
                    <img src={juryImageUrl(work.submissionId, m.fileId, m.page, true)} alt="" loading="lazy" decoding="async" />
                  </button>
                </li>
              ))}
            </ol>
          )}
        </section>

        <aside className="jr-panel" aria-labelledby="jr-work-title">
          <p className="jr-work__ref tabular">
            {catalogNumber(work.reference)}
            {work.pdfPages > 0 && <span className="jr-tag">PDF · {work.pdfPages} səhifə</span>}
          </p>
          <h1 id="jr-work-title" className="jr-work__title">
            {work.title ?? 'Adsız iş'}
          </h1>
          <section className="jr-vote" aria-label="Xalq səsverməsi">
            <p className="jr-vote__place">
              <strong>{placeText(work.vote)}</strong> <span className="jr-muted">xalq səsverməsində</span>
            </p>
            <p className="jr-vote__counts tabular">
              <strong>{work.vote.validVotes}</strong> etibarlı səs
              {work.vote.invalidatedVotes > 0 ? (
                <span className="jr-vote__invalid"> · {work.vote.invalidatedVotes} audit qərarı ilə etibarsız sayılıb</span>
              ) : (
                <span className="jr-muted"> · etibarsız səs yoxdur</span>
              )}
            </p>
            <VoteBar valid={work.vote.validVotes} invalid={work.vote.invalidatedVotes} />
            <p className="jr-vote__key jr-muted">
              <VoteBarKey />
            </p>
          </section>
          {(work.category || work.facts.length > 0) && (
            <p className="jr-work__meta">
              {[...(work.category ? [work.category] : []), ...work.facts.map((f) => `${f.label}: ${f.value}`)].join(' · ')}
            </p>
          )}

          <fieldset className="jr-score" disabled={work.locked}>
            <legend className="jr-score__legend">
              Balınız <span className="jr-muted">(1–10, tam ədəd)</span>
            </legend>
            <div className="jr-score__buttons" role="radiogroup" aria-label="Bal">
              {SCORES.map((n) => (
                <button
                  key={n}
                  type="button"
                  role="radio"
                  aria-checked={score === n}
                  className="jr-score__btn tabular"
                  onClick={() => choose(n)}
                  aria-keyshortcuts={n === 10 ? '0' : String(n)}
                >
                  {n}
                </button>
              ))}
            </div>
            <label className="jr-field">
              <span className="jr-field__label">
                Qeyd <span className="jr-muted">(istəyə bağlı, yalnız təşkilatçı görür)</span>
              </span>
              <textarea
                className="jr-note"
                rows={3}
                maxLength={1000}
                value={comment}
                onChange={(e) => onComment(e.target.value)}
                onBlur={flushComment}
                placeholder={score === null ? 'Qeyd bal seçildikdən sonra yadda saxlanılır' : ''}
              />
            </label>
          </fieldset>

          <p className={`jr-save jr-save--${save.kind}`} role="status" aria-live="polite">
            {work.locked
              ? 'Qiymətləndirmə kilidlidir — bal dəyişmir.'
              : save.kind === 'saving'
                ? 'Yadda saxlanılır…'
                : save.kind === 'saved'
                  ? `Yadda saxlanıldı · ${save.at.toLocaleTimeString('az', { hour: '2-digit', minute: '2-digit' })}`
                  : save.kind === 'conflict'
                    ? save.message
                    : save.kind === 'error'
                      ? save.message
                      : score === null
                        ? 'Bal seçin — seçim dərhal yadda saxlanılır.'
                        : 'Yadda saxlanılıb.'}
          </p>
          {save.kind === 'conflict' && (
            <button type="button" className="btn" onClick={() => void load()}>
              Ən son balı yüklə
            </button>
          )}
          {save.kind === 'error' && score !== null && (
            <button type="button" className="btn" onClick={() => persist(score, comment)}>
              Yenidən cəhd et
            </button>
          )}

          <div className="jr-panel__next">
            {work.nextId ? (
              <button type="button" className="btn btn--primary" onClick={() => go(work.nextId)}>
                Növbəti iş →
              </button>
            ) : (
              <Link to="/jury/evaluate" className="btn btn--primary" onClick={flushComment}>
                Siyahıya qayıt və yekunlaşdır
              </Link>
            )}
            <p className="jr-hint">Klaviatura: 1–9, 0 = 10 · ← → işlər arasında</p>
          </div>

          {work.concept && (
            <section className="jr-concept" aria-label={work.conceptLabel ?? 'Təsvir'}>
              {work.conceptLabel && <h2 className="jr-concept__label">{work.conceptLabel}</h2>}
              <p className="jr-concept__text">{work.concept}</p>
            </section>
          )}
        </aside>
      </div>
    </div>
  )
}
