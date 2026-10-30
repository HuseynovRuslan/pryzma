import { useEffect } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDateTime, shortCompetitionTitle } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { adminApi, type Dashboard, type DashboardCompetition } from '../api'
import { Empty, PageHeader, Stat, SubmissionStatusBadge } from '../ui'

/** The URL's word for "every competition", so the choice survives a reload and is never mistaken for "not chosen". */
const ALL = 'all'

export default function DashboardPage() {
  useTitle('İdarəetmə')
  const [params, setParams] = useSearchParams()
  const raw = params.get('competitionId')
  const competitionId = raw && raw !== ALL ? Number(raw) : undefined
  const state = useApi((signal) => adminApi.dashboard(signal, competitionId), [competitionId])

  // Nothing chosen yet: the competition being run (the open one) is what an organiser looks at first.
  const open = state.status === 'success' && raw === null ? state.data.competitions.find((c) => c.isOpen) : undefined
  useEffect(() => {
    if (open) setParams({ competitionId: String(open.id) }, { replace: true })
  }, [open, setParams])
  const choose = (value: string) => setParams({ competitionId: value }, { replace: true })

  return (
    <>
      <PageHeader
        title="İcmal"
        actions={
          <>
            <Link to="/admin/projects/new" className="btn btn--sm">
              + Yeni layihə
            </Link>
            <Link to="/admin/competitions/new" className="btn btn--primary btn--sm">
              + Yeni müsabiqə
            </Link>
          </>
        }
      />
      {(state.status === 'loading' || open) && <PageLoading />}
      {state.status === 'error' && <PageError error={state.error} onRetry={state.retry} />}
      {state.status === 'success' && !open && (
        <div className="a-form" style={{ gap: 'var(--s-6)' }}>
          <label className="a-field a-dashboard__choice">
            <span className="a-field__label">Müsabiqə</span>
            <select className="input" value={competitionId ?? ALL} onChange={(e) => choose(e.target.value)}>
              <option value={ALL}>Bütün müsabiqələr</option>
              {state.data.competitions.map((c) => (
                <option key={c.id} value={c.id}>
                  {choiceLabel(c)}
                </option>
              ))}
            </select>
          </label>

          {/* The figures follow the answer, not the address: while another competition's numbers load, the cards
              keep the labels of the numbers they show. */}
          <Figures data={state.data} competitionId={state.data.competitionId ?? undefined} />

          <section className="a-panel" aria-labelledby="recent-title">
            <div className="a-panel__head">
              <h2 id="recent-title" className="a-panel__title">
                Son təkliflər
              </h2>
              <Link to={submissionsLink(competitionId)} className="a-muted">
                Hamısı
              </Link>
            </div>
            {state.data.recentSubmissions.length === 0 ? (
              <Empty title="Hələ göndərilmiş təklif yoxdur." />
            ) : (
              <table className="a-table a-table--stack">
                <thead>
                  <tr>
                    <th>İstinad</th>
                    <th>Təklif</th>
                    <th>Müsabiqə</th>
                    <th>Göndərilib</th>
                    <th>Status</th>
                  </tr>
                </thead>
                <tbody>
                  {state.data.recentSubmissions.map((s) => (
                    <tr key={s.id}>
                      <td data-label="İstinad">
                        <Link to={`/admin/submissions/${s.id}`} className="a-table__link tabular">
                          {s.referenceNumber}
                        </Link>
                      </td>
                      <td data-label="Təklif">
                        <span className="a-table__primary">{s.title || 'Adsız'}</span>
                        <span className="a-table__sub">{s.participant.name}</span>
                      </td>
                      <td data-label="Müsabiqə">
                        {s.competition.name}
                        {s.category && <span className="a-table__sub">{s.category.name}</span>}
                      </td>
                      <td data-label="Göndərilib" className="tabular">
                        {formatDateTime(s.submittedAt)}
                      </td>
                      <td data-label="Status">
                        <SubmissionStatusBadge status={s.status} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>
        </div>
      )}
    </>
  )
}

/** The submissions page, narrowed the same way (and to a status, when one is given). */
function submissionsLink(competitionId: number | undefined, status?: string) {
  const q = new URLSearchParams()
  if (competitionId) q.set('competitionId', String(competitionId))
  if (status) q.set('status', status)
  const query = q.toString()
  return `/admin/submissions${query ? `?${query}` : ''}`
}

const PHASE_WORD: Record<DashboardCompetition['phase'], string> = { Upcoming: 'tezliklə', Open: 'açıq', Closed: 'bağlı' }

/** "Novxanının simvolu (açıq)": where a competition stands, in one word. */
function choiceLabel(c: DashboardCompetition) {
  const word = c.status === 'Draft' ? 'qaralama' : c.status === 'Archived' ? 'arxivdə' : PHASE_WORD[c.phase]
  return `${shortCompetitionTitle(c.title)} (${word})`
}

/**
 * The figures: for one competition, its submissions by status; for all of them, the platform as before — projects
 * and open competitions included, which mean nothing for a single competition.
 */
function Figures({ data, competitionId }: { data: Dashboard; competitionId: number | undefined }) {
  const link = (status?: string) => submissionsLink(competitionId, status)
  return (
    <section aria-label="Göstəricilər" className="a-stats">
      {!competitionId && <Stat value={data.publishedProjects} label="Aktiv layihələr" to="/admin/projects" />}
      {!competitionId && <Stat value={data.openCompetitions} label="Qəbulu açıq müsabiqələr" to="/admin/competitions" />}
      <Stat value={data.totalSubmissions} label="Ümumi təkliflər" to={link()} />
      <Stat value={data.submittedSubmissions} label="Baxılmamış (Göndərilib)" to={link('Submitted')} />
      <Stat value={data.underReviewSubmissions} label="Yoxlanılır" to={link('UnderReview')} />
      {competitionId && <Stat value={data.approvedSubmissions} label="Təsdiqlənib" to={link('Approved')} />}
      {competitionId && <Stat value={data.rejectedSubmissions} label="Rədd edilib" to={link('Rejected')} />}
      <Stat value={data.draftSubmissions} label="Qaralamalar" />
    </section>
  )
}
