import { Link, useParams } from 'react-router-dom'
import { usePlatform } from '../../platform/context'
import { resultsApi } from '../../api/endpoints'
import type { GalleryFile, GalleryWork, ItemAward, ResultCompetition } from '../../api/types'
import { Breadcrumbs } from '../../components/Breadcrumbs'
import { PageError, PageLoading } from '../../components/PageState'
import { WinnerBadge } from '../../components/WinnerBadge'
import { accentVars } from '../../lib/accent'
import { useApi } from '../../lib/useApi'
import { placeName } from '../../lib/text'
import { useTitle } from '../../lib/useTitle'
import { workName } from './lib'
import { Answers, ResultsLocked, WorkVisual } from './shared'
import './Results.css'

/**
 * One entry of a published result. A winner's page says so first — "{settlement} üzrə qalib", the winner's name,
 * the work and its reference — then shows the artwork whole, as the object of the page. Any other entry shows only
 * its public fields and files, never who made it.
 */
export default function GalleryWorkPage() {
  const { slug = '', reference = '' } = useParams()
  const state = useApi(
    async (signal) => ({
      work: await resultsApi.work(slug, reference, signal),
      gallery: await resultsApi.gallery(slug, null, signal),
    }),
    [slug, reference],
  )
  useTitle(state.data ? winnerTitle(state.data.work) ?? workName(state.data.work) : 'İş')
  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error')
    return state.error.status === 404 ? <ResultsLocked slug={slug} /> : <PageError error={state.error} onRetry={state.retry} />

  const { work: w, gallery } = state.data
  return <WorkView w={w} c={gallery.competition} />
}

/** What a winning work is called on its own page: its settlement's winner, its place ("2-ci yer"), or its podium place. */
function winnerTitle(w: GalleryWork): string | null {
  if (!w.isWinner) return null
  if (w.place) return placeName(w.place)
  // A work in both choices is named by both; its places, with their numbers, follow under the title.
  if (w.awards && w.awards.length > 1) return 'Münsiflərin və xalqın seçimi'
  if (w.awards?.length) return `${awardChoice(w.awards[0])} — ${placeName(w.awards[0].place)}`
  return w.category ? `${w.category.name} üzrə qalib` : null
}

const awardChoice = (a: ItemAward) => (a.category === 'Jury' ? 'Münsiflərin seçimi' : 'Xalqın seçimi')

/** "Münsiflərin seçimi — 2-ci yer · 26 bal": each choice a work stands in, with its own number, never summed. */
const awardLine = (a: ItemAward) => `${awardChoice(a)} — ${placeName(a.place)} · ${a.points} ${a.category === 'Jury' ? 'bal' : 'səs'}`

function WorkView({ w, c }: { w: GalleryWork; c: ResultCompetition }) {
  const { platformName } = usePlatform()
  const name = workName(w)
  const place = w.place ? placeName(w.place) : (w.category?.name ?? null)
  const label = winnerTitle(w) ?? 'Qalib'
  // Every public file is a server-made image: re-encoded uploads and first-page previews of PDFs.
  const [first, ...rest] = w.files
  const alt = (f: GalleryFile, i: number) =>
    f.kind === 'document-preview'
      ? `${name} — sənədin birinci səhifəsi`
      : w.isWinner
        ? `${label} iş — ${name}${i > 0 ? ` (${i + 1})` : ''}`
        : `${name}${i > 0 ? ` (${i + 1})` : ''}`

  return (
    <article className="work" style={accentVars(c.project.accentColor)}>
      <div className="container">
        <Breadcrumbs
          items={[
            { label: platformName, to: '/' },
            { label: c.title, to: `/competitions/${c.slug}` },
            w.isWinner ? { label: 'Nəticələr', to: `/competitions/${c.slug}/results` } : { label: 'İşlər', to: `/competitions/${c.slug}/gallery` },
            { label: w.awards?.length ? label : w.isWinner && place ? place : w.reference },
          ]}
        />

        {w.isWinner ? (
          <header className="work__head work__head--winner">
            <WinnerBadge size="lg" label={w.awards?.length ? placeName(Math.min(...w.awards.map((a) => a.place))) : undefined} />
            <h1 className="work__title">{label}</h1>
            {w.awards && w.awards.length > 0 && (
              <ul className="work__awards" aria-label="Nəticədə tutduğu yerlər">
                {w.awards.map((a) => (
                  <li key={a.category}>{awardLine(a)}</li>
                ))}
              </ul>
            )}
            <dl className="work__facts">
              {w.winnerName && (
                <div>
                  <dt>İştirakçı</dt>
                  <dd className="work__winner">{w.winnerName}</dd>
                </div>
              )}
              <div>
                <dt>İşin adı</dt>
                <dd>{name}</dd>
              </div>
              <div>
                <dt>İstinad nömrəsi</dt>
                <dd className="tabular">{w.reference}</dd>
              </div>
            </dl>
          </header>
        ) : (
          <header className="work__head">
            <h1 className="work__title">{name}</h1>
            <p className="work__meta">
              {w.category && (
                <>
                  <Link to={`/competitions/${c.slug}/gallery?category=${encodeURIComponent(w.category.slug)}`}>{w.category.name}</Link>
                  <span aria-hidden="true"> / </span>
                </>
              )}
              <span className="tabular">{w.reference}</span>
            </p>
          </header>
        )}
      </div>

      <div className="work__stage">
        <div className="container">
          <figure className="work__hero art-frame">
            {first ? (
              <img src={first.url} alt={alt(first, 0)} width={first.width ?? undefined} height={first.height ?? undefined} fetchPriority="high" />
            ) : (
              <WorkVisual item={w} alt={w.isWinner ? `${label} iş — ${name}` : name} size="full" eager />
            )}
          </figure>
          {first?.kind === 'document-preview' && (
            <p className="work__note">Sənədin birinci səhifəsi (önizləmə). Sənədin özü ictimai deyil.</p>
          )}
        </div>
      </div>

      <div className="container work__body">
        {rest.length > 0 && (
          <ul role="list" className="work__more">
            {rest.map((f, i) => (
              <li key={f.id}>
                <figure className="art-frame">
                  <img src={f.url} alt={alt(f, i + 1)} width={f.width ?? undefined} height={f.height ?? undefined} loading="lazy" decoding="async" />
                </figure>
                {f.kind === 'document-preview' && <p className="work__note">Sənədin birinci səhifəsi (önizləmə).</p>}
              </li>
            ))}
          </ul>
        )}

        {w.answers.length > 0 && (
          <section className="work__answers" aria-label="İş haqqında">
            <Answers answers={w.answers} />
          </section>
        )}

        <nav className="work__back" aria-label="Nəticələrə qayıt">
          <Link to={`/competitions/${c.slug}/results`} className="btn">
            Bütün qaliblər
          </Link>
          <Link to={`/competitions/${c.slug}/gallery`} className="btn btn--ghost">
            Bütün işlər
          </Link>
        </nav>
      </div>
    </article>
  )
}
