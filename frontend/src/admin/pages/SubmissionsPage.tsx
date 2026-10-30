import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDateTime } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { adminApi, type AdminSubmissionRow, type Paged, type SubmissionFilters } from '../api'
import { AdminModerationStatuses } from '../constants'
import { submissionStatusAdmin } from '../labels'
import { bakuDayEnd, bakuDayStart } from '../lib'
import { WinnerBadge } from '../../components/WinnerBadge'
import { DemoBadge, DemoFilter, Empty, PageHeader, SubmissionStatusBadge } from '../ui'
import { SubmissionCover, SubmissionsGallery } from './SubmissionsGallery'
import { SubmissionQuickPreview } from './SubmissionQuickPreview'
import { printHref } from '../print'

const FILTER_KEYS = ['projectId', 'competitionId', 'categoryId', 'status', 'winner', 'demo', 'from', 'to', 'q', 'page'] as const

type View = 'table' | 'gallery'
const VIEW_KEY = 'prizma.admin.submissions.view'

/** The reviewer's last choice, on this browser only; a table until they choose otherwise. */
function readView(): View {
  try {
    return localStorage.getItem(VIEW_KEY) === 'gallery' ? 'gallery' : 'table'
  } catch {
    return 'table'
  }
}

export default function SubmissionsPage() {
  useTitle('Təkliflər — idarəetmə')
  const [params, setParams] = useSearchParams()
  const filters: SubmissionFilters = Object.fromEntries(FILTER_KEYS.map((k) => [k, params.get(k) ?? undefined]))
  const [search, setSearch] = useState(filters.q ?? '')
  const [view, setView] = useState<View>(readView)
  // The work open in the quick preview, by id: the preview fetches its own details and so survives a page change.
  const [previewId, setPreviewId] = useState<number | null>(null)
  // Stepping past the end of a page opens the neighbouring page's first (or last) work once that page has arrived.
  // The page is part of the wait: the router applies the new address a render later, so "the current page" can
  // still be the old one when the wait begins.
  const [openOnArrival, setOpenOnArrival] = useState<{ page: number; pick: 'first' | 'last' } | null>(null)

  const chooseView = (next: View) => {
    setView(next)
    try {
      localStorage.setItem(VIEW_KEY, next)
    } catch {
      /* private mode: the choice lasts for this visit */
    }
  }

  const projects = useApi((signal) => adminApi.projects(signal), [])
  const competitions = useApi((signal) => adminApi.competitions(undefined, signal), [])
  const categories = useApi(
    (signal) => (filters.competitionId ? adminApi.categories(Number(filters.competitionId), signal) : Promise.resolve([])),
    [filters.competitionId],
  )
  const query = {
    ...filters,
    from: filters.from ? bakuDayStart(filters.from) : undefined,
    to: filters.to ? bakuDayEnd(filters.to) : undefined,
  }
  const state = useApi((signal) => adminApi.submissions(query, signal), [params.toString()])

  // Debounced search: typing does not fire a request per keystroke.
  useEffect(() => {
    if ((filters.q ?? '') === search) return
    const t = window.setTimeout(() => update({ q: search || undefined }), 350)
    return () => window.clearTimeout(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search])

  function update(patch: Partial<SubmissionFilters>) {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(patch)) {
      if (v) next.set(k, v)
      else next.delete(k)
    }
    if (!('page' in patch)) next.delete('page')
    setParams(next, { replace: true })
  }

  const visibleCompetitions = (competitions.data ?? []).filter((c) => !filters.projectId || String(c.projectId) === filters.projectId)
  const page = Number(filters.page ?? 1)
  const hasFilters = FILTER_KEYS.some((k) => k !== 'page' && filters[k])

  // The last page that arrived: the preview keeps its bearings while the next one is on its way.
  const [lastList, setLastList] = useState<Paged<AdminSubmissionRow> | null>(null)
  useEffect(() => {
    if (state.status !== 'success') return
    setLastList(state.data)
    // Only the page that was asked for: while it is on its way, the old one is still here.
    if (openOnArrival && state.data.page === openOnArrival.page) {
      const items = state.data.items
      setPreviewId(items.length === 0 ? null : openOnArrival.pick === 'first' ? items[0].id : items[items.length - 1].id)
      setOpenOnArrival(null)
    }
  }, [state, openOnArrival])
  const list = state.status === 'success' ? state.data : lastList

  const at = list && previewId !== null ? list.items.findIndex((s) => s.id === previewId) : -1
  const hasNextPage = list ? list.page * list.pageSize < list.total : false
  const step = (dir: 1 | -1) => {
    if (!list || at < 0) return
    const next = at + dir
    if (next >= 0 && next < list.items.length) {
      setPreviewId(list.items[next].id)
    } else if (dir === 1 && hasNextPage) {
      setOpenOnArrival({ page: list.page + 1, pick: 'first' })
      update({ page: String(list.page + 1) })
    } else if (dir === -1 && list.page > 1) {
      setOpenOnArrival({ page: list.page - 1, pick: 'last' })
      update({ page: String(list.page - 1) })
    }
  }

  return (
    <>
      <PageHeader
        crumbs={[{ label: 'İdarəetmə', to: '/admin' }, { label: 'Təkliflər' }]}
        title="Təkliflər"
        actions={
          // The same selection, on paper: the filters travel with it, the page number does not.
          <Link to={printHref(params)} className="btn">
            Çap et / PDF
          </Link>
        }
      />

      <form className="a-filters" role="search" aria-label="Təklifləri süz" onSubmit={(e) => e.preventDefault()}>
        <label className="a-field a-filters__search">
          <span className="a-field__label">Axtarış</span>
          <input
            className="input"
            type="search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="İstinad nömrəsi, iştirakçının adı və ya e-maili"
          />
        </label>
        <label className="a-field">
          <span className="a-field__label">Status</span>
          <select className="input" value={filters.status ?? ''} onChange={(e) => update({ status: e.target.value || undefined })}>
            <option value="">Hamısı</option>
            {AdminModerationStatuses.map((s) => (
              <option key={s} value={s}>
                {submissionStatusAdmin[s]}
              </option>
            ))}
          </select>
        </label>
        <label className="a-field">
          <span className="a-field__label">Layihə</span>
          <select
            className="input"
            value={filters.projectId ?? ''}
            onChange={(e) => update({ projectId: e.target.value || undefined, competitionId: undefined, categoryId: undefined })}
          >
            <option value="">Hamısı</option>
            {projects.data?.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </label>
        <label className="a-field">
          <span className="a-field__label">Müsabiqə</span>
          <select
            className="input"
            value={filters.competitionId ?? ''}
            onChange={(e) => update({ competitionId: e.target.value || undefined, categoryId: undefined })}
          >
            <option value="">Hamısı</option>
            {visibleCompetitions.map((c) => (
              <option key={c.id} value={c.id}>
                {c.title}
              </option>
            ))}
          </select>
        </label>
        <label className="a-field">
          <span className="a-field__label">Kateqoriya</span>
          <select
            className="input"
            value={filters.categoryId ?? ''}
            disabled={!filters.competitionId || (categories.data?.length ?? 0) === 0}
            onChange={(e) => update({ categoryId: e.target.value || undefined })}
          >
            <option value="">{filters.competitionId ? 'Hamısı' : 'Əvvəlcə müsabiqəni seçin'}</option>
            {categories.data?.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label className="a-field">
          <span className="a-field__label">Nəticə</span>
          <select className="input" value={filters.winner ?? ''} onChange={(e) => update({ winner: e.target.value || undefined })}>
            <option value="">Hamısı</option>
            <option value="true">Yalnız qaliblər</option>
          </select>
        </label>
        <DemoFilter value={filters.demo} onChange={(demo) => update({ demo })} />
        <label className="a-field">
          <span className="a-field__label">Tarixdən</span>
          <input className="input" type="date" value={filters.from ?? ''} onChange={(e) => update({ from: e.target.value || undefined })} />
        </label>
        <label className="a-field">
          <span className="a-field__label">Tarixədək</span>
          <input className="input" type="date" value={filters.to ?? ''} onChange={(e) => update({ to: e.target.value || undefined })} />
        </label>
        <div className="a-filters__foot">
          <span aria-live="polite">{state.status === 'success' ? `${state.data.total} təklif` : ' '}</span>
          {/* Two ways to look at the same filtered page; the choice is remembered on this browser. */}
          <fieldset className="a-seg a-viewswitch">
            <legend className="visually-hidden">Görünüş</legend>
            {(
              [
                ['table', 'Cədvəl'],
                ['gallery', 'Qalereya'],
              ] as const
            ).map(([value, label]) => (
              <label key={value} className="a-seg__option">
                <input type="radio" name="submissions-view" value={value} checked={view === value} onChange={() => chooseView(value)} />
                <span>{label}</span>
              </label>
            ))}
          </fieldset>
          {hasFilters && (
            <button
              type="button"
              className="btn btn--ghost btn--sm"
              onClick={() => {
                setSearch('')
                setParams({}, { replace: true })
              }}
            >
              Filtrləri təmizlə
            </button>
          )}
        </div>
      </form>

      {state.status === 'loading' && <PageLoading />}
      {state.status === 'error' && <PageError error={state.error} onRetry={state.retry} />}
      {state.status === 'success' &&
        (state.data.items.length === 0 ? (
          <Empty title={hasFilters ? 'Bu filtrlərə uyğun təklif yoxdur.' : 'Hələ göndərilmiş təklif yoxdur.'} />
        ) : (
          <>
            {view === 'gallery' ? (
              <SubmissionsGallery items={state.data.items} onOpen={setPreviewId} />
            ) : (
              <SubmissionsTable items={state.data.items} onOpen={setPreviewId} />
            )}
            <nav className="a-pager" aria-label="Səhifələr">
              <span className="tabular">
                {(page - 1) * state.data.pageSize + 1}–{Math.min(page * state.data.pageSize, state.data.total)} / {state.data.total}
              </span>
              <span className="a-inline">
                <button type="button" className="btn btn--sm" disabled={page <= 1} onClick={() => update({ page: String(page - 1) })}>
                  Əvvəlki
                </button>
                <button
                  type="button"
                  className="btn btn--sm"
                  disabled={page * state.data.pageSize >= state.data.total}
                  onClick={() => update({ page: String(page + 1) })}
                >
                  Növbəti
                </button>
              </span>
            </nav>
          </>
        ))}

      {previewId !== null && (
        <SubmissionQuickPreview
          id={previewId}
          row={at >= 0 ? list?.items[at] : undefined}
          position={list && at >= 0 ? { at: (list.page - 1) * list.pageSize + at + 1, total: list.total } : null}
          canPrev={list !== null && at >= 0 && (at > 0 || list.page > 1)}
          canNext={list !== null && at >= 0 && (at < list.items.length - 1 || hasNextPage)}
          busy={state.status === 'loading'}
          onPrev={() => step(-1)}
          onNext={() => step(1)}
          onClose={() => {
            setPreviewId(null)
            setOpenOnArrival(null)
          }}
        />
      )}
    </>
  )
}

/** The page of results as rows: every column the list has, and a small picture that opens the quick preview. */
function SubmissionsTable({ items, onOpen }: { items: AdminSubmissionRow[]; onOpen: (id: number) => void }) {
  return (
    <table className="a-table a-table--stack">
      <thead>
        <tr>
          <th className="a-table__preview-col">Önizləmə</th>
          <th>İstinad</th>
          <th>İştirakçı</th>
          <th>Layihə / müsabiqə</th>
          <th>Kateqoriya</th>
          <th>Göndərilib</th>
          <th>Status</th>
        </tr>
      </thead>
      <tbody>
        {items.map((s) => (
          <tr key={s.id} className={s.isWinner ? 'is-winner' : undefined}>
            <td data-label="Önizləmə" className="a-table__preview">
              <button
                type="button"
                className="a-table__thumb"
                onClick={() => onOpen(s.id)}
                aria-label={`${s.referenceNumber ?? 'Təklif'}: önizləməni aç`}
              >
                <SubmissionCover row={s} />
              </button>
            </td>
            <td data-label="İstinad">
              <Link to={`/admin/submissions/${s.id}`} className="a-table__link tabular">
                {s.referenceNumber}
              </Link>
              {s.isWinner && <WinnerBadge size="sm" />}
              {s.isDemo && <DemoBadge />}
              <span className="a-table__sub">{s.title || 'Adsız'}</span>
            </td>
            <td data-label="İştirakçı">
              {s.participant.name}
              <span className="a-table__sub">{s.participant.email}</span>
            </td>
            <td data-label="Müsabiqə">
              {s.competition.name}
              <span className="a-table__sub">{s.project.name}</span>
            </td>
            <td data-label="Kateqoriya">{s.category?.name ?? <span className="a-muted">—</span>}</td>
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
  )
}
