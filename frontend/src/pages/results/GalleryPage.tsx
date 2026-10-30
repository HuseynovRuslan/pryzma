import { useState } from 'react'
import { usePlatform } from '../../platform/context'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { resultsApi } from '../../api/endpoints'
import { Breadcrumbs } from '../../components/Breadcrumbs'
import { PageError, PageLoading } from '../../components/PageState'
import { accentVars } from '../../lib/accent'
import { formatDate } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { GalleryFilter } from './GalleryFilter'
import { GalleryCard, ResultsLocked } from './shared'
import './Results.css'

/** Entries of a competition, available only after its results are published (the API refuses before). */
/** Cards rendered at first; the rest on request, so hundreds of works stay light on phones. */
const PAGE = 48

export default function GalleryPage() {
  const { platformName } = usePlatform()
  const { slug = '' } = useParams()
  const [params] = useSearchParams()
  const category = params.get('category')
  // One request for the whole gallery; switching category filters locally, so the filter keeps its search text.
  const state = useApi((signal) => resultsApi.gallery(slug, null, signal), [slug])
  // Remembers how many cards to show per category, so switching the filter starts from the top.
  const [shown, setShown] = useState<{ key: string | null; count: number }>({ key: null, count: PAGE })
  useTitle(state.data ? `İşlər — ${state.data.competition.title}` : 'İşlər')

  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error')
    return state.error.status === 404 ? <ResultsLocked slug={slug} /> : <PageError error={state.error} onRetry={state.retry} />
  const g = state.data
  const c = g.competition
  const label = c.categoryLabel ?? 'Kateqoriyalar'
  const active = g.categories.find((x) => x.slug === category) ?? null
  const items = active ? g.items.filter((i) => i.category?.slug === active.slug) : g.items
  const limit = shown.key === (active?.slug ?? null) ? shown.count : PAGE
  const visible = items.slice(0, limit)

  return (
    <div className="gallery" style={accentVars(c.project.accentColor)}>
      <div className="container">
        <Breadcrumbs
          items={[
            { label: platformName, to: '/' },
            { label: c.project.name, to: `/projects/${c.project.slug}` },
            { label: c.title, to: `/competitions/${c.slug}` },
            { label: 'İşlər' },
          ]}
        />
        <header className="gallery__head">
          <div>
            <h1 className="gallery__title">İşlər</h1>
            <p className="gallery__meta">
              <Link to={`/competitions/${c.slug}`}>{c.title}</Link>. Nəticələr {formatDate(g.publishedAt)} tarixində açıqlanıb.
            </p>
          </div>
          <Link to={`/competitions/${c.slug}/results`} className="btn">
            Nəticələrə bax
          </Link>
        </header>

        {c.hasCategories && g.categories.length > 0 && (
          <GalleryFilter label={label} categories={g.categories} selected={active?.slug ?? null} total={g.total} />
        )}

        <p className="gallery__count" aria-live="polite">
          {active ? `${active.name}: ` : ''}
          <span className="tabular">{items.length}</span> iş
        </p>

        {items.length === 0 ? (
          <div className="empty">
            <h2>Bu seçimdə iş yoxdur</h2>
            <Link to="?" className="btn">
              Bütün işlərə bax
            </Link>
          </div>
        ) : (
          <ul role="list" className="gallery__grid">
            {visible.map((item) => (
              <GalleryCard key={item.reference} item={item} slug={c.slug} />
            ))}
          </ul>
        )}
        {items.length > visible.length && (
          <div className="gallery__more">
            <p className="muted">
              <span className="tabular">{visible.length}</span> / <span className="tabular">{items.length}</span> iş göstərilir
            </p>
            <button type="button" className="btn" onClick={() => setShown({ key: active?.slug ?? null, count: limit + PAGE })}>
              Daha çox göstər
            </button>
          </div>
        )}
      </div>
    </div>
  )
}
