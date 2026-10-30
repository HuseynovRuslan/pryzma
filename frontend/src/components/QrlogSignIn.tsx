import { lazy, Suspense, useState } from 'react'
import { usePlatform } from '../platform/context'
import { Link } from 'react-router-dom'
import { ApiError } from '../api/client'
import { beginQrlogSignIn, isPhoneLike, usePhoneLike } from '../lib/qrlog'
import './QrlogSignIn.css'

/** Loaded only when a computer's visitor asks for the QR (it brings the QR library with it). */
const QrLoginDialog = lazy(() => import('./QrLoginDialog'))

/**
 * QRLog's own logo: the "QRLog" wordmark from the QRLog repository (frontend/public/brand/qrlog-logo.png, trimmed and
 * scaled to 96 px high). It stands in for the word itself, so "QRLog ilə daxil ol" reads — and is announced — whole.
 */
export function QrlogLogo({ height = 22, className }: { height?: number; className?: string }) {
  return (
    <img
      src="/brand/qrlog-logo.png"
      alt="QRLog"
      width={Math.round(height * (320 / 96))}
      height={height}
      className={className ? `qrlog-logo ${className}` : 'qrlog-logo'}
      decoding="async"
    />
  )
}

interface Props {
  /** Where to continue after signing in (a same-site path). */
  next?: string | null
  /** "daxil ol" on the login page, "davam et" on registration. */
  verb?: 'daxil ol' | 'davam et'
}

/**
 * "QRLog ilə daxil ol" — the primary option above the e-mail form. Shown only when the deployment has the QRLog
 * door open (see usePublicConfig); the caller decides that. On a phone it continues in QRLog on the same device
 * (QRLog's verified https link opens the app where it is installed, the website where it is not); on a computer or
 * tablet it shows a QR for the phone. Both are the same ticket and the same signed confirmation.
 */
export function QrlogSignIn({ next, verb = 'daxil ol' }: Props) {
  const { platformName } = usePlatform()
  const phone = usePhoneLike()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [qrOpen, setQrOpen] = useState(false)

  const onClick = async () => {
    setError(undefined)
    // Decided at the tap, from the device as it is now (the rendered hint follows the same rule).
    if (!isPhoneLike()) {
      setQrOpen(true)
      return
    }
    setBusy(true)
    try {
      await beginQrlogSignIn(next)
    } catch (err) {
      setBusy(false)
      setError(err instanceof ApiError && err.status === 0 ? err.message : 'QRLog ilə giriş başlamadı. Yenidən cəhd edin.')
    }
  }

  return (
    <div className="qrlog">
      {error && (
        <div className="alert" role="alert">
          {error}
        </div>
      )}
      <button
        type="button"
        className="btn btn--lg qrlog__btn"
        onClick={onClick}
        disabled={busy}
        aria-haspopup={phone ? undefined : 'dialog'}
      >
        <QrlogLogo height={22} className="qrlog__logo" />{' '}
        <span>{busy ? 'açılır…' : `ilə ${verb}`}</span>
      </button>
      {phone && <p className="qrlog__hint">QRLog tətbiqində davam edin</p>}
      <p className="qrlog__note">
        QRLog tətbiqində bir toxunuşla təsdiqləyirsiniz; {platformName} yalnız adınızı və e-mail ünvanınızı alır. Davam etməklə{' '}
        <Link to="/terms">iştirak şərtləri</Link> və <Link to="/privacy">məxfilik siyasəti</Link> ilə razılaşırsınız.
      </p>
      <div className="qrlog__or" role="separator" aria-label="və ya">
        <span>və ya</span>
      </div>
      {qrOpen && (
        <Suspense fallback={null}>
          <QrLoginDialog next={next ?? null} onClose={() => setQrOpen(false)} />
        </Suspense>
      )}
    </div>
  )
}
