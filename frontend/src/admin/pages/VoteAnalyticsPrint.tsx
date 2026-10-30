import { useMemo } from 'react'
import { submissionsApi } from '../../api/endpoints'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDateTime } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { adminApi } from '../api'
import { chunk } from '../print'
import { shrinkForPrint, usePictureCache, type Picture } from '../printPictures'
import { catalogNumber } from '../../pages/voting/lib'
import { breakdownText, orderWorks, voteCatalogApi, type VoteCatalog, type VoteCatalogWork } from '../voteCatalogApi'
import './vote-analytics.css'

/** Works per A4 page, as the jury's catalog. */
const PER_PAGE = 9

/** A card's picture is about 60 mm wide on paper: 360 px is enough, and keeps the 99-work PDF light. */
const PRINT_PX = 360

/**
 * The catalog of every work with its public vote — for the administration only, never for the jury: each work's public
 * position, short number ("İş №90"), valid votes and a bar of its votes (green: valid; red: set aside by an approved audit rule —
 * D12 phone cluster, R2, D14), with the exact counts. Most valid votes first, or back in submission-number order; on
 * request only the works that lost votes to an audit decision. Nine to an A4 page, printed from the browser as the
 * other documents are. All numbers come from the server (voting/catalog); nothing here estimates a risk or labels a
 * person. "Pending review" is not shown: no such state is recorded.
 */
export function VoteAnalyticsPrint({
  competitionId,
  order,
  onlyInvalidated,
  set,
}: {
  competitionId: number
  order: 'votes' | 'reference'
  onlyInvalidated: boolean
  set: (patch: Record<string, string | null>) => void
}) {
  const state = useApi((signal) => voteCatalogApi.catalog(competitionId, signal), [competitionId])
  const works = useMemo(() => (state.data ? orderWorks(state.data.works, order, onlyInvalidated) : undefined), [state.data, order, onlyInvalidated])
  const jobs = useMemo(() => works?.filter((w) => w.image), [works])
  const picture = usePictureCache(
    jobs,
    (w) => `${w.submissionId}:${w.image!.fileId}:${w.image!.page ?? 1}`,
    async (w, signal) =>
      shrinkForPrint(
        await ((w.image!.page ?? 1) > 1
          ? adminApi.documentPageBlob(w.submissionId, w.image!.fileId, w.image!.page!, signal, true)
          : submissionsApi.thumbBlob(w.submissionId, w.image!.fileId, signal)),
        PRINT_PX,
      ),
  )

  if (state.status === 'loading') return <PageLoading label="Səs analitikası hazırlanır" />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  const c = state.data
  const shown = works ?? []
  const pages = chunk(shown, PER_PAGE)
  const max = Math.max(1, ...c.works.map((w) => w.validVotes + w.invalidatedVotes))
  const withImage = shown.filter((w) => w.image)
  const settled = withImage.filter((w) => picture(`${w.submissionId}:${w.image!.fileId}:${w.image!.page ?? 1}`) !== undefined).length
  const pic = (w: VoteCatalogWork) => (w.image ? picture(`${w.submissionId}:${w.image.fileId}:${w.image.page ?? 1}`) : undefined)

  return (
    <>
      <div className="va-controls pr-bar__row pr-bar__row--options">
        <fieldset className="pr-layout">
          <legend className="pr-layout__legend">Sıra</legend>
          <label className="pr-radio">
            <input type="radio" name="order" checked={order === 'votes'} onChange={() => set({ order: null })} />
            Etibarlı səslərə görə
          </label>
          <label className="pr-radio">
            <input type="radio" name="order" checked={order === 'reference'} onChange={() => set({ order: 'reference' })} />
            Nömrəyə görə
          </label>
        </fieldset>
        <label className="pr-check">
          <input type="checkbox" checked={onlyInvalidated} onChange={(e) => set({ only: e.target.checked ? 'invalidated' : null })} />
          Yalnız etibarsız sayılmış səsi olanlar ({c.worksWithInvalidations})
        </label>
      </div>
      <p className="pr-bar__status jp-status va-status" role="status">
        {shown.length} iş · {pages.length} səhifə (A4) ·{' '}
        {settled === withImage.length ? 'Bütün şəkillər yükləndi.' : `Şəkillər yüklənir: ${settled} / ${withImage.length}`}
      </p>
      {shown.length === 0 ? (
        <p className="pr-empty">Bu seçimdə iş yoxdur.</p>
      ) : (
        <div className="pr-pages va">
          {pages.map((group, i) => (
            <section key={group[0].submissionId} className="pr-sheet va-sheet" aria-label={`Səs analitikası, səhifə ${i + 1}`}>
              <header className="va-head">
                <span className="va-head__flag">Admin analitikası — münsiflərə verilmir</span>
                <span className="va-head__title">
                  {c.competitionTitle} · {order === 'votes' ? 'etibarlı səslərə görə' : 'nömrəyə görə'}
                  {onlyInvalidated && ' · yalnız etibarsız sayılmış səsi olanlar'}
                </span>
                <span className="va-head__page tabular">
                  {i + 1} / {pages.length}
                </span>
              </header>
              {i === 0 && <Summary c={c} />}
              <div className="va-grid">
                {group.map((w) => (
                  <Card key={w.submissionId} w={w} max={max} picture={pic(w)} />
                ))}
              </div>
              <footer className="pr-foot va-foot">
                <span>Mənbə: serverdəki səs qeydləri · {formatDateTime(c.generatedAt)}</span>
                <span className="tabular">
                  Səhifə {i + 1} / {pages.length}
                </span>
              </footer>
            </section>
          ))}
        </div>
      )}
    </>
  )
}

function Summary({ c }: { c: VoteCatalog }) {
  const total = c.validVotes + c.invalidatedVotes
  return (
    <div className="va-summary">
      <p className="va-summary__numbers tabular">
        <strong>{c.validVotes}</strong> etibarlı · <strong>{c.invalidatedVotes}</strong> etibarsız sayılıb
        {c.invalidatedVotes > 0 && ` (${breakdownText(c.invalidated)})`} · cəmi {total}
        {c.outsideCatalog > 0 && ` · siyahıdan kənar işlərə ${c.outsideCatalog} səs`}
      </p>
      <Bar valid={c.validVotes} invalid={c.invalidatedVotes} width={100} />
      <p className="va-legend">
        <span className="va-key va-key--valid" /> etibarlı səs
        <span className="va-key va-key--invalid" /> təsdiqlənmiş audit qaydası ilə etibarsız sayılıb (D12 telefon, R2, D14)
        {c.pendingReview === null && <span className="va-legend__note"> · baxış gözləyən vəziyyət qeydə alınmır — göstərilmir</span>}
      </p>
    </div>
  )
}

function Bar({ valid, invalid, width }: { valid: number; invalid: number; width: number }) {
  const total = valid + invalid
  return (
    <span className="va-bar" role="img" aria-label={`${valid} etibarlı, ${invalid} etibarsız`}>
      <span className="va-bar__fill" style={{ width: `${total === 0 ? 0 : Math.max(width, 2)}%` }}>
        {valid > 0 && <span className="va-bar__valid" style={{ flexGrow: valid }} />}
        {invalid > 0 && <span className="va-bar__invalid" style={{ flexGrow: invalid }} />}
      </span>
    </span>
  )
}

function Card({ w, max, picture }: { w: VoteCatalogWork; max: number; picture: Picture | undefined }) {
  const total = w.validVotes + w.invalidatedVotes
  const number = catalogNumber(w.reference)
  return (
    <article className="va-card" aria-label={`${w.position}. ${number}`}>
      <div className="va-card__top">
        <span className="va-card__pos tabular" title={w.tieSize > 1 ? `${w.tieSize} iş bərabərdir` : undefined}>
          {w.position}
          {w.tieSize > 1 && '='}
        </span>
        <span className="va-card__ref tabular">{number}</span>
      </div>
      <figure className="va-card__fig">
        {!w.image ? (
          <span className="pr-figure__none">Şəkil yoxdur</span>
        ) : picture === undefined ? (
          <span className="pr-figure__none">Yüklənir…</span>
        ) : picture === 'failed' ? (
          <span className="pr-figure__none">Şəkil yüklənmədi</span>
        ) : (
          <img src={picture.url} alt={`${number} ${w.title ?? ''}`.trim()} />
        )}
      </figure>
      {w.title && <p className="va-card__title">{w.title}</p>}
      <p className="va-card__votes tabular">
        <strong>{w.validVotes}</strong> etibarlı
        {w.invalidatedVotes > 0 && <span className="va-card__invalid"> · {w.invalidatedVotes} etibarsız</span>}
      </p>
      <Bar valid={w.validVotes} invalid={w.invalidatedVotes} width={(total / max) * 100} />
      <p className="va-card__why">{w.invalidatedVotes > 0 ? breakdownText(w.invalidated) : 'Etibarsız sayılmış səs yoxdur'}</p>
    </article>
  )
}
