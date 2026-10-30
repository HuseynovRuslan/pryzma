import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import type { ProjectStatus } from '../../api/types'
import { PageError, PageLoading } from '../../components/PageState'
import { ProjectFallback } from '../../components/Motifs'
import { accentVars } from '../../lib/accent'
import { formatDateTime } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { adminApi, type AdminProject, type ProjectInput } from '../api'
import { CoverPanel } from '../CoverPanel'
import { PermanentDelete } from '../PermanentDelete'
import { AField } from '../forms'
import { readErrors, type FieldErrors } from '../errors'
import { slugify } from '../slug'
import { validateProject } from '../validation'
import { AuditList, CompetitionStatusBadge, ConfirmButton, DemoBadge, Notice, PageHeader, PhaseNote, ProjectStatusBadge } from '../ui'

const HEX = /^#[0-9a-f]{6}$/i

const empty: ProjectInput = { name: '', slug: '', shortDescription: '', description: '', accentColor: '#10262B', sortOrder: 0 }

export default function ProjectEditPage() {
  const { id } = useParams()
  const isNew = id === undefined
  const state = useApi((signal) => (isNew ? Promise.resolve(null) : adminApi.project(Number(id), signal)), [id])

  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  return <ProjectEditor key={id ?? 'new'} initial={state.data} />
}

function ProjectEditor({ initial }: { initial: AdminProject | null }) {
  const navigate = useNavigate()
  const [project, setProject] = useState(initial)
  const [values, setValues] = useState<ProjectInput>(
    initial
      ? {
          name: initial.name,
          slug: initial.slug,
          shortDescription: initial.shortDescription,
          description: initial.description ?? '',
          accentColor: initial.accentColor ?? '#10262B',
          sortOrder: initial.sortOrder,
        }
      : empty,
  )
  const [slugTouched, setSlugTouched] = useState(!!initial)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [message, setMessage] = useState<{ tone: 'error' | 'success'; text: string }>()
  const [busy, setBusy] = useState(false)
  const [auditKey, setAuditKey] = useState(0)
  useTitle(project ? `${project.name} — idarəetmə` : 'Yeni layihə')

  const set = <K extends keyof ProjectInput>(key: K, value: ProjectInput[K]) => {
    setValues((v) => {
      const next = { ...v, [key]: value }
      if (key === 'name' && !slugTouched) next.slug = slugify(String(value))
      return next
    })
    setErrors((e) => ({ ...e, [key]: undefined }))
  }

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setMessage(undefined)
    const found = validateProject(values)
    setErrors(found)
    if (Object.values(found).some(Boolean)) return
    setBusy(true)
    try {
      const saved = project ? await adminApi.updateProject(project.id, values) : await adminApi.createProject(values)
      setAuditKey((k) => k + 1)
      if (!project) {
        navigate(`/admin/projects/${saved.id}`, { replace: true, state: { created: true } })
        return
      }
      setProject(saved)
      setMessage({ tone: 'success', text: 'Dəyişikliklər yadda saxlanıldı.' })
    } catch (err) {
      const { message: text, fields } = readErrors(err, 'Yadda saxlanılmadı.')
      setErrors(fields)
      setMessage({ tone: 'error', text })
    } finally {
      setBusy(false)
    }
  }

  const changeStatus = async (status: ProjectStatus) => {
    if (!project) return
    try {
      setProject(await adminApi.projectStatus(project.id, status))
      setAuditKey((k) => k + 1)
    } catch (err) {
      setMessage({ tone: 'error', text: readErrors(err, 'Status dəyişmədi.').message })
    }
  }

  const colour = HEX.test(values.accentColor) ? values.accentColor : '#10262B'

  return (
    <>
      <PageHeader
        crumbs={[{ label: 'İdarəetmə', to: '/admin' }, { label: 'Layihələr', to: '/admin/projects' }, { label: project?.name ?? 'Yeni' }]}
        title={project?.name ?? 'Yeni layihə'}
        meta={
          project && (
            <>
              <ProjectStatusBadge status={project.status} />
              {project.isDemo && <DemoBadge />}
              {project.status === 'Published' && (
                <a href={`/projects/${project.slug}`} target="_blank" rel="noreferrer">
                  Saytda bax
                </a>
              )}
              <span className="tabular">Yenilənib: {formatDateTime(project.updatedAt)}</span>
            </>
          )
        }
      />

      <div className="a-split a-split--wide-aside">
        <form className="a-panel a-form" onSubmit={save} noValidate>
          {message && <Notice tone={message.tone}>{message.text}</Notice>}
          {!project && <Notice>Yeni layihə qaralama kimi yaradılır. Dərc edənə qədər saytda görünmür.</Notice>}
          <AField label="Ad" error={errors.name}>
            <input className="input" value={values.name} onChange={(e) => set('name', e.target.value)} maxLength={160} />
          </AField>
          <AField label="Ünvan (slug)" hint={`Saytda: /projects/${values.slug || slugify(values.name) || '…'}`} error={errors.slug}>
            <input
              className="input"
              value={values.slug}
              onChange={(e) => {
                setSlugTouched(true)
                set('slug', e.target.value)
              }}
              maxLength={120}
              spellCheck={false}
            />
          </AField>
          <AField label="Qısa təsvir" hint="Layihə kartında və başlığın altında görünür." error={errors.shortDescription}>
            <textarea className="input" rows={3} value={values.shortDescription} onChange={(e) => set('shortDescription', e.target.value)} maxLength={400} />
          </AField>
          <AField label="Ətraflı təsvir" hint="İstəyə bağlı. Boş buraxılsa layihə səhifəsində “Layihə haqqında” bölməsi görünmür." error={errors.description}>
            <textarea className="input" rows={6} value={values.description} onChange={(e) => set('description', e.target.value)} />
          </AField>
          <div className="a-form__row">
            <div className="a-colour">
              <input type="color" aria-label="Rəngi palitradan seçin" value={colour} onChange={(e) => set('accentColor', e.target.value.toUpperCase())} />
              <AField label="Əsas rəng" error={errors.accentColor}>
                <input className="input" value={values.accentColor} onChange={(e) => set('accentColor', e.target.value)} maxLength={7} spellCheck={false} />
              </AField>
            </div>
            <AField label="Sıra" hint="Kiçik rəqəm əvvəl göstərilir.">
              <input className="input" type="number" min={0} value={values.sortOrder} onChange={(e) => set('sortOrder', Number(e.target.value) || 0)} />
            </AField>
          </div>
          <div className="a-actions">
            <button type="submit" className="btn btn--primary" disabled={busy}>
              {project ? 'Yadda saxla' : 'Layihəni yarat'}
            </button>
            <Link to="/admin/projects" className="btn btn--ghost">
              Ləğv et
            </Link>
          </div>
        </form>

        <div className="a-form a-split__aside">
          <section className="a-panel" aria-label="Önizləmə">
            <div className="a-preview" style={accentVars(colour)}>
              {project?.coverImage ? <img src={project.coverImage} alt="" /> : <ProjectFallback seed={values.slug || 'new'} />}
              <span className="a-preview__name">{values.name || 'Layihənin adı'}</span>
            </div>
            <p className="a-muted">Örtük şəkli yoxdursa layihənin rəngi və avtomatik naxışı istifadə olunur.</p>
          </section>

          {project && (
            <>
              <StatusPanel project={project} onChange={changeStatus} />
              <CoverPanel
                title="Örtük şəkli"
                hint="PNG, JPG və ya WEBP. Şəkil yoxdursa avtomatik naxış göstərilir."
                image={project.coverImage}
                upload={(file, onProgress) => adminApi.uploadCover(project.id, file, onProgress)}
                remove={() => adminApi.removeCover(project.id)}
                onChange={(p) => {
                  setProject(p)
                  setAuditKey((k) => k + 1)
                }}
              />
              <section className="a-panel" aria-labelledby="comps-title">
                <div className="a-panel__head">
                  <h2 id="comps-title" className="a-panel__title">
                    Müsabiqələr
                  </h2>
                  <Link to={`/admin/competitions/new?projectId=${project.id}`} className="btn btn--sm">
                    + Yeni müsabiqə
                  </Link>
                </div>
                <ProjectCompetitions projectId={project.id} />
              </section>
              <section className="a-panel" aria-labelledby="history-title">
                <h2 id="history-title" className="a-panel__title">
                  Tarixçə
                </h2>
                <AuditList entityType="Project" entityId={project.id} refreshKey={auditKey} />
              </section>
              <section className="a-panel" aria-labelledby="danger-title">
                <h2 id="danger-title" className="a-panel__title">
                  Silmək
                </h2>
                <PermanentDelete
                  noun="layihəni"
                  loadImpact={() => adminApi.projectDeleteImpact(project.id)}
                  onDelete={() => adminApi.deleteProjectPermanently(project.id)}
                  onDeleted={() => navigate('/admin/projects', { replace: true, state: { deleted: true } })}
                />
              </section>
            </>
          )}
        </div>
      </div>
    </>
  )
}

function StatusPanel({ project, onChange }: { project: AdminProject; onChange: (s: ProjectStatus) => Promise<void> }) {
  return (
    <section className="a-panel" aria-labelledby="status-title">
      <div className="a-panel__head">
        <h2 id="status-title" className="a-panel__title">
          Görünüş
        </h2>
        <ProjectStatusBadge status={project.status} />
      </div>
      <p className="a-muted">
        {project.status === 'Published'
          ? 'Layihə saytda görünür.'
          : project.status === 'Archived'
            ? 'Arxivdədir: saytda görünmür, bütün məlumat saxlanılır.'
            : 'Qaralamadır: yalnız idarəetmədə görünür.'}
      </p>
      <div className="a-actions">
        {project.status !== 'Published' && (
          <ConfirmButton label="Dərc et" confirmLabel="Bəli, dərc et" tone="primary" onConfirm={() => onChange('Published')} />
        )}
        {project.status === 'Published' && (
          <ConfirmButton label="Dərcdən çıxar" confirmLabel="Bəli, qaralamaya qaytar" onConfirm={() => onChange('Draft')} />
        )}
        {project.status !== 'Archived' && (
          <ConfirmButton label="Arxivləşdir" confirmLabel="Bəli, arxivləşdir" onConfirm={() => onChange('Archived')} />
        )}
      </div>
    </section>
  )
}

function ProjectCompetitions({ projectId }: { projectId: number }) {
  const state = useApi((signal) => adminApi.competitions(projectId, signal), [projectId])
  if (state.status !== 'success') return <p className="a-muted">Yüklənir…</p>
  if (state.data.length === 0) return <p className="a-muted">Bu layihədə hələ müsabiqə yoxdur.</p>
  return (
    <ul role="list" className="a-fieldlist">
      {state.data.map((c) => (
        <li key={c.id} className="a-fieldrow" style={{ gridTemplateColumns: 'minmax(0,1fr)' }}>
          <span className="a-fieldrow__main">
            <Link to={`/admin/competitions/${c.id}`} className="a-table__link">
              {c.title}
            </Link>
            <span className="a-fieldrow__tags">
              <CompetitionStatusBadge status={c.status} />
              {c.status === 'Published' && <PhaseNote phase={c.phase} />}
              <span>{c.submissionCount} təklif</span>
            </span>
          </span>
        </li>
      ))}
    </ul>
  )
}
