import { Link } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDate } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { adminApi } from '../api'
import { DemoBadge, Empty, PageHeader, ProjectStatusBadge } from '../ui'

export default function ProjectsPage() {
  useTitle('Layihələr — idarəetmə')
  const state = useApi((signal) => adminApi.projects(signal), [])

  return (
    <>
      <PageHeader
        crumbs={[{ label: 'İdarəetmə', to: '/admin' }, { label: 'Layihələr' }]}
        title="Layihələr"
        actions={
          <Link to="/admin/projects/new" className="btn btn--primary btn--sm">
            + Yeni layihə
          </Link>
        }
      />
      {state.status === 'loading' && <PageLoading />}
      {state.status === 'error' && <PageError error={state.error} onRetry={state.retry} />}
      {state.status === 'success' &&
        (state.data.length === 0 ? (
          <Empty title="Hələ layihə yoxdur.">
            <Link to="/admin/projects/new" className="btn btn--primary btn--sm">
              İlk layihəni yarat
            </Link>
          </Empty>
        ) : (
          <table className="a-table a-table--stack">
            <thead>
              <tr>
                <th>Layihə</th>
                <th>Status</th>
                <th className="num">Müsabiqələr</th>
                <th className="num">Təkliflər</th>
                <th className="num">Sıra</th>
                <th>Yenilənib</th>
              </tr>
            </thead>
            <tbody>
              {state.data.map((p) => (
                <tr key={p.id} className={p.status === 'Archived' ? 'is-muted' : undefined}>
                  <td data-label="Layihə">
                    <span className="a-inline">
                      <span
                        aria-hidden="true"
                        style={{ width: 14, height: 14, background: p.accentColor ?? 'var(--ink)', flex: 'none' }}
                      />
                      <Link to={`/admin/projects/${p.id}`} className="a-table__link">
                        {p.name}
                      </Link>
                      {p.isDemo && <DemoBadge />}
                    </span>
                    <span className="a-table__sub">/{p.slug}</span>
                  </td>
                  <td data-label="Status">
                    <ProjectStatusBadge status={p.status} />
                  </td>
                  <td data-label="Müsabiqələr" className="num">
                    {p.competitionCount}
                  </td>
                  <td data-label="Təkliflər" className="num">
                    {p.submissionCount}
                  </td>
                  <td data-label="Sıra" className="num">
                    {p.sortOrder}
                  </td>
                  <td data-label="Yenilənib" className="tabular">
                    {formatDate(p.updatedAt)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ))}
    </>
  )
}
