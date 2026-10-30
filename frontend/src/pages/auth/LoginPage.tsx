import { useRef, useState, type FormEvent } from 'react'
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { useAuth } from '../../auth/AuthContext'
import { authPath, safeNext } from '../../auth/intent'
import { PasswordField, SubmitButton, TextField } from '../../components/Form'
import { QrlogSignIn } from '../../components/QrlogSignIn'
import { usePublicConfig } from '../../lib/publicConfig'
import { focusFirstInvalid, fromServer, hasErrors, validateLogin, type Errors } from '../../lib/validation'
import { useTitle } from '../../lib/useTitle'
import { AuthLayout } from './AuthLayout'

export default function LoginPage() {
  useTitle('Daxil ol')
  const { login, status } = useAuth()
  const config = usePublicConfig()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const next = params.get('next')
  const destination = safeNext(next, '/')

  const formRef = useRef<HTMLFormElement>(null)
  const [values, setValues] = useState({ email: '', password: '', rememberMe: false })
  const [errors, setErrors] = useState<Errors>({})
  const [formError, setFormError] = useState<string>()
  // Refused credentials (or a locked account): say what usually lies behind it. Most refusals in October 2026 were
  // addresses with no account yet — people who had not finished registering — trying again and again.
  const [refused, setRefused] = useState(false)
  const [busy, setBusy] = useState(false)

  if (status === 'authenticated' && !busy) return <Navigate to={destination} replace />

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setFormError(undefined)
    const found = validateLogin(values)
    setErrors(found)
    if (hasErrors(found)) return focusFirstInvalid(formRef.current)

    setBusy(true)
    try {
      await login({ ...values, email: values.email.trim() })
      navigate(destination, { replace: true })
    } catch (err) {
      const apiErr = err instanceof ApiError ? err : new ApiError(0, 'Daxil olmaq alınmadı.')
      setErrors(fromServer(apiErr.fieldErrors))
      setFormError(apiErr.message)
      setRefused(apiErr.status === 401 || apiErr.status === 423)
      setBusy(false)
    }
  }

  return (
    <AuthLayout
      title="Daxil ol"
      next={next}
      intro={
        // Most visitors arrive from Instagram on "İşini təqdim et" and land here first; many took "Daxil ol" for the way
        // in and tried an address with no account (October 2026). The first thing they read is where to start.
        <div className="auth-first">
          <p>
            <strong>Saytda ilk dəfəsinizsə</strong>, əvvəlcə qeydiyyatdan keçin: hesab olmadan daxil olmaq mümkün deyil.
          </p>
          <Link to={authPath('register', next)} className="btn">
            Qeydiyyatdan keçin
          </Link>
        </div>
      }
    >
      {config.qrlogLogin && <QrlogSignIn next={next} verb="daxil ol" />}
      <form ref={formRef} className="auth-form" onSubmit={onSubmit} noValidate>
        {formError && (
          <div className="alert" role="alert">
            {formError}
          </div>
        )}
        {refused && (
          <div className="auth-help">
            <p className="auth-help__title">Daxil ola bilmirsiniz?</p>
            <ul role="list">
              <li>
                Hesabınız yoxdursa və ya qeydiyyatı bitirməmisinizsə,{' '}
                <Link to={authPath('register', next)}>qeydiyyatdan keçin</Link>. Hesab e-poçtunuza gələn linki açıb
                formu doldurandan sonra yaranır.
              </li>
              <li>
                Şifrəni unutmusunuzsa, <Link to="/forgot-password">şifrəni bərpa edin</Link>.
              </li>
              {config.qrlogLogin && <li>Əvvəllər «QRLog ilə daxil ol» düyməsi ilə girmisinizsə, yenə onu seçin.</li>}
            </ul>
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
          value={values.email}
          onChange={(e) => setValues({ ...values, email: e.target.value })}
          error={errors.email}
        />
        <PasswordField
          label="Şifrə"
          name="password"
          autoComplete="current-password"
          value={values.password}
          onChange={(e) => setValues({ ...values, password: e.target.value })}
          error={errors.password}
        />
        <div className="auth-form__inline">
          <label className="check">
            <input
              type="checkbox"
              checked={values.rememberMe}
              onChange={(e) => setValues({ ...values, rememberMe: e.target.checked })}
            />
            <span>Məni xatırla</span>
          </label>
          <Link to="/forgot-password">Şifrəni unutmuşam</Link>
        </div>
        <SubmitButton busy={busy}>Daxil ol</SubmitButton>
      </form>
    </AuthLayout>
  )
}
