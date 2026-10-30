import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDate } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { adminApi } from '../api'
import { PermanentDelete, VOTED_ACCOUNT } from '../PermanentDelete'
import { Badge, DemoBadge, DemoFilter, Empty, PageHeader } from '../ui'

export default function ParticipantsPage() {
  useTitle('İştirakçılar — idarəetmə')
  const [params, setParams] = useSearchParams()
  const q = params.get('q') ?? ''
  const page = Number(params.get('page') ?? 1)
  const demo = params.get('demo') ?? undefined
  const [search, setSearch] = useState(q)
  const state = useApi((signal) => adminApi.participants(q, page, demo, signal), [q, page, demo])

  /** The URL holds the filters; a filter change goes back to the first page, paging adds a history step. */
  const go = (next: { q?: string; demo?: string; page?: number }, replace = true) =>
    setParams(
      {
        ...(next.q ? { q: next.q } : {}),
        ...(next.demo ? { demo: next.demo } : {}),
        ...(next.page && next.page > 1 ? { page: String(next.page) } : {}),
      },
      { replace },
    )

  useEffect(() => {
    if (search === q) return
    const t = window.setTimeout(() => go({ q: search, demo }), 350)
    return () => window.clearTimeout(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search, q])

  return (
    <>
      <PageHeader crumbs={[{ label: 'İdarəetmə', to: '/admin' }, { label: 'İştirakçılar' }]} title="İştirakçılar" />
      <div className="a-filters" style={{ gridTemplateColumns: 'minmax(0, 480px) minmax(0, 220px)' }}>
        <label className="a-field">
          <span className="a-field__label">Axtarış</span>
          <input className="input" type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Ad, e-mail və ya telefon" />
        </label>
        <DemoFilter value={demo} onChange={(value) => go({ q, demo: value })} />
      </div>
      {state.status === 'loading' && <PageLoading />}
      {state.status === 'error' && <PageError error={state.error} onRetry={state.retry} />}
      {state.status === 'success' &&
        (state.data.items.length === 0 ? (
          <Empty title="Heç kim tapılmadı." />
        ) : (
          <>
            <table className="a-table a-table--stack">
              <thead>
                <tr>
                  <th>Ad</th>
                  <th>E-mail</th>
                  <th>Telefon</th>
                  <th>Qeydiyyat</th>
                  <th className="num">Təkliflər</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {state.data.items.map((p) => (
                  <tr key={p.id}>
                    <td data-label="Ad">
                      <span className="a-inline">
                        <span className="a-table__primary">{p.name}</span>
                        {p.isAdmin && <Badge tone="live">Admin</Badge>}
                        {p.isDemo && <DemoBadge />}
                      </span>
                    </td>
                    <td data-label="E-mail">{p.email}</td>
                    <td data-label="Telefon">{p.phone ?? '—'}</td>
                    <td data-label="Qeydiyyat" className="tabular">
                      {formatDate(p.registeredAt)}
                    </td>
                    <td data-label="Təkliflər" className="num">
                      {p.submissionCount}
                    </td>
                    <td data-label="" className="a-table__actions">
                      {/* Only where the server already said the account holds nothing: work is deleted through
                          the entry it belongs to, where what goes is spelled out. An account that voted is never
                          removed, and the row says so instead of offering a button the server would refuse. */}
                      {p.canDelete ? (
                        <PermanentDelete
                          noun="hesabı"
                          loadImpact={() => adminApi.participantDeleteImpact(p.id)}
                          onDelete={() => adminApi.deleteParticipantPermanently(p.id)}
                          onDeleted={state.retry}
                        />
                      ) : (
                        p.hasVotes && !p.isAdmin && <p className="a-muted">{VOTED_ACCOUNT}</p>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <nav className="a-pager" aria-label="Səhifələr">
              <span className="tabular">{state.data.total} iştirakçı</span>
              <span className="a-inline">
                <button type="button" className="btn btn--sm" disabled={page <= 1} onClick={() => go({ q, demo, page: page - 1 }, false)}>
                  Əvvəlki
                </button>
                <button
                  type="button"
                  className="btn btn--sm"
                  disabled={page * state.data.pageSize >= state.data.total}
                  onClick={() => go({ q, demo, page: page + 1 }, false)}
                >
                  Növbəti
                </button>
              </span>
            </nav>
          </>
        ))}
    </>
  )
}
