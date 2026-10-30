import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { authApi } from '../../api/endpoints'
import type { RegistrationVerified } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { authPath, safeNext } from '../../auth/intent'
import { FieldError, PasswordField, SubmitButton, TextField } from '../../components/Form'
import { Turnstile } from '../../components/Turnstile'
import { usePublicConfig } from '../../lib/publicConfig'
import { useTitle } from '../../lib/useTitle'
import {
  focusFirstInvalid,
  fromServer,
  hasErrors,
  validateEmail,
  validatePassword,
  validatePhone,
  type Errors,
} from '../../lib/validation'
import { AuthLayout } from './AuthLayout'

/**
 * Email-first registration (when the server says `registrationFlow: "email-first"`). Three screens:
 * the address and the robot check → "check your mail" → (the link) → name, phone and password. No account exists
 * until the last one is sent. Between the link and the form the browser keeps only the short-lived proof the server
 * gave, in this tab's session storage, and drops it as soon as it is used or out of date.
 */

const PROOF_KEY = 'registration.proof'
const NEXT_KEY = 'registration.next'

function readProof(): RegistrationVerified | null {
  try {
    const raw = sessionStorage.getItem(PROOF_KEY)
    if (!raw) return null
    const proof = JSON.parse(raw) as RegistrationVerified
    return new Date(proof.expiresAt).getTime() > Date.now() ? proof : null
  } catch {
    return null
  }
}

function forgetProof() {
  try {
    sessionStorage.removeItem(PROOF_KEY)
  } catch {
    /* storage unavailable: nothing to forget */
  }
}

/** Step 1, shown by RegisterPage in place of the one-step form. */
export function EmailFirstStart({ next }: { next: string | null }) {
  const config = usePublicConfig()
  const siteKey = config.captchaSiteKey ?? null
  const formRef = useRef<HTMLFormElement>(null)
  const [email, setEmail] = useState('')
  const [errors, setErrors] = useState<Errors>({})
  const [formError, setFormError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [sentTo, setSentTo] = useState<string | null>(null)
  const [captcha, setCaptcha] = useState<string | null>(null)
  const [round, setRound] = useState(0)
  const [notice, setNotice] = useState<string>()
  // The address already has an account (the server answers 409): say so, and offer the ways in.
  const [existing, setExisting] = useState(false)

  const send = async (e?: FormEvent) => {
    e?.preventDefault()
    setFormError(undefined)
    setNotice(undefined)
    const address = (sentTo ?? email).trim()
    const found: Errors = {
      email: sentTo ? undefined : validateEmail(address),
      captcha: siteKey && !captcha ? 'Robot olmadığınızı təsdiqləyin: yoxlamanı tamamlayın.' : undefined,
    }
    setErrors(found)
    if (hasErrors(found)) return focusFirstInvalid(formRef.current)
    setBusy(true)
    try {
      await authApi.registrationStart({ email: address, ...(siteKey && captcha ? { captchaToken: captcha } : {}) })
      try {
        if (next) sessionStorage.setItem(NEXT_KEY, next)
      } catch {
        /* the way back is a convenience */
      }
      if (sentTo) setNotice('Yeni link göndərildi.')
      setSentTo(address)
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setExisting(true)
        return
      }
      const apiErr = err instanceof ApiError ? err : new ApiError(0, 'Bağlantı alınmadı. Yenidən cəhd edin.')
      const server = fromServer(apiErr.fieldErrors)
      setErrors(server)
      setFormError(server[''] ?? (server.email || server.captcha ? undefined : apiErr.message))
    } finally {
      // A robot-check token works once: ask for a fresh one for the next send.
      setCaptcha(null)
      setRound((n) => n + 1)
      setBusy(false)
    }
  }

  const check = siteKey && (
    <div className="field">
      <Turnstile
        key={round}
        siteKey={siteKey}
        action="register"
        onToken={(token) => {
          setCaptcha(token)
          if (token && errors.captcha) setErrors((er) => ({ ...er, captcha: undefined }))
        }}
      />
      {errors.captcha && <FieldError message={errors.captcha} />}
    </div>
  )

  if (existing)
    return (
      <div className="auth-form">
        <h2 className="auth-form__title">Bu e-mail ilə artıq hesab mövcuddur</h2>
        <p>Daxil olun və ya şifrənizi unutmusunuzsa, bərpa edin.</p>
        <Link to={authPath('login', next)} className="btn btn--primary btn--lg">
          Daxil ol
        </Link>
        <Link to="/forgot-password" className="btn btn--lg">
          Şifrəni unutmuşam
        </Link>
        <p className="auth-form__switch">
          <button type="button" className="link-button" onClick={() => setExisting(false)}>
            Başqa e-mail yaz
          </button>
        </p>
      </div>
    )

  if (sentTo)
    return (
      <form ref={formRef} className="auth-form" onSubmit={send} noValidate>
        <h2 className="auth-form__title">Təsdiq linkini göndərdik</h2>
        <p>
          E-mailinizi yoxlayın və qeydiyyata davam edin. Link <strong>{sentTo}</strong> ünvanına göndərildi və 45 dəqiqə
          etibarlıdır.
        </p>
        <p className="field__hint">
          Məktub gəlməyibsə, spam qovluğuna baxın və ya bir dəqiqə sonra yenisini istəyin. Əvvəlki məktublardakı linklər
          də 45 dəqiqə ərzində işləyir. Alınmırsa, <Link to="/contact">bizə yazın</Link>.
        </p>
        {notice && (
          <div className="alert alert--success" role="status">
            {notice}
          </div>
        )}
        {formError && (
          <div className="alert" role="alert">
            {formError}
          </div>
        )}
        {check}
        <SubmitButton busy={busy}>Linki yenidən göndər</SubmitButton>
        <p className="auth-form__switch">
          <button type="button" className="link-button" onClick={() => setSentTo(null)}>
            Başqa e-mail yazın
          </button>
        </p>
      </form>
    )

  return (
    <form ref={formRef} className="auth-form" onSubmit={send} noValidate>
      <h2 className="auth-form__title">E-mailinizi təsdiqləyin</h2>
      <p>Qeydiyyata davam etmək üçün e-mail ünvanınıza təsdiq linki göndərəcəyik.</p>
      {formError && (
        <div className="alert" role="alert">
          {formError}
        </div>
      )}
      <TextField
        label="E-mail"
        type="email"
        name="email"
        autoComplete="email"
        inputMode="email"
        autoCapitalize="none"
        spellCheck={false}
        value={email}
        onChange={(e) => {
          setEmail(e.target.value)
          if (errors.email) setErrors((er) => ({ ...er, email: undefined }))
        }}
        error={errors.email}
      />
      {check}
      <SubmitButton busy={busy}>Təsdiq linki göndər</SubmitButton>
      <p className="auth-form__switch">
        Artıq hesabınız var? <Link to={authPath('login', next)}>Daxil olun</Link>
      </p>
    </form>
  )
}

/** Step 2: the link from the mail. Spends the token at once and moves on to the form. */
export function RegisterVerifyPage() {
  useTitle('E-mail təsdiqi')
  const navigate = useNavigate()
  const [token] = useState(() => new URLSearchParams(window.location.hash.slice(1)).get('token') ?? '')
  const [error, setError] = useState<string | null>(token ? null : 'E-maildəki linki tam açın və ya yenisini istəyin.')
  const sent = useRef(false)

  useEffect(() => {
    if (window.location.hash) window.history.replaceState(window.history.state, '', window.location.pathname)
    if (!token || sent.current) return
    sent.current = true
    authApi
      .registrationVerify({ token })
      .then((proof) => {
        try {
          sessionStorage.setItem(PROOF_KEY, JSON.stringify(proof))
        } catch {
          /* without storage the proof travels in navigation state */
        }
        navigate('/register/complete', { replace: true, state: { proof } })
      })
      .catch((err) => {
        const apiErr = err instanceof ApiError ? err : null
        setError(apiErr?.fieldErrors.token?.[0] ?? apiErr?.message ?? 'Link işləmədi. Yenisini istəyin.')
      })
  }, [token, navigate])

  if (!error)
    return (
      <AuthLayout title="E-mail təsdiqlənir…">
        <p className="lede" role="status">
          Bir an gözləyin.
        </p>
      </AuthLayout>
    )

  return (
    <AuthLayout title="Link işləmədi">
      <div className="alert" role="alert">
        {error}
      </div>
      <Link to="/register" className="btn btn--primary btn--lg">
        Yeni link istə
      </Link>
      <p className="field__hint">
        Alınmırsa, <Link to="/contact">Əlaqə</Link> səhifəsindən bizə yazın.
      </p>
    </AuthLayout>
  )
}

/** Step 3: name, phone and password for the proven address; creates the account and signs it in. */
export function RegisterCompletePage() {
  useTitle('Qeydiyyat')
  const navigate = useNavigate()
  const { applySession } = useAuth()
  const [proof] = useState<RegistrationVerified | null>(() => {
    const fromState = (window.history.state?.usr as { proof?: RegistrationVerified } | undefined)?.proof
    return fromState && new Date(fromState.expiresAt).getTime() > Date.now() ? fromState : readProof()
  })
  const formRef = useRef<HTMLFormElement>(null)
  const [values, setValues] = useState({
    firstName: '',
    lastName: '',
    phone: '',
    password: '',
    confirmPassword: '',
    acceptTerms: false,
  })
  const [errors, setErrors] = useState<Errors>({})
  const [formError, setFormError] = useState<string>()
  const [expired, setExpired] = useState(false)
  const [busy, setBusy] = useState(false)

  if (!proof || expired)
    return (
      <AuthLayout title="Qeydiyyatın vaxtı bitib">
        <div className="alert" role="alert">
          E-mailinizi yenidən təsdiqləyin: qeydiyyat formu linki açandan sonra 30 dəqiqə ərzində göndərilməlidir.
        </div>
        <Link to="/register" className="btn btn--primary btn--lg">
          E-maili yenidən təsdiqlə
        </Link>
      </AuthLayout>
    )

  const set = <K extends keyof typeof values>(key: K, value: (typeof values)[K]) => {
    setValues((v) => ({ ...v, [key]: value }))
    if (errors[key]) setErrors((e) => ({ ...e, [key]: undefined }))
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setFormError(undefined)
    const found: Errors = {
      firstName: values.firstName.trim() ? undefined : 'Adınızı yazın.',
      lastName: values.lastName.trim() ? undefined : 'Soyadınızı yazın.',
      phone: validatePhone(values.phone),
      password: validatePassword(values.password),
      confirmPassword: !values.confirmPassword
        ? 'Şifrəni təkrar yazın.'
        : values.confirmPassword !== values.password
          ? 'Şifrələr üst-üstə düşmür.'
          : undefined,
      acceptTerms: values.acceptTerms ? undefined : 'Davam etmək üçün şərtlərlə razılaşın.',
    }
    setErrors(found)
    if (hasErrors(found)) return focusFirstInvalid(formRef.current)
    setBusy(true)
    try {
      const session = await authApi.registrationComplete({ proof: proof.proof, ...values })
      forgetProof()
      let next: string | null = null
      try {
        next = sessionStorage.getItem(NEXT_KEY)
        sessionStorage.removeItem(NEXT_KEY)
      } catch {
        /* no way back stored */
      }
      applySession(session)
      navigate(safeNext(next, '/'), { replace: true })
    } catch (err) {
      const apiErr = err instanceof ApiError ? err : new ApiError(0, 'Qeydiyyat alınmadı.')
      const server = fromServer(apiErr.fieldErrors)
      if (server.proof) {
        forgetProof()
        setExpired(true)
        return
      }
      setErrors(server)
      setFormError(server[''] ?? server.email ?? apiErr.message)
      setBusy(false)
      focusFirstInvalid(formRef.current)
    }
  }

  return (
    <AuthLayout title="Qeydiyyat" intro={<p>{proof.email} təsdiqləndi. Qeydiyyatı tamamlayın.</p>}>
      <form ref={formRef} className="auth-form" onSubmit={onSubmit} noValidate>
        {formError && (
          <div className="alert" role="alert">
            {formError}
          </div>
        )}
        <div className="auth-form__row">
          <TextField
            label="Ad"
            name="firstName"
            autoComplete="given-name"
            value={values.firstName}
            onChange={(e) => set('firstName', e.target.value)}
            error={errors.firstName}
          />
          <TextField
            label="Soyad"
            name="lastName"
            autoComplete="family-name"
            value={values.lastName}
            onChange={(e) => set('lastName', e.target.value)}
            error={errors.lastName}
          />
        </div>
        <TextField
          label="Telefon"
          type="tel"
          name="phone"
          autoComplete="tel"
          inputMode="tel"
          placeholder="+994 50 123 45 67"
          value={values.phone}
          onChange={(e) => set('phone', e.target.value)}
          error={errors.phone}
        />
        <PasswordField
          label="Şifrə"
          name="password"
          autoComplete="new-password"
          hint="Ən azı 8 simvol, içində rəqəm və kiçik hərf olsun."
          value={values.password}
          onChange={(e) => set('password', e.target.value)}
          error={errors.password}
        />
        <PasswordField
          label="Şifrəni təkrar et"
          name="confirmPassword"
          autoComplete="new-password"
          value={values.confirmPassword}
          onChange={(e) => set('confirmPassword', e.target.value)}
          error={errors.confirmPassword}
        />
        <div className="field">
          <label className="check">
            <input
              type="checkbox"
              name="acceptTerms"
              checked={values.acceptTerms}
              aria-invalid={errors.acceptTerms ? true : undefined}
              onChange={(e) => set('acceptTerms', e.target.checked)}
            />
            <span>
              <Link to="/terms" target="_blank" rel="noopener">
                İştirak şərtləri
              </Link>{' '}
              ilə razıyam
            </span>
          </label>
          {errors.acceptTerms && <FieldError message={errors.acceptTerms} />}
        </div>
        <SubmitButton busy={busy}>Hesab yarat</SubmitButton>
      </form>
    </AuthLayout>
  )
}
