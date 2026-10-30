import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { authApi } from '../../api/endpoints'
import { useTitle } from '../../lib/useTitle'
import { AuthLayout } from './AuthLayout'

/**
 * The link from the confirmation e-mail. Like the reset link it carries the address and the token in the fragment
 * (#email=…&token=…), which never reaches a server log; it is read once and stripped from the address bar. The
 * link is opened at once — there is nothing to fill in — and a link opened twice is still a success.
 */
export default function ConfirmEmailPage() {
  useTitle('E-mail təsdiqi')
  const [{ email, token }] = useState(() => {
    const p = new URLSearchParams(window.location.hash.slice(1))
    return { email: p.get('email') ?? '', token: p.get('token') ?? '' }
  })
  const [state, setState] = useState<'pending' | 'done' | 'error'>(email && token ? 'pending' : 'error')
  const [message, setMessage] = useState<string>()
  const sent = useRef(false)

  useEffect(() => {
    if (window.location.hash) window.history.replaceState(window.history.state, '', window.location.pathname)
    if (!email || !token || sent.current) return
    sent.current = true
    authApi
      .confirmEmail({ email, token })
      .then(() => setState('done'))
      .catch((err) => {
        const apiErr = err instanceof ApiError ? err : null
        setMessage(apiErr?.fieldErrors.token?.[0] ?? apiErr?.message)
        setState('error')
      })
  }, [email, token])

  if (state === 'pending')
    return (
      <AuthLayout title="E-mail təsdiqlənir…">
        <p className="lede" role="status">
          Bir an gözləyin.
        </p>
      </AuthLayout>
    )

  if (state === 'done')
    return (
      <AuthLayout title="E-mail təsdiqləndi">
        <div className="alert alert--success" role="status">
          E-mail ünvanınız təsdiqləndi. İndi səs verə bilərsiniz.
        </div>
        <Link to="/" className="btn btn--primary btn--lg">
          Ana səhifəyə qayıt
        </Link>
      </AuthLayout>
    )

  return (
    <AuthLayout title="Link işləmədi">
      <div className="alert" role="alert">
        {message ?? 'E-maildəki linki tam açın. Link köhnədirsə, hesabınıza daxil olub yenisini istəyin.'}
      </div>
      <Link to="/" className="btn btn--primary btn--lg">
        Ana səhifəyə qayıt
      </Link>
    </AuthLayout>
  )
}
