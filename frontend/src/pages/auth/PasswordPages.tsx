import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { authApi } from '../../api/endpoints'
import { PasswordField, SubmitButton, TextField } from '../../components/Form'
import { usePublicConfig } from '../../lib/publicConfig'
import { useTitle } from '../../lib/useTitle'
import {
  focusFirstInvalid,
  fromServer,
  hasErrors,
  validateEmail,
  validatePassword,
  type Errors,
} from '../../lib/validation'
import { AuthLayout } from './AuthLayout'

export function ForgotPasswordPage() {
  useTitle('Şifrəni bərpa et')
  const config = usePublicConfig()
  const formRef = useRef<HTMLFormElement>(null)
  const [email, setEmail] = useState('')
  const [error, setError] = useState<string>()
  const [formError, setFormError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [sent, setSent] = useState(false)

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    const found = validateEmail(email)
    setError(found)
    setFormError(undefined)
    if (found) return focusFirstInvalid(formRef.current)
    setBusy(true)
    try {
      await authApi.forgotPassword(email.trim())
      setSent(true)
    } catch (err) {
      setFormError(err instanceof ApiError ? err.message : 'Sorğu göndərilmədi.')
    } finally {
      setBusy(false)
    }
  }

  // No mail provider on this deployment yet: say so before anyone types an address, instead of promising a message
  // that cannot be sent. Whether an address has an account is still never revealed — the form is simply not offered.
  if (config.ready && !config.passwordReset) {
    return (
      <AuthLayout title="Şifrəni bərpa et">
        <div className="alert" role="status">
          Şifrənin e-mail ilə bərpası bu saytda hələ aktiv deyil.
        </div>
        <p className="lede">
          Şifrənizi unutmusunuzsa, təşkilatçıya müraciət edin. QRLog hesabınız varsa, giriş səhifəsində “QRLog ilə daxil
          ol” seçimi ilə şifrəsiz daxil ola bilərsiniz.
        </p>
        <Link to="/login" className="btn btn--lg">
          Girişə qayıt
        </Link>
      </AuthLayout>
    )
  }

  if (sent) {
    return (
      <AuthLayout title="E-maili yoxlayın">
        <div className="alert alert--success" role="status">
          <strong>{email.trim()}</strong> ünvanı ilə hesab varsa, şifrəni yeniləmək üçün link göndərdik.
        </div>
        <p className="muted">Məktub gəlmirsə, “Spam” qovluğuna baxın və ya bir neçə dəqiqədən sonra yenidən cəhd edin.</p>
        <Link to="/login" className="btn btn--lg">
          Girişə qayıt
        </Link>
      </AuthLayout>
    )
  }

  return (
    <AuthLayout
      title="Şifrəni bərpa et"
      intro={<p>Hesabınızın e-mail ünvanını yazın. Şifrəni yeniləmək üçün link göndərəcəyik.</p>}
    >
      <form ref={formRef} className="auth-form" onSubmit={onSubmit} noValidate>
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
          onChange={(e) => setEmail(e.target.value)}
          error={error}
        />
        <SubmitButton busy={busy}>Link göndər</SubmitButton>
        <p className="auth-form__switch">
          Şifrəni xatırladınız? <Link to="/login">Daxil olun</Link>
        </p>
      </form>
    </AuthLayout>
  )
}

/**
 * The reset link carries email and token in the URL fragment (#email=…&token=…), which browsers never
 * send to a server. Read it once, then strip it from the address bar and history.
 */
function useResetParams() {
  const [initial] = useState(() => new URLSearchParams(window.location.hash.slice(1)))
  useEffect(() => {
    if (window.location.hash) {
      window.history.replaceState(window.history.state, '', window.location.pathname)
    }
  }, [])
  return { email: initial.get('email') ?? '', token: initial.get('token') ?? '' }
}

export function ResetPasswordPage() {
  useTitle('Yeni şifrə')
  const { email, token } = useResetParams()
  const formRef = useRef<HTMLFormElement>(null)
  const [values, setValues] = useState({ password: '', confirmPassword: '' })
  const [errors, setErrors] = useState<Errors>({})
  const [formError, setFormError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)

  if (!email || !token) {
    return (
      <AuthLayout title="Link natamamdır">
        <p className="lede">E-maildəki linki tam açın və ya yeni link istəyin.</p>
        <Link to="/forgot-password" className="btn btn--primary btn--lg">
          Yeni link istə
        </Link>
      </AuthLayout>
    )
  }

  if (done) {
    return (
      <AuthLayout title="Şifrə yeniləndi">
        <div className="alert alert--success" role="status">
          Yeni şifrə ilə daxil ola bilərsiniz. Bütün cihazlardakı girişlər bağlandı — hər yerdə yeni şifrə ilə yenidən daxil olun.
        </div>
        <Link to="/login" className="btn btn--primary btn--lg">
          Daxil ol
        </Link>
      </AuthLayout>
    )
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    const found: Errors = {
      password: validatePassword(values.password),
      confirmPassword: values.confirmPassword === values.password ? undefined : 'Şifrələr üst-üstə düşmür.',
    }
    setErrors(found)
    setFormError(undefined)
    if (hasErrors(found)) return focusFirstInvalid(formRef.current)
    setBusy(true)
    try {
      await authApi.resetPassword({ email, token, ...values })
      setDone(true)
    } catch (err) {
      const apiErr = err instanceof ApiError ? err : new ApiError(0, 'Şifrə yenilənmədi.')
      const server = fromServer(apiErr.fieldErrors)
      setErrors(server)
      setFormError(server.token ?? apiErr.message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthLayout title="Yeni şifrə" intro={<p>{email} hesabı üçün yeni şifrə təyin edin.</p>}>
      <form ref={formRef} className="auth-form" onSubmit={onSubmit} noValidate>
        {formError && (
          <div className="alert" role="alert">
            {formError}
          </div>
        )}
        <PasswordField
          label="Yeni şifrə"
          autoComplete="new-password"
          hint="Ən azı 8 simvol, içində rəqəm və kiçik hərf olsun."
          value={values.password}
          onChange={(e) => setValues({ ...values, password: e.target.value })}
          error={errors.password}
        />
        <PasswordField
          label="Şifrəni təkrar et"
          autoComplete="new-password"
          value={values.confirmPassword}
          onChange={(e) => setValues({ ...values, confirmPassword: e.target.value })}
          error={errors.confirmPassword}
        />
        <SubmitButton busy={busy}>Şifrəni yenilə</SubmitButton>
      </form>
    </AuthLayout>
  )
}
