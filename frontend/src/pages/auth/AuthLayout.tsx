import type { ReactNode } from 'react'
import { participatePath, readIntent } from '../../auth/intent'
import { usePlatform } from '../../platform/context'
import './AuthLayout.css'

/** The picture the app ships with, shown until an admin uploads one of their own. */
const DEFAULT_PROMO_IMAGE = '/images/projects/prizma.jpeg'

interface Props {
  title: string
  intro?: ReactNode
  children: ReactNode
  /** Post-auth destination. When it is a pending participation, the page says what it is for. */
  next?: string | null
}

export function AuthLayout({ title, intro, children, next }: Props) {
  const { authPromoImage } = usePlatform()
  const saved = readIntent()
  const intent = saved && next?.startsWith(participatePath(saved.competitionSlug)) ? saved : null

  return (
    <div className="container auth">
      <div className="auth__main">
        {intent && (
          <div className="auth__intent" aria-live="polite">
            <p className="auth__intent-lead">Seçiminiz saxlanılıb</p>
            <p className="auth__intent-title">{intent.competitionTitle}</p>
            <p className="auth__intent-meta">
              {intent.projectName}
              {intent.categoryName && (
                <>
                  <span aria-hidden="true"> / </span>
                  {intent.categoryName}
                </>
              )}
            </p>
          </div>
        )}
        <h1 className="auth__title">{title}</h1>
        {intro && <div className="auth__intro">{intro}</div>}
        {children}
      </div>

      <aside className="auth__aside auth__aside--image" aria-hidden="true">
        {/* An admin's picture when there is one; PRIZMA's own is what the app ships with. */}
        <img className="auth__image" src={authPromoImage ?? DEFAULT_PROMO_IMAGE} alt="" decoding="async" />
      </aside>
    </div>
  )
}
