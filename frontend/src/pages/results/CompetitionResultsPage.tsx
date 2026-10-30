import { useId, useMemo, useState } from 'react'
import { usePlatform } from '../../platform/context'
import { Link, useParams } from 'react-router-dom'
import { resultsApi } from '../../api/endpoints'
import type { CompetitionResult, ResultCategory } from '../../api/types'
import { Breadcrumbs } from '../../components/Breadcrumbs'
import { PageError, PageLoading } from '../../components/PageState'
import { accentVars } from '../../lib/accent'
import { formatDate } from '../../lib/format'
import { matchesSearch, singularAz } from '../../lib/text'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { AwardsResults } from './AwardsResults'
import { ResultsLocked, WinnerCard } from './shared'
import './Results.css'

export default function CompetitionResultsPage() {
  const { slug = '' } = useParams()
  const state = useApi((signal) => resultsApi.result(slug, signal), [slug])
  useTitle(state.data ? `Nəticələr — ${state.data.competition.title}` : 'Nəticələr')
  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error')
    return state.error.status === 404 ? <ResultsLocked slug={slug} /> : <PageError error={state.error} onRetry={state.retry} />
  const awards = state.data.awards
  // A result announced as two independent choices has its own page; every other result keeps the classic one.
  if (awards && awards.jury.length + awards.public.length > 0) return <AwardsResults r={state.data} awards={awards} />
  return <ResultView r={state.data} />
}

function ResultView({ r }: { r: CompetitionResult }) {
  const { platformName } = usePlatform()
  const c = r.competition
  const label = c.categoryLabel ?? 'Kateqoriyalar'
  const categorised = r.categories.length > 0

  return (
    <div className="results" style={accentVars(c.project.accentColor)}>
      <div className="container">
        <Breadcrumbs
          items={[
            { label: platformName, to: '/' },
            { label: c.project.name, to: `/projects/${c.project.slug}` },
            { label: c.title, to: `/competitions/${c.slug}` },
            { label: 'Nəticələr' },
          ]}
        />
        <header className="results__head">
          <p className="results__project">
            <span className="results__swatch" aria-hidden="true" />
            {c.project.name}
          </p>
          <h1 className="results__title">Nəticələr</h1>
          <p className="results__competition">
            <Link to={`/competitions/${c.slug}`}>{c.title}</Link>
          </p>
          <dl className="results__facts">
            {categorised && (
              <div>
                <dt>{singularAz(label).toLocaleLowerCase('az')}</dt>
                <dd className="tabular">{r.categories.length}</dd>
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
            <div>
              <dt>açıqlanıb</dt>
              <dd>
                <time dateTime={r.publishedAt}>{formatDate(r.publishedAt)}</time>
              </dd>
            </div>
          </dl>
        </header>
      </div>

      {r.coverImage && (
        <figure className="results__cover">
          <img src={r.coverImage} alt={`${c.title} — nəticələr`} fetchPriority="high" />
        </figure>
      )}

      <div className="container results__body">
        {r.announcement && (
          <div className="prose results__announcement">
            {r.announcement.split(/\n{2,}/).map((p, i) => (
              <p key={i}>{p}</p>
            ))}
          </div>
        )}

        {categorised ? (
          <CategoryWinners r={r} label={label} />
        ) : r.places.length > 1 ? (
          <section aria-labelledby="winner-title">
            <h2 id="winner-title" className="results__section-title">
              Qaliblər
            </h2>
            <ol role="list" className="wgrid wgrid--podium">
              {r.places.map((item) => (
                <WinnerCard key={item.reference} item={item} slug={c.slug} place={null} final={finalOf(r, item.reference)} />
              ))}
            </ol>
          </section>
        ) : (
          r.overallWinner && (
            <section aria-labelledby="winner-title">
              <h2 id="winner-title" className="results__section-title">
                Qalib
              </h2>
              <ul role="list" className="wgrid wgrid--single">
                <WinnerCard item={r.overallWinner} slug={c.slug} place={null} final={finalOf(r, r.overallWinner.reference)} />
              </ul>
            </section>
          )
        )}

        <section className="results__gallery-link" aria-labelledby="all-title">
          <h2 id="all-title">Bütün işlər</h2>
          <p>
            Müsabiqəyə göndərilmiş <span className="tabular">{r.entryCount}</span> işin hamısına qalereyada baxın
            {categorised ? ` — ${label.toLocaleLowerCase('az')} üzrə süzmək olar` : ''}.
          </p>
          <Link to={`/competitions/${c.slug}/gallery`} className="btn btn--primary">
            İşlərə bax
          </Link>
        </section>
      </div>
    </div>
  )
}

/** The final's numbers for a placed work, when the places came from a jury final. */
const finalOf = (r: CompetitionResult, reference: string) => {
  const score = r.final?.places.find((p) => p.reference === reference)
  return score && r.final ? { score, juryMax: r.final.juryMax } : undefined
}

type Show = 'all' | 'won' | 'open'

/**
 * The winners of every category (settlement), searchable (mastaga → Maştağa) and filterable — never a 59-item
 * dropdown. Categories without a winner are listed compactly and neutrally, not as empty cards.
 */
function CategoryWinners({ r, label }: { r: CompetitionResult; label: string }) {
  const id = useId()
  const [query, setQuery] = useState('')
  const [show, setShow] = useState<Show>('all')
  const one = singularAz(label)

  const matches = useMemo(() => r.categories.filter((k) => matchesSearch(k.name, query)), [r.categories, query])
  const won = matches.filter((k) => k.winner)
  const open = matches.filter((k) => !k.winner)
  const total = { all: r.categories.length, won: r.categories.filter((k) => k.winner).length }
  const options: [Show, string, number][] = [
    ['all', 'Hamısı', total.all],
    ['won', 'Qalibi olanlar', total.won],
    ['open', 'Qalibi olmayanlar', total.all - total.won],
  ]
  const shown = (show === 'open' ? 0 : won.length) + (show === 'won' ? 0 : open.length)

  return (
    <section className="rwinners" aria-labelledby={`${id}-title`}>
      <h2 id={`${id}-title`} className="results__section-title">
        {label} üzrə qaliblər
      </h2>

      <div className="rwinners__tools">
        <div className="rwinners__search">
          <label htmlFor={`${id}-q`} className="rwinners__label">
            {one} axtar
          </label>
          <input
            id={`${id}-q`}
            type="search"
            className="input"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="məs. balaxani"
            autoComplete="off"
            spellCheck={false}
            enterKeyHint="search"
          />
        </div>
        <fieldset className="seg">
          <legend className="visually-hidden">Göstər</legend>
          {options.map(([value, text, count]) => (
            <label key={value} className="seg__option">
              <input type="radio" name={`${id}-show`} value={value} checked={show === value} onChange={() => setShow(value)} />
              <span>
                {text} <span className="seg__n tabular">{count}</span>
              </span>
            </label>
          ))}
        </fieldset>
      </div>
      <p className="rwinners__count" aria-live="polite">
        {query ? `“${query}”: ` : ''}
        <span className="tabular">{shown}</span> {one.toLocaleLowerCase('az')} göstərilir
      </p>

      {shown === 0 && (
        <p className="rwinners__none">
          Axtarışa uyğun {one.toLocaleLowerCase('az')} yoxdur.{' '}
          <button type="button" className="btn btn--sm btn--ghost" onClick={() => { setQuery(''); setShow('all') }}>
            Axtarışı təmizlə
          </button>
        </p>
      )}

      {show !== 'open' && won.length > 0 && (
        <ul role="list" className="wgrid">
          {won.map((k) => (
            <WinnerCard key={k.slug} item={k.winner!} slug={r.competition.slug} place={k.name} />
          ))}
        </ul>
      )}

      {show !== 'won' && open.length > 0 && <OpenCategories categories={open} slug={r.competition.slug} />}
    </section>
  )
}

/** Categories still without a winner: a compact, neutral list — how many works each received, linked to them. */
function OpenCategories({ categories, slug }: { categories: ResultCategory[]; slug: string }) {
  return (
    <div className="ropen">
      <h3 className="ropen__title">
        Qalib hələ müəyyən edilməyib <span className="ropen__n tabular">{categories.length}</span>
      </h3>
      <ul role="list" className="ropen__list">
        {categories.map((k) => (
          <li key={k.slug}>
            <span className="ropen__name">{k.name}</span>
            {k.entryCount > 0 ? (
              <Link
                to={`/competitions/${slug}/gallery?category=${encodeURIComponent(k.slug)}`}
                className="ropen__n"
                aria-label={`${k.name}: ${k.entryCount} iş`}
              >
                <span className="tabular">{k.entryCount}</span> iş
              </Link>
            ) : (
              <span className="ropen__n ropen__n--none">iş yoxdur</span>
            )}
          </li>
        ))}
      </ul>
    </div>
  )
}
