import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { usePlatform } from '../platform/context'
import { createPortal } from 'react-dom'
import { QRCodeSVG } from 'qrcode.react'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError } from '../api/client'
import { authApi } from '../api/endpoints'
import { useAuth } from '../auth/AuthContext'
import { authPath, safeNext } from '../auth/intent'
import { beginQrlogSignIn, FAILED_MESSAGE } from '../lib/qrlog'
import { QrlogLogo } from './QrlogSignIn'
import './QrLoginDialog.css'

/** Between polls while the phone has not approved; doubled after a 429 or a network hiccup. */
const POLL_MS = 2500
/** How long "Təsdiqləndi" stays before the page moves on. */
const CONTINUE_AFTER_MS = 900
/** A sign-in ticket's lifetime on the server (QrlogSsoService). */
const TICKET_LIFETIME_MS = 5 * 60_000

type Phase =
  | { kind: 'starting' }
  | { kind: 'waiting'; scanUrl: string; deadline: number }
  | { kind: 'approved' }
  | { kind: 'expired' }
  | { kind: 'email-taken'; email: string }
  | { kind: 'failed'; message: string }

interface Props {
  /** Where to continue after signing in (same-site path); kept on the server's ticket as well. */
  next: string | null
  /** Link QRLog to the signed-in account instead of signing in. */
  link?: boolean
  onClose: () => void
  /** Link mode: the account now has QRLog. */
  onLinked?: () => void
}

/** mm:ss */
function clock(ms: number): string {
  const s = Math.max(0, Math.ceil(ms / 1000))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

/**
 * "QRLog ilə daxil ol" on a computer: a one-time QR for the phone. The QR holds only QRLog's approval address with
 * the ticket code — the secret that collects the result stays in this browser's HttpOnly cookie — so a photo of the
 * screen can never become somebody's session. The page asks the server every few seconds whether the phone has
 * approved; the first positive answer is the sign-in itself (collected once), and the person continues where they
 * were going. Closing the dialog ends the ticket.
 */
export default function QrLoginDialog({ next, link = false, onClose, onLinked }: Props) {
  const { platformName } = usePlatform()
  const id = useId()
  const navigate = useNavigate()
  const { applySession } = useAuth()
  const dialog = useRef<HTMLDivElement>(null)
  const closeButton = useRef<HTMLButtonElement>(null)
  const [phase, setPhase] = useState<Phase>({ kind: 'starting' })
  const [now, setNow] = useState(() => Date.now())
  const [run, setRun] = useState(0)
  const finished = useRef(false)
  // The latest callback, without restarting the polling when the parent re-renders.
  const linkedRef = useRef(onLinked)
  useEffect(() => {
    linkedRef.current = onLinked
  }, [onLinked])

  // Open a ticket (again, for "Yeni QR kod yarat": never the old code recycled).
  useEffect(() => {
    let alive = true
    finished.current = false
    setPhase({ kind: 'starting' })
    authApi
      .qrlogStart({ returnTo: next, link })
      .then((started) => {
        if (!alive) return
        // The server's clock decides expiry; locally only the length of the window is trusted, and never more than
        // a ticket lives (a browser clock a few seconds behind would otherwise show 5:04 on a five-minute code).
        const left = Date.parse(started.expiresAt) - Date.now()
        const span = Number.isFinite(left) && left > 30_000 ? Math.min(left, TICKET_LIFETIME_MS) : TICKET_LIFETIME_MS
        setPhase({ kind: 'waiting', scanUrl: started.scanUrl, deadline: Date.now() + span })
      })
      .catch((err: unknown) => {
        if (alive) setPhase({ kind: 'failed', message: err instanceof ApiError && err.status !== 0 ? err.message : FAILED_MESSAGE })
      })
    return () => {
      alive = false
    }
  }, [next, link, run])

  // While the QR is showing: ask whether the phone has approved. Stops on any answer, on expiry, and on close.
  const waiting = phase.kind === 'waiting'
  const deadline = phase.kind === 'waiting' ? phase.deadline : 0
  useEffect(() => {
    if (!waiting) return
    let timer: number | undefined
    let stopped = false
    const poll = async (delay: number) => {
      timer = window.setTimeout(async () => {
        if (stopped) return
        if (Date.now() >= deadline) {
          setPhase({ kind: 'expired' })
          return
        }
        try {
          const claim = await authApi.qrlogClaim()
          if (stopped) return
          switch (claim.status) {
            case 'pending':
              return poll(POLL_MS)
            case 'signed-in':
              finished.current = true
              // "Təsdiqləndi" first: the session is applied as the page moves on (applying it at once would let the
              // login page redirect before anyone saw the confirmation). The server-side session exists already.
              setPhase({ kind: 'approved' })
              window.setTimeout(() => {
                if (claim.session) applySession(claim.session)
                navigate(safeNext(claim.returnTo, '/'), { replace: true })
              }, CONTINUE_AFTER_MS)
              return
            case 'linked':
              finished.current = true
              setPhase({ kind: 'approved' })
              window.setTimeout(() => linkedRef.current?.(), CONTINUE_AFTER_MS)
              return
            case 'email-taken':
              finished.current = true
              setPhase({ kind: 'email-taken', email: claim.email ?? '' })
              return
            default:
              setPhase({ kind: 'expired' })
          }
        } catch (err) {
          if (stopped) return
          if (err instanceof ApiError && err.status === 404) {
            // This browser no longer holds the ticket (another tab started a new one): a fresh QR is needed.
            setPhase({ kind: 'expired' })
          } else if (err instanceof ApiError && err.status >= 400 && err.status < 500 && err.status !== 429) {
            finished.current = true
            setPhase({ kind: 'failed', message: err.status === 403 || err.status === 409 ? err.message : FAILED_MESSAGE })
          } else {
            poll(POLL_MS * 2) // throttled or offline for a moment: ask again, a little later
          }
        }
      }, delay)
    }
    poll(POLL_MS)
    const tick = window.setInterval(() => setNow(Date.now()), 1000)
    return () => {
      stopped = true
      window.clearTimeout(timer)
      window.clearInterval(tick)
    }
  }, [waiting, deadline, applySession, navigate])

  const close = useCallback(() => {
    // An unfinished ticket is ended on the server: a late approval on the phone then goes nowhere.
    if (!finished.current) authApi.qrlogCancel().catch(() => undefined)
    onClose()
  }, [onClose])

  // Focus in on open, back to the button that opened it on close; the page behind does not scroll.
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null
    closeButton.current?.focus()
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.body.style.overflow = overflow
      opener?.focus?.()
    }
  }, [])

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.stopPropagation()
      close()
      return
    }
    if (e.key !== 'Tab' || !dialog.current) return
    const focusable = [...dialog.current.querySelectorAll<HTMLElement>('button:not([disabled]), a[href]')]
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

  // Capped: until the first tick, `now` is from before the ticket was issued.
  const remaining = phase.kind === 'waiting' ? Math.min(phase.deadline - now, TICKET_LIFETIME_MS) : 0

  return createPortal(
    <div className="qrdlg" onKeyDown={onKeyDown}>
      <div className="qrdlg__backdrop" onClick={close} aria-hidden="true" />
      <div ref={dialog} className="qrdlg__box" role="dialog" aria-modal="true" aria-labelledby={`${id}-t`} aria-describedby={`${id}-d`}>
        <div className="qrdlg__head">
          <h2 id={`${id}-t`} className="qrdlg__title">
            {link ? (
              <>
                <QrlogLogo height={22} /> hesabını bağla
              </>
            ) : (
              <>
                <QrlogLogo height={22} /> ilə daxil ol
              </>
            )}
          </h2>
          <button ref={closeButton} type="button" className="qrdlg__close" onClick={close} aria-label="Bağla">
            <span aria-hidden="true" />
          </button>
        </div>

        <div className="qrdlg__body">
          <div className={`qrdlg__code qrdlg__code--${phase.kind}`}>
            {phase.kind === 'waiting' && (
              <QRCodeSVG
                value={phase.scanUrl}
                size={216}
                level="M"
                marginSize={2}
                bgColor="#ffffff"
                fgColor="#10262b"
                role="img"
                aria-label="Giriş QR kodu — QRLog tətbiqi ilə skan edin"
                className="qrdlg__qr"
              />
            )}
            {phase.kind === 'starting' && <div className="qrdlg__placeholder" aria-hidden="true" />}
            {phase.kind === 'approved' && (
              <div className="qrdlg__state qrdlg__state--ok">
                <span className="qrdlg__tick" aria-hidden="true" />
                <strong>Təsdiqləndi</strong>
              </div>
            )}
            {phase.kind === 'expired' && (
              <div className="qrdlg__state">
                <strong>QR kodun vaxtı bitdi</strong>
                <button type="button" className="btn btn--primary" onClick={() => setRun((n) => n + 1)}>
                  Yeni QR kod yarat
                </button>
              </div>
            )}
            {(phase.kind === 'failed' || phase.kind === 'email-taken') && <div className="qrdlg__placeholder" aria-hidden="true" />}
          </div>

          <div className="qrdlg__side" id={`${id}-d`}>
            <p className="qrdlg__lede">QRLog tətbiqində skan edin</p>
            <ol className="qrdlg__steps">
              <li>QRLog tətbiqini açın</li>
              <li>
                {/* Not our brand: this is the entry as QRLog's own app lists it. It must match what the
                    person sees on their phone, so it changes only when QRLog renames it. */}
                <strong>Xidmətlər → MEYDAN v1</strong>
              </li>
              <li>QR kodu skan edin</li>
              <li>Girişi təsdiqləyin</li>
            </ol>

            <p className="qrdlg__status" role="status" aria-live="polite">
              {phase.kind === 'starting' && 'QR kod hazırlanır…'}
              {phase.kind === 'waiting' && (
                <>
                  Telefonda təsdiq gözlənilir · <span className="tabular">{clock(remaining)}</span>
                </>
              )}
              {phase.kind === 'approved' && (link ? 'Təsdiqləndi. QRLog hesabınız bağlanır…' : 'Təsdiqləndi. Davam edirik…')}
              {phase.kind === 'expired' && 'QR kodun vaxtı bitdi. Yeni kod yaradın.'}
            </p>

            {phase.kind === 'email-taken' && (
              <div className="alert" role="alert">
                <strong>{phase.email}</strong> ünvanı ilə {platformName} hesabı artıq var. Təhlükəsizlik üçün QRLog ona avtomatik bağlanmır:
                e-mail və şifrə ilə daxil olun, sonra <strong>Profil → Bağlı hesablar</strong> bölməsində QRLog-u bağlayın.
              </div>
            )}
            {phase.kind === 'failed' && (
              <div className="alert" role="alert">
                {phase.message}{' '}
                <button type="button" className="btn btn--sm" onClick={() => setRun((n) => n + 1)}>
                  Yenidən cəhd et
                </button>
              </div>
            )}
          </div>
        </div>

        <p className="qrdlg__foot">
          {phase.kind === 'email-taken' ? (
            <Link to={authPath('login', next)} onClick={onClose}>
              E-mail ilə daxil ol
            </Link>
          ) : (
            <>
              QRLog bu cihazdadır?{' '}
              <button
                type="button"
                className="qrdlg__link"
                onClick={() => {
                  finished.current = true // the new ticket replaces this one
                  beginQrlogSignIn(next, link).catch(() => setPhase({ kind: 'failed', message: FAILED_MESSAGE }))
                }}
              >
                Burada davam edin
              </button>
            </>
          )}
        </p>
      </div>
    </div>,
    document.body,
  )
}
