import { useParams } from 'react-router-dom'
import { usePlatform } from '../platform/context'
import { catalog } from '../api/endpoints'
import { Breadcrumbs } from '../components/Breadcrumbs'
import { CompetitionRow } from '../components/CompetitionRow'
import { PageError, PageLoading } from '../components/PageState'
import { ProjectFallback } from '../components/Motifs'
import { accentVars } from '../lib/accent'
import { useApi } from '../lib/useApi'
import { useTitle } from '../lib/useTitle'
import './ProjectDetailPage.css'

export default function ProjectDetailPage() {
  const { platformName } = usePlatform()
  const { slug = '' } = useParams()
  const state = useApi((signal) => catalog.project(slug, signal), [slug])
  useTitle(state.data?.name)

  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />

  const project = state.data
  const current = project.competitions.filter((c) => c.phase === 'Open' || c.phase === 'Upcoming')
  const finished = project.competitions.filter((c) => c.phase !== 'Open' && c.phase !== 'Upcoming')
  const openCount = project.competitions.filter((c) => c.phase === 'Open').length

  return (
    <div style={accentVars(project.accentColor)}>
      <section className={`project-hero${project.coverImage ? ' project-hero--cover' : ''}`} aria-labelledby="project-title">
        {/* A project's own image wins; the drawn fallback is only for a project without one. */}
        {project.coverImage ? (
          <img className="project-hero__cover" src={project.coverImage} alt="" fetchPriority="high" decoding="async" />
        ) : (
          <div className="project-hero__motif">
            <ProjectFallback seed={project.slug} />
          </div>
        )}
        <div className="container project-hero__inner">
          <Breadcrumbs
            className="project-hero__crumbs"
            items={[{ label: platformName, to: '/' }, { label: 'Layihələr', to: '/projects' }, { label: project.name }]}
          />
          <h1 id="project-title" className="project-hero__title">
            {project.name}
          </h1>
          <div className="project-hero__foot">
            <p className="project-hero__lede">{project.shortDescription}</p>
            <p className="project-hero__count">
              {openCount > 0 ? `${openCount} aktiv müsabiqə` : 'Hazırda aktiv müsabiqə yoxdur'}
            </p>
          </div>
        </div>
      </section>

      {project.description && (
        <section className="section container" aria-labelledby="about-title">
          <div className="project-about">
            <h2 id="about-title">Layihə haqqında</h2>
            <div className="prose">
              {project.description.split(/\n{2,}/).map((para, i) => (
                <p key={i}>{para}</p>
              ))}
            </div>
          </div>
        </section>
      )}

      <section className="section container" aria-labelledby="current-title">
        <header className="section-head">
          <h2 id="current-title">Aktiv müsabiqələr</h2>
        </header>
        {current.length === 0 ? (
          <div className="empty">
            <h3>Bu layihədə hazırda açıq müsabiqə yoxdur</h3>
            <p>Yeni müsabiqə elan olunanda burada görünəcək.</p>
          </div>
        ) : (
          <div>
            {current.map((c) => (
              <CompetitionRow key={c.id} competition={c} showProject={false} />
            ))}
          </div>
        )}
      </section>

      {finished.length > 0 && (
        <section className="section container" aria-labelledby="finished-title">
          <header className="section-head">
            <h2 id="finished-title">Bitmiş müsabiqələr</h2>
          </header>
          <div>
            {finished.map((c) => (
              <CompetitionRow key={c.id} competition={c} showProject={false} />
            ))}
          </div>
        </section>
      )}
    </div>
  )
}
