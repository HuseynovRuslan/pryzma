import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { votingApi } from '../../api/endpoints'
import type { GalleryFile, Voting, VotingWorkDetail } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { Breadcrumbs } from '../../components/Breadcrumbs'
import { PageError, PageLoading } from '../../components/PageState'
import { accentVars } from '../../lib/accent'
import { msUntil } from '../../lib/serverClock'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { usePlatform } from '../../platform/context'
import { Answers, WorkVisual } from '../results/shared'
import {
  countsVisible,
  isSinglePool,
  ONE_VOTE,
  votedViewer,
  votedWork,
  voteCopyFor,
  voteRefusal,
  votesLabel,
  workName,
} from './lib'
import { VoteButton, VoteCount, VoteDialog, VoteNote } from './shared'
import { VotingLocked } from './state'
import '../../components/voting/stage.css'
import './Voting.css'

/** Like the ballot: what this page says depends on who is asking, so it waits until that is known. */
export default function VotingWorkPage() {
  const { status } = useAuth()
  if (status === 'loading') return <PageLoading label="İş yüklənir" />
  return <VotingWork key={status} />
}

/**
 * One work on the ballot: the artwork as the object of the page, what an admin chose to make public about it,
 * the running count, and the single control. No author — on this surface a work is known by its name and its
 * reference (D2).
 */
function VotingWork() {
  const { slug = '', reference = '' } = useParams()
  const state = useApi(
    async (signal) => ({
      detail: await votingApi.work(slug, reference, signal),
      voting: await votingApi.summary(slug, signal),
    }),
    [slug, reference],
  )
  useTitle(state.data ? workName(state.data.detail.work) : 'İş')

  if (state.status === 'loading') return <PageLoading label="İş yüklənir" />
  if (state.status === 'error')
    return state.error.status === 404 ? (
      <VotingLocked slug={slug} message={state.error.message} />
    ) : (
      <PageError error={state.error} onRetry={state.retry} />
    )
  return <WorkView initial={state.data.detail} voting={state.data.voting} slug={slug} onStale={state.retry} />
}

function WorkView({
  initial,
  voting,
  slug,
  onStale,
}: {
  initial: VotingWorkDetail
  voting: Voting
  slug: string
  onStale: () => void
}) {
  const { platformName } = usePlatform()
  const [detail, setDetail] = useState(initial)
  const [open, setOpen] = useState(false)
  const [cast, setCast] = useState(false)
  const [casting, setCasting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const inlineButton = useRef<HTMLDivElement>(null)
  const bar = useRef<HTMLDivElement>(null)
  const [barShown, setBarShown] = useState(true)

  useEffect(() => setDetail(initial), [initial])

  // At the window's end, by the server's clock, the page reads itself again, so its button says what the server now
  // says instead of offering a vote that would be refused. Never sooner than a second apart, should the server
  // still be a moment behind.
  useEffect(() => {
    const endsAt = voting.state === 'Open' ? voting.endsAt : null
    if (!endsAt) return
    let timer: ReturnType<typeof setTimeout> | undefined
    const wait = () => {
      const left = msUntil(endsAt)
      // Further than one timer reaches: wait the next step first (see msUntil).
      timer = left > 1_000 ? setTimeout(wait, left) : setTimeout(onStale, Math.max(left + 250, 1_000))
    }
    wait()
    return () => clearTimeout(timer)
  }, [voting.state, voting.endsAt, onStale])

  const c = voting.competition
  const w = detail.work
  const name = workName(w)
  const single = isSinglePool(voting)
  // The same rule as the ballot and the home page, from the same switch.
  const countShown = countsVisible(voting.state)
  const copy = voteCopyFor(w.eligibility)
  const mine = detail.viewer?.votedWorks.includes(w.reference) ?? false

  // The bar reserves its own height under the footer and steps aside while the page's own button is on screen.
  useEffect(() => {
    const el = bar.current
    if (!el) return
    const html = document.documentElement
    const apply = () => html.style.setProperty('--action-bar-h', `${el.offsetHeight}px`)
    apply()
    const observer = new ResizeObserver(apply)
    observer.observe(el)
    return () => {
      observer.disconnect()
      html.style.removeProperty('--action-bar-h')
    }
  }, [])

  useEffect(() => {
    const target = inlineButton.current
    if (!target || typeof IntersectionObserver === 'undefined') return
    const observer = new IntersectionObserver((entries) => setBarShown(!entries.some((e) => e.isIntersecting)), {
      rootMargin: '-64px 0px -96px 0px',
    })
    observer.observe(target)
    return () => observer.disconnect()
  }, [])

  const confirm = async (captchaToken?: string) => {
    setCasting(true)
    setError(null)
    try {
      const receipt = await votingApi.vote(slug, w.reference, captchaToken)
      setDetail((d) => ({ ...d, work: votedWork(d.work, receipt), viewer: votedViewer(d.viewer, receipt) }))
      // The dialog stays, as the receipt and the invitation to pass the vote on.
      setCast(true)
    } catch (err) {
      setError(voteRefusal(err))
      // Whatever the refusal was, what this viewer may do has changed: ask again rather than keep a stale button.
      if (err instanceof ApiError && [401, 403, 409, 422].includes(err.status)) onStale()
    } finally {
      setCasting(false)
    }
  }

  const [first, ...rest] = detail.files
  const alt = (f: GalleryFile, i: number) =>
    f.kind === 'document-preview'
      ? `${name} — sənədin birinci səhifəsi`
      : f.kind === 'document-page'
        ? `${name} — sənədin ${f.page}. səhifəsi`
        : `${name}${i > 0 ? ` (${i + 1})` : ''}`
  // A document is shown whole: its first page above, every other page below, in order.
  const documentPages = first?.kind === 'document-preview' ? 1 + rest.filter((f) => f.kind === 'document-page' && f.id === first.id).length : 0

  return (
    <article className="voting vwork" style={accentVars(c.project.accentColor)}>
      <div className="container">
        <Breadcrumbs
          items={[
            { label: platformName, to: '/' },
            { label: c.title, to: `/competitions/${c.slug}` },
            { label: 'Səsvermə', to: `/competitions/${c.slug}/voting` },
            { label: name },
          ]}
        />
      </div>

      <div className="container vwork__layout">
        <div className="vwork__main">
          <figure className="vwork__hero art-frame">
            {first ? (
              <img
                src={first.url}
                alt={alt(first, 0)}
                width={first.width ?? undefined}
                height={first.height ?? undefined}
                fetchPriority="high"
              />
            ) : (
              <WorkVisual item={w} alt={name} size="full" eager />
            )}
          </figure>
          {documentPages === 1 && <p className="vwork__note">Sənədin birinci səhifəsi (önizləmə). Sənədin özü ictimai deyil.</p>}
          {documentPages > 1 && (
            <p className="vwork__note">
              Sənədin {documentPages} səhifəsinin hamısı aşağıdadır — səhifə-səhifə şəkil kimi. Sənədin özü ictimai deyil.
            </p>
          )}

          {rest.length > 0 && (
            <ul role="list" className="vwork__more">
              {rest.map((f, i) => (
                <li key={`${f.id}-${f.page ?? 0}`}>
                  <figure className="art-frame">
                    <img
                      src={f.url}
                      alt={alt(f, i + 1)}
                      width={f.width ?? undefined}
                      height={f.height ?? undefined}
                      loading="lazy"
                      decoding="async"
                    />
                    {f.kind === 'document-page' && <figcaption className="vwork__page tabular">{f.page}. səhifə</figcaption>}
                  </figure>
                </li>
              ))}
            </ul>
          )}
        </div>

        <div className="vwork__panel">
          {!single && <p className="vwork__badge">{w.nomination.name}</p>}
          <h1 className="vwork__title">{name}</h1>
          <p className="vwork__ref tabular">{w.reference}</p>

          {detail.answers.length > 0 && (
            <section className="vwork__answers" aria-label="İş haqqında">
              <Answers answers={detail.answers} />
            </section>
          )}

          {(countShown || mine) && (
            <div className="vstatus">
              {countShown && (
                <>
                  <span className="vstatus__label">Səs sayı</span>
                  <strong className="vstatus__count tabular">{votesLabel(w.voteCount)}</strong>
                </>
              )}
              {mine && <span className="vstatus__mine">Səs verdiniz</span>}
            </div>
          )}

          <div className="vwork__cta" ref={inlineButton}>
            <VoteButton work={w} onVote={() => setOpen(true)} busy={casting} block />
            <VoteNote work={w} />
            {copy.tone === 'action' && w.eligibility === 'Allowed' && <p className="vnote">{ONE_VOTE}</p>}
          </div>

          <nav className="vwork__back" aria-label="Səsverməyə qayıt">
            <Link to={`/competitions/${c.slug}/voting`} className="btn btn--ghost">
              Bütün işlərə bax
            </Link>
          </nav>
        </div>
      </div>

      {/* Only while there is something to press: a bar that explains why a vote is impossible would follow the
          reader down the page repeating itself, and the panel says it once already. */}
      {copy.tone === 'action' && (
        <div
          className="action-bar"
          ref={bar}
          role="region"
          aria-label="Səs ver"
          data-shown={barShown}
          // Out of sight means out of reach: a hidden bar must not take keyboard focus or be read out.
          inert={!barShown}
        >
          <span className="action-bar__choice">
            {!single && <span className="action-bar__label">{w.nomination.name}</span>}
            <span className="action-bar__value">{name}</span>
          </span>
          {countShown && <VoteCount count={w.voteCount} />}
          <VoteButton work={w} onVote={() => setOpen(true)} busy={casting} />
        </div>
      )}

      {open && (
        <VoteDialog
          work={w}
          busy={casting}
          error={error}
          cast={cast}
          competition={{ slug: c.slug, title: c.title }}
          onConfirm={confirm}
          onClose={() => {
            setOpen(false)
            setError(null)
            setCast(false)
          }}
        />
      )}
    </article>
  )
}
