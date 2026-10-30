import { Link } from 'react-router-dom'
import { usePlatform } from '../platform/context'
import { resultsApi } from '../api/endpoints'
import type { ResultListItem } from '../api/types'
import { Breadcrumbs } from '../components/Breadcrumbs'
import { ProjectFallback } from '../components/Motifs'
import { PageError, PageLoading } from '../components/PageState'
import { accentVars } from '../lib/accent'
import { formatDate } from '../lib/format'
import { useApi } from '../lib/useApi'
import { useTitle } from '../lib/useTitle'
import './ListPage.css'
import './results/Results.css'

/** Every competition whose result an admin has published, newest first. */
export default function ResultsPage() {
  const { platformName } = usePlatform()
  useTitle('Nəticələr')
  const state = useApi((signal) => resultsApi.list(signal), [])
  return (
    <div className="container list-page">
      <Breadcrumbs items={[{ label: platformName, to: '/' }, { label: 'Nəticələr' }]} />
      <header className="list-page__head">
        <h1>Nəticələr</h1>
        <p className="lede">Başa çatmış müsabiqələrin qalibləri və müsabiqəyə göndərilmiş işlər.</p>
      </header>
      {state.status === 'loading' && <PageLoading />}
      {state.status === 'error' && <PageError error={state.error} onRetry={state.retry} />}
      {state.status === 'success' &&
        (state.data.length === 0 ? (
          <div className="empty">
            <h2>Hələ elan olunmuş nəticə yoxdur</h2>
            <p>Nəticələr elan olunanda bu səhifədə görünəcək. Hazırda qəbulu açıq olan müsabiqələrə baxa bilərsiniz.</p>
            <Link to="/competitions" className="btn btn--primary">
              Açıq müsabiqələrə bax
            </Link>
          </div>
        ) : (
          <ul role="list" className="rlist">
            {state.data.map((r) => (
              <ResultRow key={r.competition.slug} r={r} />
            ))}
          </ul>
        ))}
    </div>
  )
}

/**
 * One competition's results: its own results banner (the admin's choice — a poster is shown whole), else the
 * competition's or project's cover, else the project's pattern. Never one winner's artwork presented as "the" winner.
 */
function ResultRow({ r }: { r: ResultListItem }) {
  const c = r.competition
  const to = `/competitions/${c.slug}/results`
  const label = (c.categoryLabel ?? 'Kateqoriyalar').toLocaleLowerCase('az').replace(/(lar|lər)$/u, '')
  return (
    <li className="rlist__item" style={accentVars(c.project.accentColor)}>
      <Link to={to} className={`rlist__visual${r.coverKind === 'Result' ? ' rlist__visual--poster' : ''}`} tabIndex={-1} aria-hidden="true">
        {r.coverImage ? <img src={r.coverImage} alt="" loading="lazy" decoding="async" /> : <ProjectFallback seed={c.slug} />}
      </Link>
      <div className="rlist__text">
        <p className="rlist__project">
          <span className="rlist__swatch" aria-hidden="true" />
          {c.project.name}
        </p>
        <h2 className="rlist__title">
          <Link to={to}>{c.title}</Link>
        </h2>
        <dl className="rlist__facts">
          {c.hasCategories && r.categoryCount > 0 && (
            <div>
              <dt>{label}</dt>
              <dd className="tabular">{r.categoryCount}</dd>
            </div>
          )}
          <div>
            <dt>qalib</dt>
            <dd className="tabular">{r.winnerCount}</dd>
          </div>
          <div>
            <dt>təqdim edilmiş iş</dt>
            <dd className="tabular">{r.entryCount}</dd>
          </div>
        </dl>
        <p className="rlist__meta">
          Nəticələr <time dateTime={r.publishedAt}>{formatDate(r.publishedAt)}</time> tarixində açıqlanıb
        </p>
        <div className="rlist__actions">
          <Link to={to} className="btn btn--sm btn--primary">
            Nəticələrə bax
          </Link>
          <Link to={`/competitions/${c.slug}/gallery`} className="btn btn--sm">
            İşlərə bax
          </Link>
        </div>
      </div>
    </li>
  )
}
