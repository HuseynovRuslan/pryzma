import { lazy, Suspense, useEffect, useRef, useState, type FormEvent } from 'react'
import { usePlatform } from '../../platform/context'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { meApi } from '../../api/endpoints'
import type { LoginsInfo } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { SubmitButton, TextField } from '../../components/Form'
import { QrlogLogo } from '../../components/QrlogSignIn'
import { beginQrlogSignIn, isPhoneLike } from '../../lib/qrlog'
import { usePublicConfig } from '../../lib/publicConfig'
import { useTitle } from '../../lib/useTitle'
import { focusFirstInvalid, fromServer, hasErrors, phoneDigits, validatePhone, type Errors } from '../../lib/validation'
import { AccountNav } from './AccountNav'
import './Account.css'

const QrLoginDialog = lazy(() => import('../../components/QrLoginDialog'))

export default function ProfilePage() {
  const { platformName } = usePlatform()
  useTitle('Profil')
  const { user, setUser, logout } = useAuth()
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const config = usePublicConfig()
  const formRef = useRef<HTMLFormElement>(null)
  const [logins, setLogins] = useState<LoginsInfo>()
  const [loginsMessage, setLoginsMessage] = useState<{ kind: 'success' | 'error'; text: string }>()
  const [loginsBusy, setLoginsBusy] = useState(false)
  const [values, setValues] = useState({
    firstName: user?.firstName ?? '',
    lastName: user?.lastName ?? '',
    phone: user?.phone ?? '',
  })
  const [errors, setErrors] = useState<Errors>({})
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string }>()
  const [busy, setBusy] = useState(false)
  const [linkQrOpen, setLinkQrOpen] = useState(false)

  // Which sign-in methods this account has; only meaningful where the QRLog door is open.
  useEffect(() => {
    if (!config.qrlogLogin) return
    const ctrl = new AbortController()
    meApi
      .logins(ctrl.signal)
      .then(setLogins)
      .catch(() => undefined)
    return () => ctrl.abort()
  }, [config.qrlogLogin])

  // Back from a successful "QRLog hesabını bağla": say so once and clean the address.
  useEffect(() => {
    if (params.get('linked') !== '1') return
    setLoginsMessage({ kind: 'success', text: 'QRLog hesabı bağlandı. Bundan sonra QRLog ilə də daxil ola bilərsiniz.' })
    setParams({}, { replace: true })
  }, [params, setParams])

  if (!user) return null

  const linkQrlog = async () => {
    setLoginsMessage(undefined)
    // On a computer the phone scans a QR; on a phone QRLog opens right here.
    if (!isPhoneLike()) {
      setLinkQrOpen(true)
      return
    }
    setLoginsBusy(true)
    try {
      await beginQrlogSignIn('/my/profile', true)
    } catch {
      setLoginsBusy(false)
      setLoginsMessage({ kind: 'error', text: 'QRLog açılmadı. Yenidən cəhd edin.' })
    }
  }

  const unlinkQrlog = async () => {
    setLoginsBusy(true)
    setLoginsMessage(undefined)
    try {
      await meApi.unlinkQrlog()
      setLogins((l) => (l ? { ...l, qrlog: false } : l))
      setLoginsMessage({ kind: 'success', text: 'QRLog bağlantısı ayrıldı.' })
    } catch (err) {
      setLoginsMessage({ kind: 'error', text: err instanceof ApiError ? err.message : 'Bağlantı ayrılmadı.' })
    } finally {
      setLoginsBusy(false)
    }
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setMessage(undefined)
    const found: Errors = {
      firstName: values.firstName.trim() ? undefined : 'Adınızı yazın.',
      lastName: values.lastName.trim() ? undefined : 'Soyadınızı yazın.',
      // A number the account already has stays as it is, however it was written then; only a changed one is checked.
      phone: phoneDigits(values.phone) === phoneDigits(user.phone) ? undefined : validatePhone(values.phone),
    }
    setErrors(found)
    if (hasErrors(found)) return focusFirstInvalid(formRef.current)
    setBusy(true)
    try {
      const updated = await meApi.updateProfile(values)
      setUser(updated)
      setMessage({ kind: 'success', text: 'Dəyişikliklər yadda saxlanıldı.' })
    } catch (err) {
      const apiErr = err instanceof ApiError ? err : new ApiError(0, 'Yadda saxlanılmadı.')
      setErrors(fromServer(apiErr.fieldErrors))
      setMessage({ kind: 'error', text: apiErr.message })
    } finally {
      setBusy(false)
    }
  }

  const onLogout = async () => {
    await logout()
    navigate('/', { replace: true })
  }

  return (
    <div className="container account">
      <header className="account__head">
        <p className="account__who">{user.email || `${user.firstName} ${user.lastName}`}</p>
        <h1>Profil</h1>
        <AccountNav />
      </header>

      <div className="profile">
        <form ref={formRef} className="auth-form profile__form" onSubmit={onSubmit} noValidate>
          {message && (
            <div className={`alert${message.kind === 'success' ? ' alert--success' : ''}`} role={message.kind === 'error' ? 'alert' : 'status'}>
              {message.text}
            </div>
          )}
          <div className="auth-form__row">
            <TextField
              label="Ad"
              autoComplete="given-name"
              value={values.firstName}
              onChange={(e) => setValues({ ...values, firstName: e.target.value })}
              error={errors.firstName}
            />
            <TextField
              label="Soyad"
              autoComplete="family-name"
              value={values.lastName}
              onChange={(e) => setValues({ ...values, lastName: e.target.value })}
              error={errors.lastName}
            />
          </div>
          <TextField
            label="Telefon"
            type="tel"
            inputMode="tel"
            autoComplete="tel"
            value={values.phone}
            onChange={(e) => setValues({ ...values, phone: e.target.value })}
            error={errors.phone}
          />
          <TextField
            label="E-mail"
            type="email"
            value={user.email}
            readOnly
            hint={
              user.email
                ? 'E-mail hesabın girişi üçün istifadə olunur və burada dəyişdirilmir.'
                : 'Bu hesab QRLog ilə yaradılıb və QRLog qeydinizdə e-mail yoxdur; giriş QRLog ilə davam edir.'
            }
          />
          <SubmitButton busy={busy}>Yadda saxla</SubmitButton>
        </form>

        <aside className="profile__aside">
          {user.roles.includes('Admin') && (
            <>
              <h2>İdarəetmə</h2>
              <p className="muted">Layihələri, müsabiqələri və təklifləri idarə edin.</p>
              <Link to="/admin" className="btn btn--primary">
                İdarəetmə paneli
              </Link>
            </>
          )}
          {config.qrlogLogin && (
            <div className="logins">
              <h2>Bağlı hesablar</h2>
              {loginsMessage && (
                <div className={`alert${loginsMessage.kind === 'success' ? ' alert--success' : ''}`} role={loginsMessage.kind === 'error' ? 'alert' : 'status'}>
                  {loginsMessage.text}
                </div>
              )}
              {logins?.qrlog ? (
                <>
                  <p className="logins__status">
                    <QrlogLogo height={16} /> hesabı bağlıdır
                  </p>
                  <p className="muted">
                    {logins.hasPassword
                      ? 'QRLog ilə də, e-mail və şifrə ilə də daxil ola bilərsiniz.'
                      : 'Bu hesaba yalnız QRLog ilə daxil olursunuz. Bağlantını ayırmaq üçün əvvəlcə “Şifrəni unutmuşam” ilə şifrə təyin edin.'}
                  </p>
                  <button type="button" className="btn" onClick={unlinkQrlog} disabled={loginsBusy || !logins.hasPassword}>
                    Bağlantını ayır
                  </button>
                </>
              ) : user.roles.includes('Admin') ? (
                <p className="muted">İdarəetmə hesabı QRLog ilə bağlanmır: bu hesaba yalnız e-mail və şifrə ilə daxil olunur.</p>
              ) : (
                <>
                  <p className="muted">
                    QRLog hesabınız varsa, onu bağlayın — sonra {platformName}-a QRLog ilə bir toxunuşla daxil
                    olarsınız.
                  </p>
                  <button type="button" className="btn" onClick={linkQrlog} disabled={loginsBusy || !logins}>
                    <QrlogLogo height={16} /> hesabını bağla
                  </button>
                </>
              )}
              {linkQrOpen && (
                <Suspense fallback={null}>
                  <QrLoginDialog
                    next="/my/profile"
                    link
                    onClose={() => setLinkQrOpen(false)}
                    onLinked={() => {
                      setLinkQrOpen(false)
                      meApi
                        .logins()
                        .then(setLogins)
                        .catch(() => undefined)
                      setLoginsMessage({ kind: 'success', text: 'QRLog hesabı bağlandı. Bundan sonra QRLog ilə də daxil ola bilərsiniz.' })
                    }}
                  />
                </Suspense>
              )}
            </div>
          )}
          <h2>Sessiya</h2>
          <p className="muted">Bu cihazda hesabdan çıxın. Digər cihazlardakı girişlər qalır.</p>
          <button type="button" className="btn" onClick={onLogout}>
            Hesabdan çıx
          </button>
        </aside>
      </div>
    </div>
  )
}
