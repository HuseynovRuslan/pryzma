import { Suspense, useEffect, useState, type ReactNode } from 'react'
import { Link, Navigate, NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { authPath } from '../auth/intent'
import { PageLoading, SessionUnavailable } from '../components/PageState'
import { usePlatform } from '../platform/context'
import './admin.css'

const nav = [
  { to: '/admin', label: 'İcmal', end: true },
  { to: '/admin/projects', label: 'Layihələr' },
  { to: '/admin/competitions', label: 'Müsabiqələr' },
  { to: '/admin/submissions', label: 'Təkliflər' },
  { to: '/admin/participants', label: 'İştirakçılar' },
  { to: '/admin/settings', label: 'Parametrlər' },
]

/**
 * Only for the Admin role. This is a convenience gate: every admin API call is authorised on the server
 * regardless of what the browser shows.
 */
export function RequireAdmin({ children }: { children: ReactNode }) {
  const { status, user } = useAuth()
  const location = useLocation()
  if (status === 'loading') return <PageLoading />
  if (status === 'unavailable') return <SessionUnavailable />
  if (status === 'anonymous') return <Navigate to={authPath('login', location.pathname + location.search)} replace />
  if (!user?.roles.includes('Admin')) {
    return (
      <div className="a-denied" role="alert">
        <h1>Bu bölmə yalnız administratorlar üçündür</h1>
        <p>Hesabınızın idarəetmə panelinə girişi yoxdur.</p>
        <Link to="/" className="btn btn--primary">
          Sayta qayıt
        </Link>
      </div>
    )
  }
  return <>{children}</>
}

export function AdminLayout() {
  const { user, logout } = useAuth()
  const { platformName } = usePlatform()
  const location = useLocation()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)

  useEffect(() => setOpen(false), [location.pathname])

  const onLogout = async () => {
    await logout()
    navigate('/', { replace: true })
  }

  return (
    <RequireAdmin>
      <div className="admin">
        <a href="#admin-main" className="skip-link">
          Məzmuna keç
        </a>
        <header className="a-topbar">
          <Link to="/admin" className="a-brand">
            <span className="a-brand__word">{platformName}</span>
            <span className="a-brand__role">İdarəetmə</span>
          </Link>
          <button type="button" className="a-topbar__menu" aria-expanded={open} aria-controls="admin-nav" onClick={() => setOpen((v) => !v)}>
            {open ? 'Bağla' : 'Menyu'}
          </button>
        </header>

        <aside id="admin-nav" className="a-sidebar" data-open={open}>
          <Link to="/admin" className="a-brand a-brand--side">
            <span className="a-brand__word">{platformName}</span>
            <span className="a-brand__role">İdarəetmə</span>
          </Link>
          <nav aria-label="İdarəetmə">
            <ul role="list" className="a-nav">
              {nav.map((item) => (
                <li key={item.to}>
                  <NavLink to={item.to} end={item.end} className="a-nav__link">
                    {item.label}
                  </NavLink>
                </li>
              ))}
            </ul>
          </nav>
          <div className="a-sidebar__foot">
            <Link to="/" className="a-sidebar__site">
              Sayta keç
            </Link>
            <p className="a-sidebar__user">
              {user?.firstName} {user?.lastName}
            </p>
            <button type="button" className="a-sidebar__logout" onClick={onLogout}>
              Çıxış
            </button>
          </div>
        </aside>

        <main id="admin-main" className="a-main" tabIndex={-1}>
          <Suspense fallback={<PageLoading />}>
            <Outlet />
          </Suspense>
        </main>
      </div>
    </RequireAdmin>
  )
}
