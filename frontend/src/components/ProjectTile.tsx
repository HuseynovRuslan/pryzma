import { Link } from 'react-router-dom'
import type { ProjectCard } from '../api/types'
import { accentVars } from '../lib/accent'
import { ordinal, projectStatusLabel } from '../lib/format'
import { ProjectFallback } from './Motifs'
import './ProjectTile.css'

interface Props {
  project: ProjectCard
  index: number
  feature?: boolean
  headingLevel?: 'h2' | 'h3'
}

export function ProjectTile({ project, index, feature = false, headingLevel = 'h3' }: Props) {
  const Heading = headingLevel
  const open = project.openCompetitionCount
  return (
    <article className={`project-tile${feature ? ' project-tile--feature' : ''}`} style={accentVars(project.accentColor)}>
      <Link to={`/projects/${project.slug}`} className="project-tile__link">
        <div className={`project-tile__plate${project.coverImage ? ' project-tile__plate--cover' : ''}`}>
          {/* The project's own image is the visual; the drawn fallback is only for a project without one. */}
          {project.coverImage ? (
            <>
              <img
                className="project-tile__cover"
                src={project.coverImage}
                alt=""
                loading={feature ? 'eager' : 'lazy'}
                fetchPriority={feature ? 'high' : 'auto'}
                decoding="async"
              />
              <span className="project-tile__scrim" aria-hidden="true" />
            </>
          ) : (
            <ProjectFallback seed={project.slug} />
          )}
          <span className="project-tile__index tabular" aria-hidden="true">
            {ordinal(index)}
          </span>
          <Heading className="project-tile__name">{project.name}</Heading>
        </div>

        <div className="project-tile__body">
          <p className="project-tile__desc">{project.shortDescription}</p>
          <dl className="project-tile__facts">
            <div>
              <dt className="visually-hidden">Status</dt>
              <dd>{projectStatusLabel[project.status]}</dd>
            </div>
            <div>
              <dt className="visually-hidden">Aktiv müsabiqələr</dt>
              <dd className={open > 0 ? 'project-tile__open' : undefined}>
                {open > 0 ? `${open} aktiv müsabiqə` : 'Aktiv müsabiqə yoxdur'}
              </dd>
            </div>
          </dl>
          <span className="project-tile__cta">Layihəyə bax</span>
        </div>
      </Link>
    </article>
  )
}
