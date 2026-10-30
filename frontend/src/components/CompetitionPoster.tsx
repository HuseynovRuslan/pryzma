import { Link } from 'react-router-dom'
import type { CompetitionSummary } from '../api/types'
import { accentVars } from '../lib/accent'
import { formatDate } from '../lib/format'
import { ProjectFallback } from './Motifs'
import { PhaseStatus } from './Status'
import './CompetitionPoster.css'

interface Props {
  competition: CompetitionSummary
  headingLevel?: 'h2' | 'h3'
  /** The first card on a page: its poster is worth fetching before anything below the fold. */
  eager?: boolean
}

/**
 * A competition as its campaign: the poster first and large, then the few words a passer-by needs — what state it
 * is in, what it is called, what it asks for, and one way in. The artwork is shown whole, never cropped to fit.
 */
export function CompetitionPoster({ competition: c, headingLevel = 'h3', eager = false }: Props) {
  const Heading = headingLevel
  const cta = c.phase === 'Open' ? 'İştirak et' : 'Ətraflı bax'

  return (
    <article className="poster-card" style={accentVars(c.project.accentColor)}>
      <Link to={`/competitions/${c.slug}`} className="poster-card__link">
        <div className={`poster-card__stage${c.coverImage ? '' : ' poster-card__stage--drawn'}`}>
          {c.coverImage ? (
            <img
              className="poster-card__art"
              src={c.coverImage}
              alt=""
              loading={eager ? 'eager' : 'lazy'}
              fetchPriority={eager ? 'high' : 'auto'}
              decoding="async"
            />
          ) : (
            // No poster uploaded yet: the competition's own colour field, never an empty grey box.
            <>
              <ProjectFallback seed={c.slug} />
              <span className="poster-card__drawn-title">{c.title}</span>
            </>
          )}
        </div>

        <div className="poster-card__body">
          <p className="poster-card__meta">
            <PhaseStatus phase={c.phase} />
            {c.resultsPublished && <span>Nəticələr açıqlanıb</span>}
            {c.endDate && <span>Son tarix: {formatDate(c.endDate)}</span>}
          </p>
          <Heading className="poster-card__title">{c.title}</Heading>
          <p className="poster-card__desc">{c.shortDescription}</p>
          <span className="poster-card__cta">{cta}</span>
        </div>
      </Link>
    </article>
  )
}
