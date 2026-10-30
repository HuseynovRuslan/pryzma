import { Link } from 'react-router-dom'
import { primaryNav } from '../content/nav'
import { usePlatform } from '../platform/context'
import './Footer.css'

export function Footer() {
  const { platformName, homeHeroSubtitle } = usePlatform()
  return (
    <footer className="site-footer">
      <div className="container site-footer__inner">
        <div className="site-footer__brand">
          <p className="site-footer__word">{platformName}</p>
          <p className="site-footer__line">{homeHeroSubtitle}</p>
        </div>

        <nav aria-label="Alt naviqasiya" className="site-footer__nav">
          <ul role="list">
            {primaryNav.map((item) => (
              <li key={item.to}>
                <Link to={item.to}>{item.label}</Link>
              </li>
            ))}
            <li>
              <Link to="/about">{platformName} haqqında</Link>
            </li>
            <li>
              <Link to="/terms">İştirak şərtləri</Link>
            </li>
            <li>
              <Link to="/privacy">Məxfilik</Link>
            </li>
          </ul>
        </nav>

        <p className="site-footer__legal">© {new Date().getFullYear()} {platformName}</p>
      </div>
    </footer>
  )
}
