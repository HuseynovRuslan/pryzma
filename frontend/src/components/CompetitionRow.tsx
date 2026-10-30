import { Link } from 'react-router-dom'
import type { CompetitionSummary } from '../api/types'
import { accentVars } from '../lib/accent'
import { formatDate } from '../lib/format'
import { PhaseStatus } from './Status'
import './CompetitionRow.css'

interface Props {
  competition: CompetitionSummary
  showProject?: boolean
}

/** Editorial list row for a competition — reads like an entry on a notice board, not a product card. */
export function CompetitionRow({ competition: c, showProject = true }: Props) {
  const facts: string[] = []
  if (c.hasCategories && c.categoryCount > 0) {
    facts.push(`${c.categoryLabel ?? 'Kateqoriyalar'}: ${c.categoryCount}`)
  }
  if (c.endDate) facts.push(`Son tarix: ${formatDate(c.endDate)}`)
  if (c.hasPrize) facts.push('Mükafatlı')
  if (c.resultsPublished) facts.unshift('Nəticələr açıqlanıb')

  return (
    <article
      className={`competition-row${c.coverImage ? ' competition-row--poster' : ''}`}
      style={accentVars(c.project.accentColor)}
    >
      <Link to={`/competitions/${c.slug}`} className="competition-row__link">
        <span className="competition-row__swatch" aria-hidden="true" />
        <div className="competition-row__main">
          {showProject && <p className="competition-row__project">{c.project.name}</p>}
          <h3 className="competition-row__title">{c.title}</h3>
          <p className="competition-row__desc">{c.shortDescription}</p>
        </div>
        {/* The competition's own poster, small; decorative next to its title. */}
        {c.coverImage && <img className="competition-row__poster" src={c.coverImage} alt="" loading="lazy" decoding="async" />}
        <div className="competition-row__meta">
          <PhaseStatus phase={c.phase} />
          {facts.length > 0 && (
            <ul role="list" className="competition-row__facts">
              {facts.map((f) => (
                <li key={f}>{f}</li>
              ))}
            </ul>
          )}
          <span className="competition-row__cta">Müsabiqəyə bax</span>
        </div>
      </Link>
    </article>
  )
}
