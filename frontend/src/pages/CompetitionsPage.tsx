import { Link } from 'react-router-dom'
import { usePlatform } from '../platform/context'
import { catalog } from '../api/endpoints'
import type { CompetitionSummary } from '../api/types'
import { Breadcrumbs } from '../components/Breadcrumbs'
import { CompetitionPoster } from '../components/CompetitionPoster'
import { PageError, PageLoading } from '../components/PageState'
import { useApi } from '../lib/useApi'
import { useTitle } from '../lib/useTitle'
import './ListPage.css'

const groups: { title: string; match: (c: CompetitionSummary) => boolean }[] = [
  { title: 'Qəbul açıqdır', match: (c) => c.phase === 'Open' },
  { title: 'Tezliklə', match: (c) => c.phase === 'Upcoming' },
  { title: 'Bitmiş müsabiqələr', match: (c) => c.phase !== 'Open' && c.phase !== 'Upcoming' },
]

export default function CompetitionsPage() {
  const { platformName } = usePlatform()
  useTitle('Müsabiqələr')
  const state = useApi((signal) => catalog.competitions(signal), [])

  return (
    <div className="container list-page">
      <Breadcrumbs items={[{ label: platformName, to: '/' }, { label: 'Müsabiqələr' }]} />
      <header className="list-page__head">
        <h1>Müsabiqələr</h1>
        <p className="lede">Açıq çağırışlar yuxarıdadır. Afişaya toxunun — müsabiqənin öz səhifəsi açılır.</p>
      </header>

      {state.status === 'loading' && <PageLoading />}
      {state.status === 'error' && <PageError error={state.error} onRetry={state.retry} />}
      {state.status === 'success' &&
        (state.data.length === 0 ? (
          <div className="empty">
            <h2>Hələ elan olunmuş müsabiqə yoxdur</h2>
            <p>Yeni müsabiqələr elan olunanda burada görünəcək.</p>
            <Link to="/" className="btn">
              Ana səhifəyə qayıt
            </Link>
          </div>
        ) : (
          groups.map((g) => {
            const items = state.data.filter(g.match)
            if (items.length === 0) return null
            return (
              <section key={g.title} className="list-group" aria-label={g.title}>
                <h2 className="list-group__title">{g.title}</h2>
                <div className={`poster-grid poster-grid--${Math.min(items.length, 3)}`}>
                  {items.map((c, i) => (
                    <CompetitionPoster key={c.id} competition={c} eager={g.title === 'Qəbul açıqdır' && i < 2} />
                  ))}
                </div>
              </section>
            )
          })
        ))}
    </div>
  )
}
