import { useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { WinnerBadge } from '../../components/WinnerBadge'
import { formatDateTime } from '../../lib/format'
import { azCountOf, placeName } from '../../lib/text'
import { useApi } from '../../lib/useApi'
import { adminApi, type AdminCompetition, type AdminVoteWork, type AdminVoting, type AdminVotingNomination } from '../api'
import { juryPrintHref } from '../juryApi'
import { ImageThumb } from '../PreviewThumb'
import { previewLabel } from '../printPictures'
import { Badge, Empty, Notice, Stat } from '../ui'
import { WorkViewer, type ViewerFact } from '../WorkViewer'
import { DisposableEmailPanel } from './DisposableEmailPanel'
import { SuspicionPanel } from './SuspicionPanel'

const stateLabel: Record<string, string> = {
  NotConfigured: 'Səsvermə qurulmayıb',
  Upcoming: 'Başlamayıb',
  Open: 'Davam edir',
  Closed: 'Başa çatıb',
}

/**
 * What the public vote produced. It reads, it does not decide: the window is set on "Əsas məlumat" with the other
 * dates, and the winner is recorded on "Nəticələr", which already knows how to keep one per nomination.
 *
 * A tie is shown rather than resolved. Under the module's rules the vote decides who receives a prize, so where
 * two works finish level an admin chooses — a machine picking by id or by timestamp would be arbitrary and
 * impossible to explain to the person who lost.
 */
export function VotingTab({ competition }: { competition: AdminCompetition }) {
  const [version, setVersion] = useState(0)
  const state = useApi((signal) => adminApi.voting(competition.id, signal), [competition.id, version])
  // A decision on votes refreshes the tally; the last one stays on screen meanwhile, so the review is not lost.
  const kept = useRef<AdminVoting | null>(null)
  if (state.status === 'success') kept.current = state.data
  const v = state.status === 'success' ? state.data : kept.current
  if (!v) {
    if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
    return <PageLoading />
  }

  if (v.state === 'NotConfigured')
    return (
      <Empty title="Bu müsabiqədə səsvermə yoxdur">
        <p>
          Səsvermə tarixlərini “Əsas məlumat” bölməsində yazın. Hər ikisi doldurulan kimi səhifə saytda görünəcək.
        </p>
      </Empty>
    )

  const ties = v.nominations.filter((n) => n.isTie)

  return (
    <div className="a-form" style={{ gap: 'var(--s-5)' }}>
      <div className="a-stats">
        <Stat value={v.voteCount} label="səs verilib" />
        <Stat value={v.voterCount} label="səsverən" />
        <Stat value={v.nominations.length} label="nominasiya" />
        <Stat value={v.nominations.reduce((n, x) => n + x.workCount, 0)} label="səsverilən iş" />
      </div>

      <p className="a-muted">
        <Link to={juryPrintHref(competition.id, 'analytics')}>Səs analitikası kataloqu (A4 / PDF)</Link> — bütün işlər
        etibarlı səslərə görə, audit qərarları ilə. Yalnız adminlər üçün; münsiflərə verilmir.
      </p>

      <p>
        <Badge tone={v.state === 'Open' ? 'live' : 'closed'}>{stateLabel[v.state]}</Badge>{' '}
        {v.startsAt && v.endsAt && (
          <span className="a-muted">
            {formatDateTime(v.startsAt)} — {formatDateTime(v.endsAt)}
          </span>
        )}
      </p>

      {v.state === 'Open' && (
        <Notice tone="info">
          Səsvermə davam edir. Rəqəmlər saytda canlı görünür; qalibi yalnız səsvermə bitəndən sonra seçmək
          məsləhətdir.
        </Notice>
      )}

      {v.lateVoterCount > 0 && (
        <Notice tone="info">
          Səs verənlərdən <span className="tabular">{v.lateVoterCount}</span> nəfər hesabını səsvermə başlayandan sonra açıb.
          Belə hesablar səs verə bilər; cədvəldə hər işin səslərindən neçəsinin yeni hesablardan gəldiyi göstərilir —
          səslərinin çoxu yeni hesablardan olan işə mükafat verməzdən əvvəl bir daha baxın.
        </Notice>
      )}

      {ties.length > 0 && (
        <Notice tone="warn">
          {ties.length === 1 ? 'Bir nominasiyada' : `${ties.length} nominasiyada`} səslər bərabərdir. Qalibi say deyil,
          siz seçirsiniz — “Nəticələr” bölməsində.
        </Notice>
      )}

      {v.invalidatedVoteCount > 0 && (
        <Notice tone="info">
          <span className="tabular">{v.invalidatedVoteCount}</span> səs etibarsız sayılıb. Yuxarıdakı və aşağıdakı saylar
          yalnız sayılan səslərdir; etibarsız sayılanlar hər işin yanında ayrıca göstərilir.
        </Notice>
      )}

      {(v.voteCount > 0 || v.invalidatedVoteCount > 0) && (
        <SuspicionPanel competitionId={competition.id} onChanged={() => setVersion((n) => n + 1)} />
      )}

      {v.state === 'Closed' && (v.voteCount > 0 || v.invalidatedVoteCount > 0) && (
        <DisposableEmailPanel competitionId={competition.id} onChanged={() => setVersion((n) => n + 1)} />
      )}

      {v.unplaced.length > 0 && (
        <Notice tone="warn">
          <p>
            {v.unplaced.length} iş heç bir nominasiyaya aid deyil, ona görə də səsverməyə çıxmır. Hər birinin
            nominasiyasını təyin edin:
          </p>
          <ul>
            {v.unplaced.map((w) => (
              <li key={w.submissionId}>
                <Link to={`/admin/submissions/${w.submissionId}`}>
                  {w.reference} — {w.title}
                </Link>
              </li>
            ))}
          </ul>
        </Notice>
      )}

      {v.nominations.length === 0 ? (
        <Empty title="Nominasiya yoxdur">
          <p>Səsvermə nominasiyalar üzrə gedir. Onları “Kateqoriyalar” bölməsində yaradın.</p>
        </Empty>
      ) : (
        v.nominations.map((n) => <NominationTally key={n.categoryId} nomination={n} competitionId={competition.id} />)
      )}
    </div>
  )
}

/**
 * One nomination's ranking, made to be reviewed: each work with its picture (a PDF by the page that stands for it),
 * its vote rank, its valid votes, and — kept apart — the official place it holds, if any. "İşə bax" opens the work
 * whole over the table and steps through the works in this ranking's order; closing it leaves the table as it was.
 */
function NominationTally({ nomination: n, competitionId }: { nomination: AdminVotingNomination; competitionId: number }) {
  const [open, setOpen] = useState<number | null>(null)
  const ids = n.works.map((w) => w.submissionId)
  const byId = new Map(n.works.map((w) => [w.submissionId, w]))
  // What the ranking knows about a work besides the work: its place in the vote and its votes — never who voted.
  const facts = (id: number): ViewerFact[] => {
    const w = byId.get(id)
    if (!w) return []
    return [
      { label: 'Xalq sıralaması', value: w.place },
      { label: 'Etibarlı səs', value: w.voteCount },
      ...(w.invalidatedVoteCount > 0 ? [{ label: 'Etibarsız sayılmış', value: w.invalidatedVoteCount }] : []),
    ]
  }

  return (
    <section className="a-panel a-ranked" aria-labelledby={`nom-${n.categoryId}`}>
      <header className="a-panel__head">
        <h3 id={`nom-${n.categoryId}`} className="a-panel__title">
          {n.name}
          {!n.isActive && ' '}
          {!n.isActive && <Badge tone="archived">Bağlı</Badge>}
        </h3>
        <p className="a-muted">
          {n.workCount} iş · {n.voteCount} səs
          {n.isTie && ' · səslər bərabərdir'}
        </p>
      </header>

      {n.works.length === 0 ? (
        <p className="a-muted">Bu nominasiyada təqdim edilmiş iş yoxdur.</p>
      ) : (
        <table className="a-table a-rank a-rank--review">
          <thead>
            <tr>
              <th scope="col">Sıra</th>
              <th scope="col">İş</th>
              <th scope="col">İştirakçı</th>
              <th scope="col">Səs</th>
              <th scope="col">Yer</th>
              <th scope="col">Baxış</th>
            </tr>
          </thead>
          <tbody>
            {n.works.map((w, i) => (
              <WorkRow key={w.submissionId} work={w} tie={n.isTie} onOpen={() => setOpen(i)} />
            ))}
          </tbody>
        </table>
      )}
      {open !== null && (
        <WorkViewer
          ids={ids}
          index={open}
          onIndex={setOpen}
          onClose={() => setOpen(null)}
          context={{ title: 'Xalq səsverməsi', facts }}
          detailHref={(id) => `/admin/submissions/${id}?competitionId=${competitionId}`}
        />
      )}
    </section>
  )
}

function WorkRow({ work: w, tie, onOpen }: { work: AdminVoteWork; tie: boolean; onOpen: () => void }) {
  const leading = w.place === 1 && w.voteCount > 0
  const pages = w.pdfPages ?? 0
  const label = previewLabel(w.image)
  return (
    <tr>
      <td className="tabular a-rank__place">{w.place}</td>
      <td className="a-rank__work">
        {/* The picture, the title and the reference are one control: any of them opens the work. */}
        <button type="button" className="a-rank__open" onClick={onOpen} aria-label={`${w.reference} — ${w.title}: işə bax`}>
          <span className="a-table__thumb a-rank__thumb">
            <ImageThumb submissionId={w.submissionId} image={w.image} />
          </span>
          <span className="a-rank__name">
            <span className="a-rank__title">{w.title}</span>
            <span className="a-muted tabular">{w.reference}</span>
          </span>
        </button>
        {(pages > 0 || leading) && (
          <span className="a-rank__marks">
            {pages > 0 && <span className="a-rank__pdf tabular">PDF · {pages} səhifə</span>}
            {label && <span className="a-rank__preview tabular">{label}</span>}
            {leading && !w.isWinner && !tie && <Badge tone="good">Ən çox səs</Badge>}
            {leading && tie && <Badge tone="review">Bərabər</Badge>}
          </span>
        )}
      </td>
      <td className="a-rank__who">{w.participantName}</td>
      <td className="tabular a-rank__votes">
        {w.voteCount}
        {w.lateVoteCount > 0 && <LateVotes count={w.lateVoteCount} />}
        {w.invalidatedVoteCount > 0 && <SetAsideVotes count={w.invalidatedVoteCount} />}
      </td>
      {/* The official result's place, apart from the vote's rank — "—" where the work holds none. */}
      <td className="a-rank__official">
        {w.officialPlace ? (
          <>
            {w.officialPlace === 1 && <WinnerBadge size="sm" />} <span className="tabular">{placeName(w.officialPlace)}</span>
          </>
        ) : (
          <span className="a-muted" aria-label="Rəsmi yer yoxdur">
            —
          </span>
        )}
      </td>
      <td className="a-rank__view">
        <button type="button" className="btn btn--sm" onClick={onOpen} aria-label={`İşə bax: ${w.reference}`}>
          İşə bax
        </button>
      </td>
    </tr>
  )
}

/** "+77 etibarsız": votes an admin set aside (D12) — kept, not in the count beside it. */
export function SetAsideVotes({ count }: { count: number }) {
  return (
    <>
      <br />
      <span className="a-muted">+{count} etibarsız sayılıb</span>
    </>
  )
}

/** "5-i yeni hesabdan": how many of a work's votes came from accounts opened after voting began. */
export function LateVotes({ count }: { count: number }) {
  return (
    <>
      <br />
      <span className="a-muted">{azCountOf(count)} yeni hesabdan</span>
    </>
  )
}
