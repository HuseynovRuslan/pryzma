import { Link } from 'react-router-dom'
import type { CompetitionResult, PublicAward, PublicAwards } from '../api/types'
import { accentVars } from '../lib/accent'
import { shortCompetitionTitle } from '../lib/format'
import { placeName } from '../lib/text'
import { shortReference } from './homeWinnersData'
import { workName } from './results/lib'
import { WorkVisual } from './results/shared'
import './HomeWinners.css'

/** The two independent choices of a published result, in the order the announcement gives them (D18). */
const CHOICES = [
  { key: 'jury', mark: 'A', title: 'Münsiflərin seçimi', unit: 'bal' },
  { key: 'public', mark: 'B', title: 'Xalqın seçimi', unit: 'səs' },
] as const

type Choice = (typeof CHOICES)[number]

/** The winners on the home page itself, so nobody has to find /results to learn who won. */
/**
 * While another competition is being run: the winners as one line under the lead, so the open calls come first
 * (the owner, 20 October 2026). The full section returns when nothing is open.
 */
export function HomeWinnersStrip({ result: r }: { result: CompetitionResult }) {
  if (!r.awards || (r.awards.jury.length === 0 && r.awards.public.length === 0)) return null
  const c = r.competition
  return (
    <section className="container home-winners-strip" aria-labelledby="home-winners-strip-title" style={accentVars(c.project.accentColor)}>
      <p id="home-winners-strip-title" className="home-winners-strip__text">
        <strong>{shortCompetitionTitle(c.title)}</strong>: qaliblər elan olundu
      </p>
      <Link to={`/competitions/${c.slug}/results`} className="home-winners-strip__link">
        Nəticələrə bax <span aria-hidden="true">→</span>
      </Link>
    </section>
  )
}

export function HomeWinners({ result: r }: { result: CompetitionResult }) {
  if (!r.awards) return null
  return <Winners r={r} awards={r.awards} />
}

function Winners({ r, awards }: { r: CompetitionResult; awards: PublicAwards }) {
  const shown = CHOICES.filter((ch) => awards[ch.key].length > 0)
  if (shown.length === 0) return null
  const c = r.competition
  return (
    <section className="container home-winners" aria-labelledby="home-winners-title" style={accentVars(c.project.accentColor)}>
      <header className="home-winners__head">
        <p className="home-winners__competition">{shortCompetitionTitle(c.title)}</p>
        <h2 id="home-winners-title" className="home-winners__title">
          Qaliblər elan olundu!
        </h2>
        <Link to={`/competitions/${c.slug}/results`} className="home-winners__all">
          Bütün nəticələr <span aria-hidden="true">→</span>
        </Link>
      </header>
      <div className="home-winners__choices">
        {shown.map((ch) => (
          <ChoiceColumn key={ch.key} choice={ch} places={awards[ch.key]} slug={c.slug} />
        ))}
      </div>
    </section>
  )
}

function ChoiceColumn({ choice: ch, places, slug }: { choice: Choice; places: PublicAward[]; slug: string }) {
  const ordered = [...places].sort((a, b) => a.place - b.place)
  return (
    <section className={`hw-choice hw-choice--${ch.key}`} aria-labelledby={`hw-${ch.key}-title`}>
      <h3 id={`hw-${ch.key}-title`} className="hw-choice__title">
        <span className="hw-choice__mark" aria-hidden="true">
          {ch.mark}
        </span>
        {ch.title}
      </h3>
      <ol role="list" className="hw-places">
        {ordered.map((a) => (
          <WinnerPlace key={`${a.place}-${a.work.reference}`} award={a} choice={ch} slug={slug} />
        ))}
      </ol>
    </section>
  )
}

/** One place: the whole card is one link to the work, so a keyboard stops once per place. */
function WinnerPlace({ award: a, choice: ch, slug }: { award: PublicAward; choice: Choice; slug: string }) {
  const title = workName(a.work)
  const first = a.place === 1
  return (
    <li className={`hw-place hw-place--${a.place}`}>
      <Link
        to={`/competitions/${slug}/gallery/${a.work.reference}`}
        className="hw-place__link"
        aria-label={`${ch.title}, ${placeName(a.place)}: ${a.participantName} — ${a.points} ${ch.unit}, ${title}`}
      >
        <figure className="hw-place__visual">
          <WorkVisual item={a.work} alt="" size={first ? 'full' : 'thumb'} />
        </figure>
        <span className="hw-place__text">
          <span className="hw-place__rank">
            <span className={`hw-medal hw-medal--${a.place}`} aria-hidden="true">
              {a.place}
            </span>
            {placeName(a.place)}
          </span>
          <strong className="hw-place__name">{a.participantName}</strong>
          <span className="hw-place__meta tabular">
            <span className="hw-place__points">
              <b>{a.points}</b> {ch.unit}
            </span>
            <span className="hw-place__ref" title={a.work.reference}>
              {shortReference(a.work.reference)}
            </span>
          </span>
          {first && <span className="hw-place__work">{title}</span>}
        </span>
      </Link>
    </li>
  )
}
