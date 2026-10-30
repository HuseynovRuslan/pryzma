import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError } from '../api/client'
import { Wordmark } from '../components/Logo'
import { useTitle } from '../lib/useTitle'
import { juryApi, tokenFromHash, TOKEN_KEY, type JurorSession } from './api'
import './jury.css'

/**
 * The juror's frame: the wordmark, the competition, the juror's own name and a way out — nothing of the public site's
 * navigation and nothing of the administration. Search engines are told to stay away.
 */
export function JuryShell({ session, children }: { session?: JurorSession; children: ReactNode }) {
  const navigate = useNavigate()
  useEffect(() => {
    const meta = document.createElement('meta')
    meta.name = 'robots'
    meta.content = 'noindex, nofollow'
    document.head.appendChild(meta)
    return () => meta.remove()
  }, [])
  const signOut = async () => {
    try {
      await juryApi.signOut()
    } finally {
      navigate('/jury/start', { replace: true })
    }
  }
  return (
    <div className="jr">
      <header className="jr-top">
        <Link to="/jury/evaluate" className="jr-top__brand" aria-label="Münsif paneli">
          <Wordmark />
        </Link>
        <span className="jr-top__role">Münsif paneli</span>
        {session && (
          <span className="jr-top__who">
            <span className="jr-top__name">{session.jurorName}</span>
            <button type="button" className="jr-link" onClick={signOut}>
              Çıxış
            </button>
          </span>
        )}
      </header>
      <main className="jr-main" id="main">
        {children}
      </main>
    </div>
  )
}

function readStoredToken(): string | null {
  try {
    return sessionStorage.getItem(TOKEN_KEY)
  } catch {
    return null
  }
}

function storeToken(token: string | null) {
  try {
    if (token) sessionStorage.setItem(TOKEN_KEY, token)
    else sessionStorage.removeItem(TOKEN_KEY)
  } catch {
    // Private mode or blocked storage: the token stays in this page's memory only.
  }
}

type Stage =
  | { kind: 'checking' }
  | { kind: 'code'; token: string }
  | { kind: 'no-link' }
  | { kind: 'confirm'; session: JurorSession }
  | { kind: 'not-me'; session: JurorSession }

/**
 * /jury/start#t=… — the invitation link. The secret is read from the fragment (it never reached a server), kept for
 * this tab only and taken out of the address bar at once, so it is neither in the history nor in a screenshot. The
 * juror then types the separate access code; then confirms that the name shown is theirs.
 */
export function JuryStartPage() {
  useTitle('Münsif girişi')
  const navigate = useNavigate()
  const [stage, setStage] = useState<Stage>({ kind: 'checking' })
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  useEffect(() => {
    const fromLink = tokenFromHash(window.location.hash)
    if (fromLink) {
      storeToken(fromLink)
      window.history.replaceState(null, '', window.location.pathname + window.location.search)
    }
    const token = fromLink ?? readStoredToken()
    const controller = new AbortController()
    // A juror who is already signed in goes on; a new link always asks for its code (it may be another juror's).
    juryApi
      .session(controller.signal)
      .then((session) => {
        if (fromLink) setStage({ kind: 'code', token: fromLink })
        else if (!session.confirmed) setStage({ kind: 'confirm', session })
        else navigate('/jury/evaluate', { replace: true })
      })
      .catch(() => {
        if (controller.signal.aborted) return
        setStage(token ? { kind: 'code', token } : { kind: 'no-link' })
      })
    return () => controller.abort()
  }, [navigate])

  const signIn = async (token: string) => {
    setBusy(true)
    setError(undefined)
    try {
      const session = await juryApi.signIn(token, code)
      storeToken(null)
      setCode('')
      if (session.confirmed) navigate('/jury/evaluate', { replace: true })
      else setStage({ kind: 'confirm', session })
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Giriş alınmadı.')
    } finally {
      setBusy(false)
    }
  }

  const confirm = async () => {
    setBusy(true)
    setError(undefined)
    try {
      await juryApi.confirm()
      navigate('/jury/evaluate', { replace: true })
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Təsdiq alınmadı.')
      setBusy(false)
    }
  }

  const notMe = async (session: JurorSession) => {
    setBusy(true)
    try {
      await juryApi.signOut()
    } finally {
      setBusy(false)
      setStage({ kind: 'not-me', session })
    }
  }

  return (
    <JuryShell>
      <section className="jr-gate" aria-labelledby="jr-gate-title">
        {stage.kind === 'checking' && <p className="jr-muted">Yoxlanılır…</p>}

        {stage.kind === 'no-link' && (
          <>
            <p className="jr-eyebrow">Münsif girişi</p>
            <h1 id="jr-gate-title" className="jr-gate__title">
              Dəvət linki lazımdır
            </h1>
            <p className="jr-gate__text">
              Münsif paneli yalnız şəxsi dəvət linki və giriş kodu ilə açılır. Sizə göndərilən linki açın; link işləmirsə,
              təşkilatçıdan yeni dəvət istəyin.
            </p>
          </>
        )}

        {stage.kind === 'code' && (
          <form
            onSubmit={(e) => {
              e.preventDefault()
              void signIn(stage.token)
            }}
          >
            <p className="jr-eyebrow">Münsif girişi</p>
            <h1 id="jr-gate-title" className="jr-gate__title">
              Giriş kodunu yazın
            </h1>
            <p className="jr-gate__text">
              Linklə yanaşı sizə ayrıca 6 rəqəmli giriş kodu verilib. Kod 5 dəfə səhv yazılsa, dəvət kilidlənir.
            </p>
            <label className="jr-field">
              <span className="jr-field__label">Giriş kodu</span>
              <input
                className="jr-code"
                inputMode="numeric"
                autoComplete="one-time-code"
                pattern="[0-9 \-]*"
                maxLength={9}
                value={code}
                onChange={(e) => setCode(e.target.value)}
                aria-invalid={error ? true : undefined}
                aria-describedby={error ? 'jr-gate-error' : undefined}
                autoFocus
                required
              />
            </label>
            {error && (
              <p id="jr-gate-error" className="jr-error" role="alert">
                {error}
              </p>
            )}
            <button type="submit" className="btn btn--primary jr-gate__go" disabled={busy || code.replace(/\D/g, '').length !== 6}>
              {busy ? 'Yoxlanılır…' : 'Daxil ol'}
            </button>
          </form>
        )}

        {stage.kind === 'confirm' && (
          <>
            <p className="jr-eyebrow">{stage.session.competitionTitle}</p>
            <h1 id="jr-gate-title" className="jr-gate__title">
              Adınızı təsdiqləyin
            </h1>
            <p className="jr-gate__name">{stage.session.jurorName}</p>
            <p className="jr-gate__text">
              Dəvət bu ada verilib və verdiyiniz ballar bu adla qeyd olunacaq. Ad sizin deyilsə və ya səhv yazılıbsa, davam
              etməyin — təşkilatçıya bildirin.
            </p>
            {error && (
              <p className="jr-error" role="alert">
                {error}
              </p>
            )}
            <div className="jr-gate__actions">
              <button type="button" className="btn btn--primary" onClick={confirm} disabled={busy}>
                Bəli, bu mənəm
              </button>
              <button type="button" className="btn" onClick={() => notMe(stage.session)} disabled={busy}>
                Xeyr, bu mən deyiləm
              </button>
            </div>
          </>
        )}

        {stage.kind === 'not-me' && (
          <>
            <p className="jr-eyebrow">Münsif girişi</p>
            <h1 id="jr-gate-title" className="jr-gate__title">
              Sessiya bağlandı
            </h1>
            <p className="jr-gate__text">
              Bu dəvət {stage.session.jurorName} adına idi. Heç bir bal verilmədi. Zəhmət olmasa, təşkilatçıya müraciət edib
              öz adınıza dəvət istəyin.
            </p>
          </>
        )}
      </section>
    </JuryShell>
  )
}
