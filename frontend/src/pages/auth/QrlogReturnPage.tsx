import { useEffect, useRef, useState } from 'react'
import { usePlatform } from '../../platform/context'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { authApi } from '../../api/endpoints'
import type { QrlogClaim } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { authPath, safeNext } from '../../auth/intent'
import { FAILED_MESSAGE, beginQrlogSignIn, recallQrlogNext } from '../../lib/qrlog'
import { useTitle } from '../../lib/useTitle'
import { AuthLayout } from './AuthLayout'

/** Between polls. 90 a minute are allowed; this uses 24, so several tabs of one household still fit. */
const POLL_MS = 2500
/** A ticket lives five minutes; nothing useful can arrive after that. */
const GIVE_UP_MS = 6 * 60 * 1000

type State =
  | { kind: 'waiting' }
  | { kind: 'failed'; message: string }
  | { kind: 'other-browser' }
  | { kind: 'email-taken'; email: string }

/**
 * Where QRLog sends the person back, and where the tab that started the sign-in waits. Nothing identifying is in the
 * address: the ticket is known from an HttpOnly cookie, so this page only asks the API "is it done?" until it is.
 */
export default function QrlogReturnPage() {
  const { platformName } = usePlatform()
  useTitle('QRLog ilə giriş')
  const navigate = useNavigate()
  const { status, applySession, retry: recheckSession } = useAuth()
  // Arriving here is a full page load, so the session is still being restored from the refresh cookie. Claim only
  // once the restore has a clear answer (signed in, or not): a link claim must carry this account's token, and a
  // late "no session" answer must not undo a sign-in the claim has just made. While the restore is throttled or out
  // of reach ('unavailable') nothing is claimed, so the single-use ticket is not spent.
  const settled = status === 'authenticated' || status === 'anonymous'
  const [params] = useSearchParams()
  const cancelled = params.get('cancelled') === '1'
  const next = recallQrlogNext()
  const [state, setState] = useState<State>(cancelled ? { kind: 'failed', message: 'QRLog ilə giriş ləğv edildi.' } : { kind: 'waiting' })
  const [retrying, setRetrying] = useState(false)
  const done = useRef(false)

  useEffect(() => {
    if (!settled) return
    if (cancelled) {
      authApi.qrlogCancel().catch(() => undefined)
      return
    }
    let timer: number | undefined
    const startedAt = Date.now()

    const settle = (claim: QrlogClaim) => {
      done.current = true
      switch (claim.status) {
        case 'signed-in':
          if (claim.session) applySession(claim.session)
          navigate(safeNext(claim.returnTo, '/'), { replace: true })
          return
        case 'linked':
          navigate('/my/profile?linked=1', { replace: true })
          return
        case 'email-taken':
          setState({ kind: 'email-taken', email: claim.email ?? '' })
          return
        default:
          setState({ kind: 'failed', message: FAILED_MESSAGE })
      }
    }

    const poll = async () => {
      if (done.current) return
      try {
        const claim = await authApi.qrlogClaim()
        if (done.current) return
        if (claim.status === 'pending') {
          if (Date.now() - startedAt > GIVE_UP_MS) {
            done.current = true
            setState({ kind: 'failed', message: FAILED_MESSAGE })
            return
          }
          timer = window.setTimeout(poll, POLL_MS)
          return
        }
        settle(claim)
      } catch (err) {
        if (done.current) return
        // No ticket in this browser: the sign-in was started somewhere else (the tab that started it finishes on its own).
        if (err instanceof ApiError && err.status === 404) {
          done.current = true
          setState({ kind: 'other-browser' })
          return
        }
        // 403 (a link ticket claimed by another account) and the like: a raw status is no help to anyone.
        if (err instanceof ApiError && err.status >= 400 && err.status < 500 && err.status !== 429) {
          done.current = true
          setState({ kind: 'failed', message: err.status === 403 || err.status === 409 ? err.message : FAILED_MESSAGE })
          return
        }
        // Network hiccup or throttling: try again a little later.
        timer = window.setTimeout(poll, POLL_MS * 2)
      }
    }

    void poll()
    return () => {
      done.current = true
      if (timer) window.clearTimeout(timer)
    }
  }, [settled, cancelled, applySession, navigate])

  const retry = async () => {
    setRetrying(true)
    try {
      await beginQrlogSignIn(next)
    } catch {
      setRetrying(false)
      setState({ kind: 'failed', message: FAILED_MESSAGE })
    }
  }

  const emailLogin = (
    <Link to={authPath('login', next)} className="btn btn--lg">
      E-mail ilə daxil ol
    </Link>
  )

  if (state.kind === 'waiting') {
    return (
      <AuthLayout title="QRLog təsdiqi gözlənilir">
        <p className="lede" aria-live="polite">
          QRLog tətbiqində <strong>Təsdiqlə</strong> düyməsini basın. Bu səhifə özü davam edəcək.
        </p>
        {status === 'unavailable' && (
          <div className="alert alert--info" role="status">
            Giriş vəziyyətiniz bu an yoxlanıla bilmədi; bir neçə saniyədən sonra özü davam edəcək.{' '}
            <button type="button" className="btn btn--sm" onClick={recheckSession}>
              İndi yenidən yoxla
            </button>
          </div>
        )}
        <p className="muted">Fikrinizi dəyişdiniz? E-mail və şifrə ilə də daxil ola bilərsiniz.</p>
        {emailLogin}
      </AuthLayout>
    )
  }

  if (state.kind === 'other-browser') {
    return (
      <AuthLayout title="Giriş başqa brauzerdə başlayıb">
        <p className="lede">
          Bu brauzerdə başlanmış QRLog girişi yoxdur. {platformName}-ı açdığınız brauzerə qayıdın — giriş orada özü davam edir.
          Ya da buradan yenidən başlayın.
        </p>
        <div className="auth-form__actions">
          <button type="button" className="btn btn--primary btn--lg" onClick={retry} disabled={retrying}>
            {retrying ? 'QRLog açılır…' : 'QRLog ilə yenidən daxil ol'}
          </button>
          {emailLogin}
        </div>
      </AuthLayout>
    )
  }

  if (state.kind === 'email-taken') {
    return (
      <AuthLayout title="Bu e-mail artıq qeydiyyatdadır">
        <div className="alert" role="alert">
          <strong>{state.email}</strong> ünvanı ilə {platformName} hesabı artıq var. Təhlükəsizlik üçün QRLog hesabı ona avtomatik
          bağlanmır.
        </div>
        <p className="lede">
          Həmin hesaba e-mail və şifrə ilə daxil olun, sonra <strong>Profil → Bağlı hesablar</strong> bölməsində “QRLog
          hesabını bağla” seçin. Bundan sonra QRLog ilə birbaşa daxil ola biləcəksiniz.
        </p>
        {emailLogin}
      </AuthLayout>
    )
  }

  return (
    <AuthLayout title="Giriş tamamlanmadı">
      <div className="alert" role="alert">
        {state.message}
      </div>
      <div className="auth-form__actions">
        <button type="button" className="btn btn--primary btn--lg" onClick={retry} disabled={retrying}>
          {retrying ? 'QRLog açılır…' : 'Yenidən cəhd et'}
        </button>
        {emailLogin}
      </div>
    </AuthLayout>
  )
}
