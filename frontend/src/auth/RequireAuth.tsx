import type { ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { useAuth } from './AuthContext'
import { authPath } from './intent'
import { PageLoading, SessionUnavailable } from '../components/PageState'

/** Sends anonymous visitors to login and brings them back to exactly where they were. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { status } = useAuth()
  const location = useLocation()

  if (status === 'loading') return <PageLoading />
  // Throttled or unreachable is not "signed out": wait here instead of sending the person to the login page.
  if (status === 'unavailable') return <SessionUnavailable />
  if (status === 'anonymous') {
    return <Navigate to={authPath('login', location.pathname + location.search)} replace />
  }
  return <>{children}</>
}
