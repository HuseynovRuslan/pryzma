import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Link, useLocation } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { authApi } from '../../api/endpoints'
import type { VotingWork } from '../../api/types'
import { authPath } from '../../auth/intent'
import { Buta } from '../../components/Motifs'
import { Turnstile } from '../../components/Turnstile'
import { usePublicConfig } from '../../lib/publicConfig'
import { WorkVisual } from '../results/shared'
import { ONE_VOTE, voteCopyFor, voteNote, votesLabel, workName, workNumber } from './lib'
import { ShareVote } from './ShareVote'

/** The running total on a card, as the design shows it: a heart, the number, the word. */
export function VoteCount({ count }: { count: number }) {
  return (
    <span className="vcount">
      <Heart />
      <span className="tabular">{votesLabel(count)}</span>
    </span>
  )
}

function Heart() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path
        d="M12 20.5 4.2 13a4.8 4.8 0 0 1 6.8-6.8l1 1 1-1A4.8 4.8 0 0 1 19.8 13Z"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinejoin="round"
      />
    </svg>
  )
}

/**
 * The one control of the whole module, in the four states the design asks for. Which one it is, is the server's
 * answer (`work.eligibility`) and never a sum the browser does for itself.
 *
 * A signed-out visitor gets a link to sign in that remembers where they were; somebody who may vote gets a
 * button that opens the confirmation; anybody else gets the reason, as text rather than as a button that cannot
 * be pressed.
 */
export function VoteButton({
  work,
  onVote,
  busy = false,
  block = false,
}: {
  work: Pick<VotingWork, 'eligibility' | 'reference'>
  onVote: () => void
  busy?: boolean
  block?: boolean
}) {
  const { pathname, search } = useLocation()
  const copy = voteCopyFor(work.eligibility)
  const wide = block ? ' btn--block' : ''

  if (work.eligibility === 'NotSignedIn')
    return (
      <Link to={authPath('login', pathname + search)} className={`btn btn--primary${wide}`}>
        {copy.label}
      </Link>
    )

  if (work.eligibility === 'EmailNotConfirmed') return <ResendConfirmation label={copy.label} block={block} />

  if (work.eligibility === 'Allowed')
    return (
      <button
        type="button"
        className={`btn btn--primary${wide}${busy ? ' btn--waiting' : ''}`}
        onClick={onVote}
        disabled={busy}
      >
        {busy ? 'Göndərilir…' : copy.label}
      </button>
    )

  return (
    <span
      className={`vbtn vbtn--${copy.tone}${block ? ' vbtn--block' : ''}`}
      role={copy.tone === 'done' ? 'status' : undefined}
    >
      {copy.label}
    </span>
  )
}

/**
 * For an account that must confirm its e-mail first: the reason, and a button for a new link in case the first one
 * never came. The server allows one a minute and five a day, and says so when asked too often.
 */
function ResendConfirmation({ label, block }: { label: string; block: boolean }) {
  const [state, setState] = useState<'idle' | 'busy' | 'sent'>('idle')
  const [error, setError] = useState<string>()
  const send = async () => {
    setState('busy')
    setError(undefined)
    try {
      await authApi.resendConfirmation()
      setState('sent')
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Link göndərilmədi. Bir az sonra yenidən cəhd edin.')
      setState('idle')
    }
  }
  return (
    <span className={`vconfirm${block ? ' vbtn--block' : ''}`}>
      <span className="vbtn vbtn--off">{label}</span>
      {state === 'sent' ? (
        <span className="vnote" role="status">
          Yeni link e-mailinizə göndərildi.
        </span>
      ) : (
        <button type="button" className="btn btn--ghost btn--sm" onClick={send} disabled={state === 'busy'}>
          {state === 'busy' ? 'Göndərilir…' : 'Linki yenidən göndər'}
        </button>
      )}
      {error && (
        <span className="vnote" role="alert">
          {error}
        </span>
      )}
    </span>
  )
}

/** The line under the button: why it says what it says. Empty for a work that is simply ready to be voted for. */
export function VoteNote({ work, id }: { work: Pick<VotingWork, 'eligibility'>; id?: string }) {
  const note = voteNote(work.eligibility)
  if (!note) return null
  return (
    <p className="vnote" id={id}>
      {note}
    </p>
  )
}

/**
 * A work on the ballot: the picture first, then its name, and the one button.
 *
 * No author — on this surface a work is its name and its number, nothing else (D2). The small number on the picture
 * is the submission's own reference in its short form ("İş #36"), never an invented id, and it sits in a corner so
 * the work, not the bookkeeping, is what the card shows.
 */
export function VotingCard({
  work,
  slug,
  onVote,
  busy,
  showCount = true,
  singlePool = false,
}: {
  work: VotingWork
  slug: string
  onVote: () => void
  busy: boolean
  /** Whether the running total is printed — from `countsVisible`, so every surface follows one rule. */
  showCount?: boolean
  /** Every work in one list: the card carries no nomination, because there is nothing to tell apart. */
  singlePool?: boolean
}) {
  const name = workName(work)
  const to = `/competitions/${slug}/voting/${work.reference}`
  return (
    <li className="vcard">
      <Link to={to} className="vcard__visual" tabIndex={-1} aria-hidden="true">
        <WorkVisual item={work} alt="" />
        <span className="vcard__code tabular" title={work.reference}>
          {workNumber(work.reference)}
        </span>
      </Link>
      <div className="vcard__body">
        {(!singlePool || showCount) && (
          <p className="vcard__meta">
            {!singlePool && <span className="vcard__nomination">{work.nomination.name}</span>}
            {showCount && <VoteCount count={work.voteCount} />}
          </p>
        )}
        {/* Name and description share one reserved block, so a card is the same height whatever the work
            behind it wrote and the buttons across a row sit on one line. */}
        <div className="vcard__text">
          <h3 className="vcard__title">
            <Link to={to}>{name}</Link>
          </h3>
          {work.publicConceptDescription && <p className="vcard__desc">{work.publicConceptDescription}</p>}
        </div>
        <div className="vcard__actions">
          <VoteButton work={work} onVote={onVote} busy={busy} block />
          <Link to={to} className="vcard__more" aria-label={`${name} — işə bax`}>
            İşə bax →
          </Link>
        </div>
        <VoteNote work={work} />
      </div>
    </li>
  )
}

/**
 * The confirmation. It exists because a vote is final (D5) and says so in as many words *before* it is cast —
 * the design offers no way to change or withdraw one, and neither does the API.
 *
 * Once the vote is in (`cast`), the same dialog becomes the receipt: the mark is stamped, the vote is said to be
 * recorded and private, and the voter is offered the one thing that brings the next voter — a message to pass on,
 * shown before it is sent, that never says which work they chose.
 *
 * Keyboard behaviour follows the QRLog dialog: focus moves in on open and back to the opener on close, Tab is
 * trapped, Escape and the backdrop close it, and the page behind does not scroll.
 *
 * When the server asks for the robot check on the vote (PublicConfig.voteCaptcha), the question carries the widget
 * and the vote cannot be confirmed until it has passed; its token goes with the vote. A token works once, so a
 * refused vote asks for the check afresh.
 */
export function VoteDialog({
  work,
  busy,
  error,
  onConfirm,
  onClose,
  cast = false,
  competition,
}: {
  work: VotingWork
  busy: boolean
  error: string | null
  /** `captchaToken`: the robot check's token, when the vote asks for one. */
  onConfirm: (captchaToken?: string) => void
  onClose: () => void
  /** The vote is recorded: the dialog shows the receipt and the invitation instead of the question. */
  cast?: boolean
  /** Which competition the invitation names and links to. */
  competition: { slug: string; title: string }
}) {
  const id = useId()
  const box = useRef<HTMLDivElement>(null)
  const cancel = useRef<HTMLButtonElement>(null)
  const title = useRef<HTMLHeadingElement>(null)
  const config = usePublicConfig()
  const siteKey = config.voteCaptcha ? (config.captchaSiteKey ?? null) : null
  const [captcha, setCaptcha] = useState<string | null>(null)
  const [captchaRound, setCaptchaRound] = useState(0)
  // Until the server has said whether it asks for the check, the vote waits rather than go without a token.
  const waiting = !config.ready || (siteKey !== null && !captcha)

  // A refused vote spent its token (or never had a good one): the check is asked afresh.
  useEffect(() => {
    if (!error) return
    setCaptcha(null)
    setCaptchaRound((n) => n + 1)
  }, [error])

  // The question's buttons are gone once the vote is in; the heading takes focus, so a screen reader hears the receipt.
  useEffect(() => {
    if (cast) title.current?.focus()
  }, [cast])

  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null
    cancel.current?.focus()
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.body.style.overflow = overflow
      opener?.focus?.()
    }
  }, [])

  const close = useCallback(() => {
    if (!busy) onClose()
  }, [busy, onClose])

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.stopPropagation()
      close()
      return
    }
    if (e.key !== 'Tab' || !box.current) return
    const focusable = [...box.current.querySelectorAll<HTMLElement>('button:not([disabled]), a[href], iframe')]
    if (focusable.length === 0) return
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault()
      last.focus()
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault()
      first.focus()
    }
  }

  return createPortal(
    <div className="vdlg" onKeyDown={onKeyDown}>
      <div className="vdlg__backdrop" onClick={close} aria-hidden="true" />
      <div ref={box} className="vdlg__box" role="dialog" aria-modal="true" aria-labelledby={`${id}-t`} aria-describedby={`${id}-d`}>
        <span className="vdlg__handle" aria-hidden="true" />
        {cast ? (
          <>
            <span className="vdlg__mark vdlg__mark--stamped" aria-hidden="true">
              <Buta eye={false} />
            </span>
            <h2 id={`${id}-t`} ref={title} tabIndex={-1} className="vdlg__title">
              Səsiniz qeydə alındı
            </h2>
            <p id={`${id}-d`} className="vdlg__text">
              <strong>{workName(work)}</strong> üçün səsiniz sayıldı. Saytda heç kim hansı işə səs verdiyinizi görmür.
            </p>
            <ShareVote
              slug={competition.slug}
              competitionTitle={competition.title}
              lead="Dostlarınızı da səs verməyə çağırın. Onlara bu mesaj gedəcək:"
            />
            <button type="button" className="vdlg__dismiss" onClick={close}>
              Bağla
            </button>
          </>
        ) : (
          <>
            <span className="vdlg__mark" aria-hidden="true">
              <Buta eye={false} />
            </span>
            <h2 id={`${id}-t`} className="vdlg__title">
              Səs vermək istəyirsiniz?
            </h2>
            <p id={`${id}-d`} className="vdlg__text">
              Seçdiyiniz bu yaradıcı layihəyə səs vermək istədiyinizdən əminsiniz?
            </p>
            <p className="vdlg__work">
              <strong>{workName(work)}</strong>
            </p>
            {/* Said before the vote is cast, because it cannot be taken back — and it spends the one vote. */}
            <p className="vdlg__final">{ONE_VOTE} Səs bir dəfə verilir və geri alınmır.</p>
            {error && (
              <p className="vdlg__error" role="alert">
                {error}
              </p>
            )}
            {siteKey && (
              <div className="vdlg__captcha">
                <Turnstile key={captchaRound} siteKey={siteKey} action="vote" onToken={setCaptcha} />
              </div>
            )}
            <div className="vdlg__actions">
              <button
                type="button"
                className={`btn btn--primary${busy ? ' btn--waiting' : ''}`}
                onClick={() => onConfirm(captcha ?? undefined)}
                disabled={busy || waiting}
              >
                {busy ? 'Göndərilir…' : 'Səsi təsdiqlə'}
              </button>
              <button ref={cancel} type="button" className="btn btn--ghost" onClick={close} disabled={busy}>
                Ləğv et
              </button>
            </div>
          </>
        )}
      </div>
    </div>,
    document.body,
  )
}
