import { NavLink } from 'react-router-dom'

export function AccountNav() {
  return (
    <nav aria-label="Şəxsi kabinet" className="account-nav">
      <NavLink to="/my/submissions">İşlərim</NavLink>
      <NavLink to="/my/profile">Profil</NavLink>
    </nav>
  )
}
