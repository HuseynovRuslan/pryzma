import { useId, useLayoutEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import type { VotingLeaderboardEntry } from '../../api/types'
import { useNow } from '../../lib/competitionLifecycle'
import { ordinal, shortCompetitionTitle } from '../../lib/format'
import { azOrdinal } from '../../lib/text'
import { barShare, COMPACT, gapLine, type LiveRanking } from '../../pages/voting/leaderboard'
import { ONE_VOTE, workNumber } from '../../pages/voting/lib'
import './leaderboard.css'

/** The reorder slide; the Figma's 320 ms. */
const SLIDE_MS = 320

/** The owner's wording (D12): the ranking is provisional while votes are reviewed. */
export const PROVISIONAL =
  'Canlı reytinq ilkin nəticələri göstərir. Şübhəli səslər yoxlanılır və yekun nəticəyə daxil edilməyə bilər.'

/**
 * Rows the opened ranking adds at a time. The whole of a hundred-work ranking would be some 6 000px on a phone,
 * between the reader and the works; a few dozen rows at a time keep the gallery a scroll away.
 */
export const STEP = 25

/**
 * "Canlı reytinq": where the public vote stands, between the competition's header and its works — the top three,
 * how far apart they are, and how they moved while this page was open. After the Figma (states A–H, 390 and 1440).
 *
 * It ranks votes and nothing else. The places an admin awards are a separate decision, and the final state says
 * so in as many words. Nobody votes here: a row opens the work, and the one button leads to the works below.
 *
 * Every change is quiet: a count that rises slides into place, a row that climbs moves up, a warm tint fades — and
 * with reduced motion all of it simply changes. Screen readers hear only a change in the top three, never the
 * ten-second refresh itself.
 */
export function LiveLeaderboard({
  live,
  slug,
  galleryHref,
  votedReference = null,
  competitionTitle,
}: {
  live: LiveRanking
  slug: string
  /** The works below, for "İşlərə bax və səsini ver". */
  galleryHref: string
  /** This viewer's vote as the page knows it, until the next read confirms it. */
  votedReference?: string | null
  /**
   * Whose ranking this is. Each competition has its own vote and its own ranking — never one table across them —
   * and with two votes open at once the reader has to see which is which.
   */
  competitionTitle?: string
}) {
  const id = useId()
  const [shown, setShown] = useState(COMPACT)
  const open = shown > COMPACT
  const section = useRef<HTMLElement>(null)
  const list = useRef<HTMLOListElement>(null)
  const tops = useRef(new Map<string, number>())
  const wasShown = useRef(shown)

  // FLIP: each row is drawn at its new place, then slid from where it stood. Only after a read — opening or folding
  // the list changes places too, and nothing has moved there.
  useLayoutEffect(() => {
    const rows = list.current?.querySelectorAll<HTMLElement>('[data-ref]')
    if (!rows) return
    const slide =
      wasShown.current === shown && !window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
    const next = new Map<string, number>()
    for (const row of rows) {
      const ref = row.dataset.ref!
      next.set(ref, row.offsetTop)
      const was = tops.current.get(ref)
      if (slide && was !== undefined && was !== row.offsetTop && typeof row.animate === 'function') {
        // A climbing row passes over the one it overtakes, not under it.
        if (was > row.offsetTop) row.style.zIndex = '1'
        row.animate([{ transform: `translateY(${was - row.offsetTop}px)` }, { transform: 'none' }], {
          duration: SLIDE_MS,
          easing: 'cubic-bezier(0.2, 0.7, 0.1, 1)',
        }).onfinish = () => (row.style.zIndex = '')
      }
    }
    tops.current = next
    wasShown.current = shown
  }, [live.seq, shown])

  const board = live.board
  if (!board || board.entries.length === 0) return null

  const isLive = board.state === 'Open'
  const mine = board.myVoteReference ?? votedReference
  const leader = board.entries[0].voteCount
  const visible = board.entries.slice(0, shown)
  const own = mine ? board.entries.find((e) => e.reference === mine) : undefined
  const ownBelow = own !== undefined && !visible.includes(own)
  const rows = ownBelow ? [...visible, own] : visible
  const rest = board.entries.length - visible.length
  const gap = isLive ? gapLine(board.entries, mine) : null

  const fold = () => {
    setShown(COMPACT)
    // The folded list is short; bring its top back rather than leave the reader in the gallery.
    const box = section.current?.getBoundingClientRect()
    if (box && box.top < 0) section.current?.scrollIntoView({ block: 'start' })
  }

  return (
    <section
      ref={section}
      className={`lb ${isLive ? 'is-live' : 'is-final'}`}
      aria-labelledby={`${id}-title`}
    >
      <div className="lb__grid">
        <header className="lb__head">
          <div className="lb__titlebar">
            <h2 id={`${id}-title`} className="lb__title">
              {isLive ? 'Canlı reytinq' : 'İlkin səs sıralaması'}
            </h2>
            {isLive && (
              <span className="lb__live">
                <span className="lb__dot" aria-hidden="true" />
                Canlı
              </span>
            )}
          </div>
          <p className="lb__lede">
            {isLive ? 'Səslər gəldikcə sıralama yenilənir' : 'Bu sıralama audit davam etdiyi üçün dəyişə bilər.'}
          </p>
          {competitionTitle ? (
            <p className="lb__scope lb__scope--named">«{shortCompetitionTitle(competitionTitle)}» müsabiqəsi üzrə sıralama</p>
          ) : (
            <p className="lb__scope">Bu müsabiqənin işləri üzrə sıralama</p>
          )}
          {isLive && live.climb && <p className="lb__climb">{live.climb}</p>}
          {isLive && live.readAt !== null && <Freshness readAt={live.readAt} />}
        </header>

        <div className="lb__body">
          <ol ref={list} id={`${id}-list`} className="lb__list" role="list">
            {rows.map((e, i) => (
              <Row
                key={e.reference}
                entry={e}
                slug={slug}
                share={barShare(e.voteCount, leader)}
                live={isLive}
                move={live.movement[e.reference] ?? 0}
                gained={live.gained[e.reference] ?? 0}
                seq={live.seq}
                mine={e.reference === mine}
                // The viewer's own work, shown under the rows it is not among.
                detached={ownBelow && i === rows.length - 1}
              />
            ))}
          </ol>

          {gap && <p className="lb__gap">{gap}</p>}

          {board.entries.length > COMPACT && (
            <div className="lb__more-row">
              {open && rest > 0 && (
                <button
                  type="button"
                  className="lb__more"
                  aria-controls={`${id}-list`}
                  onClick={() => setShown((n) => n + STEP)}
                >
                  Daha {Math.min(STEP, rest)} iş göstər <span aria-hidden="true">↓</span>
                </button>
              )}
              <button
                type="button"
                className="lb__more"
                aria-expanded={open}
                aria-controls={`${id}-list`}
                onClick={() => (open ? fold() : setShown(STEP))}
              >
                {open ? 'Reytinqi yığ' : 'Bütün reytinqə bax'}{' '}
                <span aria-hidden="true">{open ? '↑' : '↓'}</span>
              </button>
            </div>
          )}

          {/* D12: the ranking counts valid votes, and a vote can still be found fraudulent and set aside. Said
              quietly, under the standings, in both states. */}
          {isLive && <p className="lb__provisional">{PROVISIONAL}</p>}

          {!isLive ? (
            // The owner's decision of 7 October 2026: after the vote the ranking stays, as a provisional one. Under
            // it, the owner's own words (9 October 2026; D16 — nothing here of the jury).
            <p className="lb__rule">
              Göstərilən sıralama etibarlı səslərə əsaslanan ilkin nəticədir. Yekun nəticələr ayrıca elan ediləcək.
            </p>
          ) : mine ? (
            <>
              <p className="lb__rule">Səsiniz qeydə alınıb. Bütün sıralama açıq qalır.</p>
              <p className="lb__fine">Müsabiqə üzrə bir səs hüququnuz istifadə olunub.</p>
            </>
          ) : (
            <>
              <p className="lb__rule">{ONE_VOTE}</p>
              <a className="lb__cta" href={galleryHref}>
                İşlərə bax və səsini ver
              </a>
            </>
          )}
        </div>
      </div>
      <p className="visually-hidden" role="status">
        {isLive ? live.said : ''}
      </p>
    </section>
  )
}

function Row({
  entry: e,
  slug,
  share,
  live,
  move,
  gained,
  seq,
  mine,
  detached,
}: {
  entry: VotingLeaderboardEntry
  slug: string
  share: number
  live: boolean
  move: number
  gained: number
  seq: number
  mine: boolean
  detached: boolean
}) {
  const classes = ['lb__row']
  if (e.rank === 1) classes.push('is-lead')
  if (mine) classes.push('is-mine')
  if (gained > 0) classes.push('is-gained')
  if (detached) classes.push('is-detached')

  return (
    <li data-ref={e.reference} className={classes.join(' ')}>
      <Link to={`/competitions/${slug}/voting/${e.reference}`} className="lb__link">
        <span className="lb__rank tabular" aria-hidden="true">
          {ordinal(e.rank)}
        </span>
        <span className="visually-hidden">{azOrdinal(e.rank)} yer: </span>
        <span className="lb__thumb">
          {e.preview && (
            <img src={e.preview.thumbUrl} alt="" width={40} height={40} loading="lazy" decoding="async" />
          )}
        </span>
        <span className="lb__work">
          <span className="lb__name">{workNumber(e.reference)}</span>
          <span className="lb__bar" aria-hidden="true">
            <span className="lb__fill" style={{ width: `${share * 100}%` }} />
          </span>
        </span>
        <span className="lb__side">
          {/* Keyed by the read, so the short rise plays again on each new vote and never on a plain refresh. */}
          <span key={gained > 0 ? seq : 'still'} className={`lb__votes tabular${gained > 0 ? ' is-rising' : ''}`}>
            {e.voteCount} səs
          </span>
          {live && <Move steps={move} />}
        </span>
        {(gained > 0 || mine) && (
          <span className="lb__notes">
            {gained > 0 && (
              <span key={seq} className="lb__gained">
                +{gained} yeni səs
              </span>
            )}
            {mine && <span className="lb__mine">✓ Sizin səs verdiyiniz iş</span>}
          </span>
        )}
      </Link>
    </li>
  )
}

/** "↑ 2", "↓ 1", or a dash: the latest reorder this page has seen, in words for a screen reader. */
function Move({ steps }: { steps: number }) {
  if (steps === 0)
    return (
      <span className="lb__move" aria-hidden="true">
        —
      </span>
    )
  const up = steps > 0
  return (
    <span className={`lb__move ${up ? 'is-up' : 'is-down'}`}>
      <span aria-hidden="true">
        {up ? '↑' : '↓'} {Math.abs(steps)}
      </span>
      <span className="visually-hidden">
        {up ? `${steps} pillə yüksəlib` : `${-steps} pillə enib`}
      </span>
    </span>
  )
}

/** "Son yenilənmə: indi" — ticking on its own, so the list does not redraw every few seconds for it. */
function Freshness({ readAt }: { readAt: number }) {
  const now = useNow(5_000)
  const seconds = Math.max(0, Math.floor((now - readAt) / 1000))
  const ago = seconds < 15 ? 'indi' : seconds < 60 ? `${seconds} saniyə əvvəl` : `${Math.floor(seconds / 60)} dəqiqə əvvəl`
  return <p className="lb__fresh">Son yenilənmə: {ago}</p>
}
