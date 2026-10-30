import { Link } from 'react-router-dom'
import type { CompetitionResult, PublicAward, PublicAwards } from '../../api/types'
import { Breadcrumbs } from '../../components/Breadcrumbs'
import { accentVars } from '../../lib/accent'
import { formatDate, shortCompetitionTitle } from '../../lib/format'
import { placeName } from '../../lib/text'
import { usePlatform } from '../../platform/context'
import { workName } from './lib'
import { WorkVisual } from './shared'
import './awards.css'

/** The page's subtitle, as the owner worded it (16 October 2026). */
export const AWARDS_LEDE = 'Yaradıcılığın iki fərqli baxışla qiymətləndirildiyi müsabiqənin nəticələri.'

/** The two independent choices, in the order the announcement gives them. */
const SECTIONS = [
  { key: 'jury', anchor: 'munsiflerin-secimi', mark: 'A', title: 'Münsiflərin seçimi', note: 'Münsiflərin səsverməsinin yekun balları', unit: 'bal' },
  { key: 'public', anchor: 'xalqin-secimi', mark: 'B', title: 'Xalqın seçimi', note: 'Audit sonrası etibarlı səslər', unit: 'səs' },
] as const

type Section = (typeof SECTIONS)[number]

/**
 * A result announced as two independent choices — the jury's and the public's — three places each, each with its own
 * number: the jury's points, the public's valid votes. They are shown apart and never added up: no overall winner,
 * no formula, no prize. The same work may stand in both. The first place leads each section, large; the second and
 * third follow, compact. The work is the subject: its picture, whole, comes first; the participant's name follows.
 */
export function AwardsResults({ r, awards }: { r: CompetitionResult; awards: PublicAwards }) {
  const { platformName } = usePlatform()
  const c = r.competition
  const name = shortCompetitionTitle(c.title)
  const shown = SECTIONS.filter((s) => awards[s.key].length > 0)

  return (
    <div className="results results--awards" style={accentVars(c.project.accentColor)}>
      <div className="container">
        <Breadcrumbs
          items={[
            { label: platformName, to: '/' },
            { label: c.project.name, to: `/projects/${c.project.slug}` },
            { label: c.title, to: `/competitions/${c.slug}` },
            { label: 'Nəticələr' },
          ]}
        />
        <header className="aw-head">
          <p className="aw-head__competition">{name}</p>
          <h1 className="aw-head__title">Müsabiqənin nəticələri</h1>
          <p className="aw-head__lede">{AWARDS_LEDE}</p>
          <p className="aw-head__date">
            Açıqlanıb: <time dateTime={r.publishedAt}>{formatDate(r.publishedAt)}</time>
          </p>
          {shown.length > 1 && (
            <nav className="aw-head__jump" aria-label="Bölmələr">
              {shown.map((s) => (
                <a key={s.key} href={`#${s.anchor}`}>
                  <span aria-hidden="true">{s.mark}</span> {s.title}
                </a>
              ))}
            </nav>
          )}
        </header>
      </div>

      {shown.map((s) => (
        <AwardSection key={s.key} section={s} places={awards[s.key]} slug={c.slug} />
      ))}

      <div className="container">
        <section className="results__gallery-link aw-gallery" aria-labelledby="aw-all-title">
          <h2 id="aw-all-title">Bütün işlər</h2>
          <p>
            Müsabiqəyə göndərilmiş <span className="tabular">{r.entryCount}</span> işin hamısına qalereyada baxın.
          </p>
          <Link to={`/competitions/${c.slug}/gallery`} className="btn btn--primary">
            İşlərə bax
          </Link>
        </section>
      </div>
    </div>
  )
}

function AwardSection({ section: s, places, slug }: { section: Section; places: PublicAward[]; slug: string }) {
  const ordered = [...places].sort((a, b) => a.place - b.place)
  return (
    <section id={s.anchor} className={`aw-section aw-section--${s.key}`} aria-labelledby={`${s.anchor}-title`}>
      <div className="container">
        <header className="aw-section__head">
          <span className="aw-section__mark" aria-hidden="true">
            {s.mark}
          </span>
          <h2 id={`${s.anchor}-title`} className="aw-section__title">
            {s.title}
          </h2>
          <p className="aw-section__note">{s.note}</p>
        </header>
        <ol role="list" className="aw-podium">
          {ordered.map((a) => (
            <AwardPlace key={`${a.place}-${a.work.reference}`} award={a} section={s} slug={slug} />
          ))}
        </ol>
      </div>
    </section>
  )
}

function AwardPlace({ award: a, section: s, slug }: { award: PublicAward; section: Section; slug: string }) {
  const title = workName(a.work)
  const rank = placeName(a.place)
  const first = a.place === 1
  return (
    <li className={`aw-place aw-place--${a.place}`}>
      <article className="aw-place__card" aria-labelledby={`${s.anchor}-${a.place}`}>
        <figure className="aw-place__visual art-frame">
          <WorkVisual item={a.work} alt={`${s.title}, ${rank}: ${title} — ${a.participantName}`} size={first ? 'full' : 'thumb'} eager={first} />
        </figure>
        <div className="aw-place__text">
          <p className="aw-place__rank">
            <span className="aw-medal" aria-hidden="true">
              {a.place}
            </span>
            {rank}
          </p>
          <h3 id={`${s.anchor}-${a.place}`} className="aw-place__title">
            {title}
          </h3>
          <p className="aw-place__name">{a.participantName}</p>
          <p className="aw-place__points tabular">
            <strong>{a.points}</strong> {s.unit}
          </p>
          <Link
            to={`/competitions/${slug}/gallery/${a.work.reference}`}
            className="aw-place__link"
            aria-label={`${s.title}, ${rank}: ${title} — işə bax`}
          >
            İşə bax <span aria-hidden="true">→</span>
          </Link>
        </div>
      </article>
    </li>
  )
}
