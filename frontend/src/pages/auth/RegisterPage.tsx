import { useRef, useState, type FormEvent } from 'react'
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router-dom'
import { ApiError } from '../../api/client'
import type { RegisterPayload } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { authPath, safeNext } from '../../auth/intent'
import { FieldError, PasswordField, SubmitButton, TextField } from '../../components/Form'
import { QrlogSignIn } from '../../components/QrlogSignIn'
import { Turnstile } from '../../components/Turnstile'
import { EmailFirstStart } from './EmailFirstRegistration'
import { usePublicConfig } from '../../lib/publicConfig'
import { focusFirstInvalid, fromServer, hasErrors, validateRegister, type Errors } from '../../lib/validation'
import { useTitle } from '../../lib/useTitle'
import { AuthLayout } from './AuthLayout'

const empty: RegisterPayload = {
  firstName: '',
  lastName: '',
  phone: '',
  email: '',
  password: '',
  confirmPassword: '',
  acceptTerms: false,
}

export default function RegisterPage() {
  useTitle('Qeydiyyat')
  const { register, status } = useAuth()
  const config = usePublicConfig()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const next = params.get('next')
  const destination = safeNext(next, '/')

  const formRef = useRef<HTMLFormElement>(null)
  const [values, setValues] = useState<RegisterPayload>(empty)
  const [errors, setErrors] = useState<Errors>({})
  const [formError, setFormError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [captcha, setCaptcha] = useState<string | null>(null)
  // A token works once: after a refused submit the check is shown afresh for a new one.
  const [captchaRound, setCaptchaRound] = useState(0)
  const siteKey = config.captchaSiteKey ?? null

  if (status === 'authenticated' && !busy) return <Navigate to={destination} replace />

  const set = <K extends keyof RegisterPayload>(key: K, value: RegisterPayload[K]) => {
    setValues((v) => ({ ...v, [key]: value }))
    // Clear a field's error as soon as the person starts fixing it.
    if (errors[key]) setErrors((e) => ({ ...e, [key]: undefined }))
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setFormError(undefined)
    const found: Errors = {
      ...validateRegister(values),
      captcha: siteKey && !captcha ? 'Robot olmadığınızı təsdiqləyin: yoxlamanı tamamlayın.' : undefined,
    }
    setErrors(found)
    if (hasErrors(found)) return focusFirstInvalid(formRef.current)

    setBusy(true)
    try {
      await register({ ...values, email: values.email.trim(), ...(siteKey && captcha ? { captchaToken: captcha } : {}) })
      navigate(destination, { replace: true })
    } catch (err) {
      const apiErr = err instanceof ApiError ? err : new ApiError(0, 'Qeydiyyat alınmadı.')
      const serverErrors = fromServer(apiErr.fieldErrors)
      setErrors(serverErrors)
      setFormError(serverErrors[''] ?? apiErr.message)
      setCaptcha(null)
      setCaptchaRound((n) => n + 1)
      setBusy(false)
      focusFirstInvalid(formRef.current)
    }
  }

  return (
    <AuthLayout
      title="Qeydiyyat"
      next={next}
      intro={
        <p>
          Artıq hesabınız var? <Link to={authPath('login', next)}>Daxil olun</Link>
        </p>
      }
    >
      {config.qrlogLogin && <QrlogSignIn next={next} verb="davam et" />}
      {/* Which form this site uses is known only once /api/config answers. Until then nothing to fill in: the
          one-step form used to show first, and on a slow phone people typed into it before it turned into the
          e-mail step and their words were gone. */}
      {!config.ready ? (
        <p className="lede" role="status" aria-busy="true">
          Yüklənir…
        </p>
      ) : config.registrationFlow === 'email-first' ? (
        <EmailFirstStart next={next} />
      ) : (
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
          <TextField
            label="E-mail"
            type="email"
            name="email"
            autoComplete="email"
            inputMode="email"
            autoCapitalize="none"
            spellCheck={false}
            value={values.email}
            onChange={(e) => set('email', e.target.value)}
            error={errors.email}
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
          {siteKey && (
            <div className="field">
              <Turnstile
                key={captchaRound}
                siteKey={siteKey}
                action="register"
                onToken={(token) => {
                  setCaptcha(token)
                  if (token && errors.captcha) setErrors((e) => ({ ...e, captcha: undefined }))
                }}
              />
              {errors.captcha && <FieldError message={errors.captcha} />}
            </div>
          )}
          <SubmitButton busy={busy}>Hesab yarat</SubmitButton>
        </form>
      )}
    </AuthLayout>
  )
}
