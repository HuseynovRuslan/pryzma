import { Link } from 'react-router-dom'
import type { CompetitionSummary } from '../api/types'
import { accentVars } from '../lib/accent'
import { competitionCardStatus } from '../lib/competitionCard'
import { formatDate } from '../lib/format'
import { ProjectFallback } from './Motifs'
import './CompetitionCard.css'

interface Props {
  competition: CompetitionSummary
  headingLevel?: 'h2' | 'h3'
  eager?: boolean
}

/**
 * A compact institutional competition card. Its contents are deliberately limited to CompetitionSummary:
 * project, cover, status, title, description, deadline and the boolean prize signal — never invented details.
 */
export function CompetitionCard({ competition: c, headingLevel = 'h3', eager = false }: Props) {
  const Heading = headingLevel
  const status = competitionCardStatus(c)

  return (
    <article className="competition-card" style={accentVars(c.project.accentColor)}>
      <Link to={`/competitions/${c.slug}`} className="competition-card__link">
        <div className={`competition-card__media${c.coverImage ? '' : ' competition-card__media--drawn'}`}>
          {c.coverImage ? (
            <>
              <img
                className="competition-card__backdrop"
                src={c.coverImage}
                alt=""
                aria-hidden="true"
                loading={eager ? 'eager' : 'lazy'}
                fetchPriority={eager ? 'high' : 'auto'}
                decoding="async"
              />
              <img
                className="competition-card__image"
                src={c.coverImage}
                alt=""
                loading={eager ? 'eager' : 'lazy'}
                fetchPriority={eager ? 'high' : 'auto'}
                decoding="async"
              />
            </>
          ) : (
            <ProjectFallback seed={c.slug} />
          )}
        </div>

        <div className="competition-card__body">
          <div className="competition-card__badges">
            <span className={`competition-card__status competition-card__status--${status.tone}`}>
              <span aria-hidden="true" />
              {status.label}
            </span>
            {c.hasPrize && <span className="competition-card__prize">Mükafatlı müsabiqə</span>}
          </div>

          <p className="competition-card__project">{c.project.name}</p>
          <Heading className="competition-card__title">{c.title}</Heading>
          {c.shortDescription && <p className="competition-card__description">{c.shortDescription}</p>}

          <div className="competition-card__foot">
            {c.endDate && (
              <p className="competition-card__deadline">
                <span>Son tarix</span>
                <strong className="tabular">{formatDate(c.endDate)}</strong>
              </p>
            )}
            <span className="competition-card__cta">Ətraflı</span>
          </div>
        </div>
      </Link>
    </article>
  )
}
