import { useState } from 'react'
import { Link } from 'react-router-dom'
import type { GalleryItem, GalleryWork, PublicFinalPlace } from '../../api/types'
import { Buta } from '../../components/Motifs'
import { WinnerBadge } from '../../components/WinnerBadge'
import { azOrdinal, placeName } from '../../lib/text'
import { workName } from './lib'

/**
 * A work's public-safe picture, always shown whole (contained, never cropped — emblems and badges must keep their
 * edges). `size="thumb"` uses the card-size copy. When there is no picture the frame says why, quietly: still being
 * made, could not be made, or not published.
 */
export function WorkVisual({ item, alt, eager = false, size = 'thumb' }: {
  item: Pick<GalleryItem, 'preview' | 'previewState'>
  alt: string
  eager?: boolean
  size?: 'thumb' | 'full'
}) {
  // A file that cannot be decoded shows the "unavailable" frame instead of a broken-image icon.
  const [broken, setBroken] = useState(false)
  const p = item.preview
  if (p && item.previewState === 'Ready' && !broken)
    return (
      <img
        src={size === 'thumb' ? p.thumbUrl : p.url}
        alt={alt}
        width={p.width ?? undefined}
        height={p.height ?? undefined}
        loading={eager ? 'eager' : 'lazy'}
        decoding="async"
        onError={() => setBroken(true)}
      />
    )
  const state = broken ? 'Unavailable' : item.previewState
  const text =
    state === 'Processing' ? 'Önizləmə hazırlanır' : state === 'Unavailable' ? 'Önizləmə yoxdur' : 'Şəkil dərc olunmayıb'
  return (
    <span className={`art-state art-state--${state.toLowerCase()}`} role={alt ? 'img' : undefined} aria-label={alt ? `${alt}: ${text}` : undefined}>
      {state !== 'Processing' && <Buta className="art-state__buta" eye={false} />}
      <span className={state === 'None' ? 'visually-hidden' : 'art-state__text'}>{text}</span>
    </span>
  )
}

/** A gallery card: every card the same size; a winner is marked by its badge, a heavier frame and its winner's name. */
export function GalleryCard({ item, slug }: { item: GalleryItem; slug: string }) {
  const name = workName(item)
  return (
    <li className={`gcard${item.isWinner ? ' gcard--winner' : ''}`}>
      <Link to={`/competitions/${slug}/gallery/${item.reference}`} className="gcard__link">
        <span className="gcard__visual art-frame">
          <WorkVisual item={item} alt="" />
          {item.isWinner && (
            <span className="gcard__badge">
              <WinnerBadge size="sm" label={item.place ? placeName(item.place) : item.awards?.length ? placeName(item.awards[0].place) : undefined} />
            </span>
          )}
        </span>
        <span className="gcard__text">
          <span className="gcard__title">{name}</span>
          {item.category && <span className="gcard__category">{item.category.name}</span>}
          {item.isWinner && item.winnerName && <span className="gcard__winner">{item.winnerName}</span>}
        </span>
      </Link>
    </li>
  )
}

/**
 * A category's winner on the results page: the work, whole, on its own paper; the settlement as the headline (that is
 * what people look for); then the work, the winner's name and the reference. The whole card is one link.
 *
 * A placed work of a competition without categories says its place instead ("2-ci yer"), on the badge and as the
 * headline.
 */
export function WinnerCard({
  item,
  slug,
  place,
  final,
}: {
  item: GalleryItem
  slug: string
  place: string | null
  /** The jury final's numbers for this place, when the result came from one (D13). */
  final?: { score: PublicFinalPlace; juryMax: number }
}) {
  const name = workName(item)
  const rank = item.place ? placeName(item.place) : null
  const headline = place ?? rank
  const label = place ? `${place} üzrə qalib iş` : rank ? `${rank} — iş` : 'Qalib iş'
  return (
    <li className={`wcard${item.place ? ` wcard--place-${item.place}` : ''}`}>
      <span className="wcard__visual art-frame">
        <WorkVisual item={item} alt={`${label} — ${name}`} />
      </span>
      <span className="wcard__text">
        <WinnerBadge size="sm" label={rank ?? undefined} />
        {headline && <span className="wcard__place">{headline}</span>}
        <span className="wcard__title">{name}</span>
        {item.winnerName && <span className="wcard__name">{item.winnerName}</span>}
        <span className="wcard__ref tabular">{item.reference}</span>
        {final && <FinalScore score={final.score} juryMax={final.juryMax} />}
        <Link to={`/competitions/${slug}/gallery/${item.reference}`} className="wcard__link" aria-label={`${label}: ${name} — bax`}>
          {item.place && item.place > 1 ? 'İşə bax' : 'Qalib işə bax'}
        </Link>
      </span>
    </li>
  )
}

/**
 * How a place was reached in the jury final (D13), in the words of the announcement: the public's valid votes, rank
 * and points, the jury's combined score, rank and points, and the sum. No juror is named.
 */
function FinalScore({ score, juryMax }: { score: PublicFinalPlace; juryMax: number }) {
  const n = (x: number) => String(x).replace('.', ',')
  return (
    <dl className="wcard__final" aria-label="Yekun xalın izahı">
      <div>
        <dt>Xalq</dt>
        <dd className="tabular">
          {score.validVotes} etibarlı səs · {azOrdinal(score.publicRank)} yer · <strong>{score.publicPoints} xal</strong>
        </dd>
      </div>
      <div>
        <dt>Münsiflər</dt>
        <dd className="tabular">
          {n(score.juryTotal)} / {n(juryMax)} · {azOrdinal(score.juryRank)} yer · <strong>{score.juryPoints} xal</strong>
        </dd>
      </div>
      <div className="wcard__final-total">
        <dt>Yekun</dt>
        <dd className="tabular">
          <strong>{score.finalScore} xal</strong>
        </dd>
      </div>
    </dl>
  )
}

/** Public answers of a work (only fields an admin marked "public after results"). */
export function Answers({ answers }: { answers: GalleryWork['answers'] }) {
  return (
    <dl className="work-answers">
      {answers.map((a) => (
        <div key={a.label}>
          <dt>{a.label}</dt>
          <dd>
            {a.fieldType === 'Url' && /^https?:\/\//i.test(a.value) ? (
              <a href={a.value} rel="noreferrer nofollow" target="_blank">
                {a.value}
              </a>
            ) : (
              a.value.split(/\n{2,}/).map((p, i) => <p key={i}>{p}</p>)
            )}
          </dd>
        </div>
      ))}
    </dl>
  )
}

/** What results, gallery and work pages show while the result is private (the API answers 404). */
export function ResultsLocked({ slug }: { slug: string }) {
  return (
    <div className="container page-state results-locked">
      <h1>Nəticələr hələ açıqlanmayıb</h1>
      <p className="lede">Qaliblər və müsabiqəyə göndərilmiş işlər nəticələr dərc olunandan sonra burada görünəcək.</p>
      <Link to={`/competitions/${slug}`} className="btn btn--primary">
        Müsabiqəyə qayıt
      </Link>
    </div>
  )
}
