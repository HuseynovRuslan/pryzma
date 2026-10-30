import { Link } from 'react-router-dom'
import type { ApiError } from '../api/client'
import { useAuth } from '../auth/AuthContext'

export function PageLoading({ label = 'Yüklənir' }: { label?: string }) {
  return (
    <div className="container page-state" role="status" aria-live="polite">
      <span className="visually-hidden">{label}…</span>
      <div className="skeleton" style={{ height: 18, width: 140 }} />
      <div className="skeleton" style={{ height: 64, width: 'min(640px, 100%)' }} />
      <div className="skeleton" style={{ height: 18, width: 'min(420px, 90%)' }} />
    </div>
  )
}

export function PageError({ error, onRetry }: { error: ApiError; onRetry?: () => void }) {
  if (error.status === 404) return <NotFoundState message={error.message} />
  return (
    <div className="container page-state" role="alert">
      <h1>Səhifə yüklənmədi</h1>
      <p className="lede">{error.message}</p>
      {onRetry && (
        <button type="button" className="btn btn--primary" onClick={onRetry}>
          Yenidən yüklə
        </button>
      )}
    </div>
  )
}

/**
 * The sign-in state could not be checked just now (the server throttled the check or was out of reach). Shown
 * instead of sending the person to the login page: nothing was cleared, and the check repeats on its own.
 */
export function SessionUnavailable() {
  const { retry } = useAuth()
  return (
    <div className="container page-state" role="status" aria-live="polite">
      <h1>Giriş vəziyyəti bu an yoxlanıla bilmədi</h1>
      <p className="lede">
        Server qısa müddətə çox məşğuldur və ya əlaqə kəsilib. Heç nə silinməyib — bir neçə saniyədən sonra özü yenidən
        yoxlayacaq.
      </p>
      <button type="button" className="btn btn--primary" onClick={retry}>
        İndi yenidən yoxla
      </button>
    </div>
  )
}

export function NotFoundState({ message }: { message?: string }) {
  return (
    <div className="container page-state">
      <h1>Səhifə tapılmadı</h1>
      <p className="lede">{message ?? 'Link köhnəlmiş və ya səhv yazılmış ola bilər.'}</p>
      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
        <Link to="/" className="btn btn--primary">
          Ana səhifəyə qayıt
        </Link>
        <Link to="/competitions" className="btn">
          Müsabiqələrə bax
        </Link>
      </div>
    </div>
  )
}
