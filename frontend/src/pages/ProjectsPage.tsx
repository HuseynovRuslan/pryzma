import { catalog } from '../api/endpoints'
import { usePlatform } from '../platform/context'
import { Breadcrumbs } from '../components/Breadcrumbs'
import { PageError, PageLoading } from '../components/PageState'
import { ProjectTile } from '../components/ProjectTile'
import { useApi } from '../lib/useApi'
import { useTitle } from '../lib/useTitle'
import './HomePage.css'
import './ListPage.css'

export default function ProjectsPage() {
  const { platformName } = usePlatform()
  useTitle('Layihələr')
  const state = useApi((signal) => catalog.projects(signal), [])

  return (
    <div className="container list-page">
      <Breadcrumbs items={[{ label: platformName, to: '/' }, { label: 'Layihələr' }]} />
      <header className="list-page__head">
        <h1>Layihələr</h1>
        <p className="lede">Hər layihə öz mövzusu ətrafında bir və ya bir neçə müsabiqəni birləşdirir.</p>
      </header>

      {state.status === 'loading' && <PageLoading />}
      {state.status === 'error' && <PageError error={state.error} onRetry={state.retry} />}
      {state.status === 'success' &&
        (state.data.length === 0 ? (
          <div className="empty">
            <h2>Hələ dərc olunmuş layihə yoxdur</h2>
            <p>İlk layihə dərc olunan kimi burada görünəcək.</p>
          </div>
        ) : (
          <div className={`project-grid project-grid--${Math.min(state.data.length, 3)}`}>
            {state.data.map((p, i) => (
              <ProjectTile key={p.id} project={p} index={i + 1} feature={state.data.length === 1} headingLevel="h2" />
            ))}
          </div>
        ))}
    </div>
  )
}
