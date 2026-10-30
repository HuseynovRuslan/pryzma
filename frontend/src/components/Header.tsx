import { useEffect, useRef, useState } from 'react'
import { Link, NavLink, useLocation } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { authPath } from '../auth/intent'
import { primaryNav } from '../content/nav'
import { usePlatform } from '../platform/context'
import { VotingStatusBadge } from './voting/VotingStatusBadge'
import { Wordmark } from './Logo'
import './Header.css'

export function Header() {
  const { status, user } = useAuth()
  const { platformName } = usePlatform()
  const location = useLocation()
  const [menuOpen, setMenuOpen] = useState(false)
  const [scrolled, setScrolled] = useState(false)
  const menuButton = useRef<HTMLButtonElement>(null)
  const panel = useRef<HTMLDivElement>(null)

  // Auth screens remember where the visitor was, so "Daxil ol" brings them back here afterwards.
  const here = location.pathname.startsWith('/login') || location.pathname.startsWith('/register')
    ? null
    : location.pathname + location.search

  useEffect(() => setMenuOpen(false), [location.pathname])

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 8)
    onScroll()
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])

  useEffect(() => {
    if (!menuOpen) return
    const previous = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    panel.current?.querySelector<HTMLElement>('a, button')?.focus()
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setMenuOpen(false)
        menuButton.current?.focus()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => {
      document.body.style.overflow = previous
      window.removeEventListener('keydown', onKey)
    }
  }, [menuOpen])

  const initials = user ? `${user.firstName[0] ?? ''}${user.lastName[0] ?? ''}`.toUpperCase() : ''

  return (
    <header className={`site-header${scrolled || menuOpen ? ' is-raised' : ''}`}>
      <div className="container site-header__bar">
        <Link to="/" className="site-header__brand" aria-label={`${platformName} — ana səhifə`}>
          <Wordmark />
        </Link>

        <VotingStatusBadge />

        <nav className="site-nav" aria-label="Əsas naviqasiya">
          <ul role="list">
            {primaryNav.map((item) => (
              <li key={item.to}>
                <NavLink to={item.to} className="site-nav__link">
                  {item.label}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>

        <div className="site-header__account">
          {status === 'authenticated' && user ? (
            <>
              <NavLink to="/my/submissions" className="site-nav__link">
                İşlərim
              </NavLink>
              <NavLink to="/my/profile" className="account-chip" aria-label={`Profil: ${user.firstName} ${user.lastName}`}>
                <span className="account-chip__initials" aria-hidden="true">
                  {initials}
                </span>
                <span>Profil</span>
              </NavLink>
            </>
          ) : status === 'anonymous' ? (
            <>
              <Link to={authPath('login', here)} className="btn btn--ghost btn--sm">
                Daxil ol
              </Link>
              <Link to={authPath('register', here)} className="btn btn--primary btn--sm">
                Qeydiyyat
              </Link>
            </>
          ) : (
            <span className="site-header__account-placeholder" aria-hidden="true" />
          )}
        </div>

        <button
          ref={menuButton}
          type="button"
          className="menu-toggle"
          aria-expanded={menuOpen}
          aria-controls="mobile-menu"
          onClick={() => setMenuOpen((v) => !v)}
        >
          <span className="menu-toggle__icon" aria-hidden="true" data-open={menuOpen} />
          <span>{menuOpen ? 'Bağla' : 'Menyu'}</span>
        </button>
      </div>

      <div
        id="mobile-menu"
        ref={panel}
        className="mobile-menu"
        data-open={menuOpen}
        hidden={!menuOpen}
      >
        <nav aria-label="Mobil naviqasiya" className="container">
          <ul role="list" className="mobile-menu__list">
            <li>
              <NavLink to="/" end className="mobile-menu__link">
                Ana səhifə
              </NavLink>
            </li>
            {primaryNav.map((item) => (
              <li key={item.to}>
                <NavLink to={item.to} className="mobile-menu__link">
                  {item.label}
                </NavLink>
              </li>
            ))}
          </ul>

          <div className="mobile-menu__account">
            {status === 'authenticated' && user ? (
              <>
                <p className="mobile-menu__who">
                  {user.firstName} {user.lastName}
                </p>
                <Link to="/my/submissions" className="btn btn--primary btn--block btn--lg">
                  İşlərim
                </Link>
                <Link to="/my/profile" className="btn btn--block btn--lg">
                  Profil
                </Link>
              </>
            ) : status === 'unavailable' ? (
              // Throttled or unreachable is not "signed out": no sign-in buttons that would suggest it was.
              <p className="mobile-menu__who">Giriş vəziyyəti yoxlanılır…</p>
            ) : (
              <>
                <Link to={authPath('register', here)} className="btn btn--primary btn--block btn--lg">
                  Qeydiyyat
                </Link>
                <Link to={authPath('login', here)} className="btn btn--block btn--lg">
                  Daxil ol
                </Link>
              </>
            )}
          </div>
        </nav>
      </div>
    </header>
  )
}
