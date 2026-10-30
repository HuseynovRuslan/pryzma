import { Link, useSearchParams } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDate } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { adminApi } from '../api'
import { CompetitionStatusBadge, DemoBadge, Empty, PageHeader, PhaseNote } from '../ui'

export default function CompetitionsPage() {
  useTitle('Müsabiqələr — idarəetmə')
  const [params, setParams] = useSearchParams()
  const projectId = params.get('projectId') ? Number(params.get('projectId')) : undefined
  const projects = useApi((signal) => adminApi.projects(signal), [])
  const state = useApi((signal) => adminApi.competitions(projectId, signal), [projectId])

  return (
    <>
      <PageHeader
        crumbs={[{ label: 'İdarəetmə', to: '/admin' }, { label: 'Müsabiqələr' }]}
        title="Müsabiqələr"
        actions={
          <Link to={`/admin/competitions/new${projectId ? `?projectId=${projectId}` : ''}`} className="btn btn--primary btn--sm">
            + Yeni müsabiqə
          </Link>
        }
      />
      <div className="a-filters" style={{ gridTemplateColumns: 'minmax(0, 320px)' }}>
        <label className="a-field">
          <span className="a-field__label">Layihə</span>
          <select
            className="input"
            value={projectId ?? ''}
            onChange={(e) => setParams(e.target.value ? { projectId: e.target.value } : {})}
          >
            <option value="">Bütün layihələr</option>
            {projects.data?.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </label>
      </div>
      {state.status === 'loading' && <PageLoading />}
      {state.status === 'error' && <PageError error={state.error} onRetry={state.retry} />}
      {state.status === 'success' &&
        (state.data.length === 0 ? (
          <Empty title="Müsabiqə yoxdur.">
            <Link to="/admin/competitions/new" className="btn btn--primary btn--sm">
              Müsabiqə yarat
            </Link>
          </Empty>
        ) : (
          <table className="a-table a-table--stack">
            <thead>
              <tr>
                <th>Müsabiqə</th>
                <th>Status</th>
                <th>Qəbul</th>
                <th className="num">Kateqoriyalar</th>
                <th className="num">Təkliflər</th>
                <th>Son tarix</th>
              </tr>
            </thead>
            <tbody>
              {state.data.map((c) => (
                <tr key={c.id} className={c.status === 'Archived' ? 'is-muted' : undefined}>
                  <td data-label="Müsabiqə">
                    <span className="a-inline">
                      <Link to={`/admin/competitions/${c.id}`} className="a-table__link">
                        {c.title}
                      </Link>
                      {c.isDemo && <DemoBadge />}
                    </span>
                    <span className="a-table__sub">{c.projectName}</span>
                  </td>
                  <td data-label="Status">
                    <CompetitionStatusBadge status={c.status} />
                  </td>
                  <td data-label="Qəbul">{c.status === 'Published' ? <PhaseNote phase={c.phase} /> : <span className="a-muted">—</span>}</td>
                  <td data-label="Kateqoriyalar" className="num">
                    {c.hasCategories ? `${c.activeCategoryCount} / ${c.categoryCount}` : '—'}
                  </td>
                  <td data-label="Təkliflər" className="num">
                    <Link to={`/admin/submissions?competitionId=${c.id}`}>{c.submissionCount}</Link>
                  </td>
                  <td data-label="Son tarix" className="tabular">
                    {c.endDate ? formatDate(c.endDate) : <span className="a-muted">Təyin olunmayıb</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ))}
    </>
  )
}
