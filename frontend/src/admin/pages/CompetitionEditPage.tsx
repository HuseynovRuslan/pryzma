import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import type { CompetitionStatus } from '../../api/types'
import { PageError, PageLoading } from '../../components/PageState'
import { formatDateTime } from '../../lib/format'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { adminApi, type AdminCompetition, type AdminProject, type CompetitionInput } from '../api'
import { CoverPanel } from '../CoverPanel'
import { PermanentDelete } from '../PermanentDelete'
import { AField } from '../forms'
import { readErrors, type FieldErrors } from '../errors'
import { fromBakuInput, toBakuInput } from '../lib'
import { slugify } from '../slug'
import { AuditList, Badge, CompetitionStatusBadge, ConfirmButton, DemoBadge, Notice, PageHeader, PhaseNote } from '../ui'
import { hasErrors, validateCompetition } from '../validation'
import { CategoriesTab } from './CategoriesTab'
import { FormBuilderTab } from './FormBuilderTab'
import { FinalTab } from './FinalTab'
import { JuryTab } from './JuryTab'
import { ResultsTab } from './ResultsTab'
import { VotingTab } from './VotingTab'

type Tab = 'settings' | 'categories' | 'form' | 'voting' | 'final' | 'jury' | 'results' | 'history'

export default function CompetitionEditPage() {
  const { id } = useParams()
  const isNew = id === undefined
  const state = useApi(
    async (signal) => ({
      competition: isNew ? null : await adminApi.competition(Number(id), signal),
      projects: await adminApi.projects(signal),
    }),
    [id],
  )
  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  return <CompetitionEditor key={id ?? 'new'} initial={state.data.competition} projects={state.data.projects} />
}

function toInput(c: AdminCompetition | null, projectId: number): CompetitionInput {
  return {
    projectId: c?.projectId ?? projectId,
    title: c?.title ?? '',
    slug: c?.slug ?? '',
    shortDescription: c?.shortDescription ?? '',
    description: c?.description ?? '',
    rules: c?.rules ?? '',
    requirements: c?.requirements ?? '',
    nominations: c?.nominations ?? '',
    judgingCriteria: c?.judgingCriteria ?? '',
    usageTerms: c?.usageTerms ?? '',
    contactNote: c?.contactNote ?? '',
    startDate: c?.startDate ?? null,
    endDate: c?.endDate ?? null,
    resultsAnnouncedOn: c?.resultsAnnouncedOn ?? null,
    exhibitionOn: c?.exhibitionOn ?? null,
    prize: c?.prize ?? '',
    hasCategories: c?.hasCategories ?? false,
    categoryLabel: c?.categoryLabel ?? '',
    maxSubmissionsPerUser: c?.maxSubmissionsPerUser ?? null,
    sortOrder: c?.sortOrder ?? 0,
    votingStartsAt: c?.votingStartsAt ?? null,
    votingEndsAt: c?.votingEndsAt ?? null,
  }
}

function CompetitionEditor({ initial, projects }: { initial: AdminCompetition | null; projects: AdminProject[] }) {
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const [competition, setCompetition] = useState(initial)
  const presetProject = Number(params.get('projectId')) || (projects.length === 1 ? projects[0].id : 0)
  const [values, setValues] = useState<CompetitionInput>(toInput(initial, presetProject))
  const [slugTouched, setSlugTouched] = useState(!!initial)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [message, setMessage] = useState<{ tone: 'error' | 'success'; text: string; list?: string[] }>()
  const [busy, setBusy] = useState(false)
  const [auditKey, setAuditKey] = useState(0)
  const tab = (competition ? (params.get('tab') as Tab | null) : null) ?? 'settings'
  useTitle(competition ? `${competition.title} — idarəetmə` : 'Yeni müsabiqə')

  const set = <K extends keyof CompetitionInput>(key: K, value: CompetitionInput[K]) => {
    setValues((v) => {
      const next = { ...v, [key]: value }
      if (key === 'title' && !slugTouched) next.slug = slugify(String(value))
      return next
    })
    setErrors((e) => ({ ...e, [key]: undefined }))
  }

  const refresh = async () => {
    if (!competition) return
    setCompetition(await adminApi.competition(competition.id))
    setAuditKey((k) => k + 1)
  }

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setMessage(undefined)
    const found = validateCompetition(values)
    setErrors(found)
    if (hasErrors(found)) return
    setBusy(true)
    try {
      const saved = competition
        ? await adminApi.updateCompetition(competition.id, values)
        : await adminApi.createCompetition(values)
      if (!competition) {
        navigate(`/admin/competitions/${saved.id}?tab=${saved.hasCategories ? 'categories' : 'form'}`, { replace: true })
        return
      }
      setCompetition(saved)
      setAuditKey((k) => k + 1)
      setMessage({ tone: 'success', text: 'Dəyişikliklər yadda saxlanıldı.' })
    } catch (err) {
      const { message: text, fields } = readErrors(err, 'Yadda saxlanılmadı.')
      setErrors(fields)
      setMessage({ tone: 'error', text })
    } finally {
      setBusy(false)
    }
  }

  const changeStatus = async (status: CompetitionStatus) => {
    if (!competition) return
    setMessage(undefined)
    try {
      setCompetition(await adminApi.competitionStatus(competition.id, status))
      setAuditKey((k) => k + 1)
    } catch (err) {
      const { message: text, lists } = readErrors(err, 'Status dəyişmədi.')
      setMessage({ tone: 'error', text: lists.status ? 'Dərc etmək hələ mümkün deyil:' : text, list: lists.status })
    }
  }

  const tabLink = (key: Tab, label: string, count?: number) => (
    <Link
      to={`?tab=${key}`}
      replace
      aria-current={tab === key ? 'page' : undefined}
      onClick={(e) => {
        e.preventDefault()
        setParams({ tab: key }, { replace: true })
      }}
    >
      {label}
      {count !== undefined && <span className="a-tabs__count">{count}</span>}
    </Link>
  )

  return (
    <>
      <PageHeader
        crumbs={[
          { label: 'İdarəetmə', to: '/admin' },
          { label: 'Müsabiqələr', to: '/admin/competitions' },
          ...(competition ? [{ label: competition.projectName, to: `/admin/projects/${competition.projectId}` }] : []),
          { label: competition?.title ?? 'Yeni' },
        ]}
        title={competition?.title ?? 'Yeni müsabiqə'}
        meta={
          competition && (
            <>
              <CompetitionStatusBadge status={competition.status} />
              {competition.isDemo && <DemoBadge />}
              {competition.status === 'Published' && <PhaseNote phase={competition.phase} />}
              {competition.resultState === 'Published' && <Badge tone="good">Nəticə dərc olunub</Badge>}
              {competition.resultState === 'Prepared' && <Badge tone="review">Qalib seçilib</Badge>}
              {competition.status !== 'Draft' && competition.status !== 'Archived' && competition.projectStatus === 'Published' && (
                <a href={`/competitions/${competition.slug}`} target="_blank" rel="noreferrer">
                  Saytda bax
                </a>
              )}
              <Link to={`/admin/submissions?competitionId=${competition.id}`}>{competition.submissionCount} təklif</Link>
            </>
          )
        }
      />

      {competition && (
        <nav className="a-tabs" aria-label="Müsabiqə bölmələri">
          {tabLink('settings', 'Əsas məlumat')}
          {tabLink('categories', 'Kateqoriyalar', competition.hasCategories ? competition.categoryCount : undefined)}
          {tabLink('form', 'Təqdimat forması', competition.fieldCount)}
          {/* Only where a vote exists: an empty tab on every competition would be a question nobody asked. */}
          {competition.votingState !== 'NotConfigured' && tabLink('voting', 'Səsvermə')}
          {/* The jury final (D13): a voted competition with one prize list. */}
          {competition.votingState !== 'NotConfigured' && !competition.hasCategories && tabLink('final', 'Final')}
          {/* Every eligible work, scored by every juror; two independent awards (IndependentPublicAndJuryAwardsV1). */}
          {competition.votingState !== 'NotConfigured' && tabLink('jury', 'Münsiflər')}
          {tabLink('results', 'Nəticələr')}
          {tabLink('history', 'Tarixçə')}
        </nav>
      )}

      {message && (
        <div style={{ marginBottom: 'var(--s-4)' }}>
          <Notice tone={message.tone}>
            {message.text}
            {message.list && (
              <ul>
                {message.list.map((m) => (
                  <li key={m}>{m}</li>
                ))}
              </ul>
            )}
          </Notice>
        </div>
      )}

      {tab === 'settings' && (
        <div className="a-split">
          <form className="a-panel a-form" onSubmit={save} noValidate>
            {!competition && <Notice>Müsabiqə qaralama kimi yaradılır. Sonra kateqoriyaları və formanı qurub dərc edəcəksiniz.</Notice>}
            <AField label="Layihə" error={errors.projectId}>
              <select className="input" value={values.projectId || ''} onChange={(e) => set('projectId', Number(e.target.value))}>
                <option value="">Seçin</option>
                {projects.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </select>
            </AField>
            <AField label="Ad" error={errors.title}>
              <input className="input" value={values.title} onChange={(e) => set('title', e.target.value)} maxLength={200} />
            </AField>
            <AField label="Ünvan (slug)" hint={`Saytda: /competitions/${values.slug || slugify(values.title) || '…'}`} error={errors.slug}>
              <input
                className="input"
                value={values.slug}
                spellCheck={false}
                maxLength={120}
                onChange={(e) => {
                  setSlugTouched(true)
                  set('slug', e.target.value)
                }}
              />
            </AField>
            <AField label="Qısa təsvir" error={errors.shortDescription}>
              <textarea className="input" rows={3} maxLength={500} value={values.shortDescription} onChange={(e) => set('shortDescription', e.target.value)} />
            </AField>
            <AField label="Ətraflı təsvir" hint="Boş qalsa saytda göstərilmir.">
              <textarea className="input" rows={6} value={values.description} onChange={(e) => set('description', e.target.value)} />
            </AField>
            <AField label="Qaydalar" hint="Boş qalsa saytda göstərilmir.">
              <textarea className="input" rows={5} value={values.rules} onChange={(e) => set('rules', e.target.value)} />
            </AField>
            <AField label="İştirak tələbləri" hint="Boş qalsa saytda göstərilmir.">
              <textarea className="input" rows={4} value={values.requirements} onChange={(e) => set('requirements', e.target.value)} />
            </AField>
            <AField
              label="Nominasiyalar"
              hint="Hər nominasiya üçün: birinci sətir adı, sonrakı sətirlər izahı. Nominasiyaları boş sətirlə ayırın. Boş qalsa saytda göstərilmir."
            >
              <textarea className="input" rows={8} value={values.nominations} onChange={(e) => set('nominations', e.target.value)} />
            </AField>
            <AField
              label="Qiymətləndirmə meyarları"
              hint="Hər meyar öz sətrində. Faiz yazmayın — platforma bal hesablamır. Boş qalsa saytda göstərilmir."
            >
              <textarea className="input" rows={6} value={values.judgingCriteria} onChange={(e) => set('judgingCriteria', e.target.value)} />
            </AField>
            <AField
              label="Müəllif hüquqları və istifadə"
              hint="İşin müəllifində nə qalır, təşkilatçı nə edə bilər. Boş qalsa saytda göstərilmir."
            >
              <textarea className="input" rows={6} value={values.usageTerms} onChange={(e) => set('usageTerms', e.target.value)} />
            </AField>
            <div className="a-form__row">
              <AField label="Qəbulun başlanması" hint="Bakı vaxtı. Boş: dərc olunan andan.">
                <input className="input" type="datetime-local" value={toBakuInput(values.startDate)} onChange={(e) => set('startDate', fromBakuInput(e.target.value))} />
              </AField>
              <AField label="Son təqdim tarixi" hint="Bakı vaxtı. Boş: son tarix yoxdur." error={errors.endDate}>
                <input className="input" type="datetime-local" value={toBakuInput(values.endDate)} onChange={(e) => set('endDate', fromBakuInput(e.target.value))} />
              </AField>
            </div>
            <div className="a-form__row">
              <AField label="Nəticələrin elan tarixi" hint="Bakı vaxtı. Boş: təqvimdə göstərilmir.">
                <input className="input" type="datetime-local" value={toBakuInput(values.resultsAnnouncedOn)} onChange={(e) => set('resultsAnnouncedOn', fromBakuInput(e.target.value))} />
              </AField>
              <AField label="Sərgi tarixi" hint="Bakı vaxtı. Boş: təqvimdə göstərilmir.">
                <input className="input" type="datetime-local" value={toBakuInput(values.exhibitionOn)} onChange={(e) => set('exhibitionOn', fromBakuInput(e.target.value))} />
              </AField>
            </div>
            {/* The vote's own dates, apart from the entry deadline on purpose: a vote opens and closes without
                anybody touching the date participants were given. */}
            <div className="a-form__row">
              <AField
                label="Səsvermənin başlanması"
                hint="Bakı vaxtı. Hər ikisi boş: bu müsabiqədə ictimai səsvermə yoxdur."
                error={errors.votingStartsAt}
              >
                <input
                  className="input"
                  type="datetime-local"
                  value={toBakuInput(values.votingStartsAt)}
                  onChange={(e) => set('votingStartsAt', fromBakuInput(e.target.value))}
                />
              </AField>
              <AField label="Səsvermənin bitməsi" hint="Bakı vaxtı. Başlama tarixindən sonra olmalıdır." error={errors.votingEndsAt}>
                <input
                  className="input"
                  type="datetime-local"
                  value={toBakuInput(values.votingEndsAt)}
                  onChange={(e) => set('votingEndsAt', fromBakuInput(e.target.value))}
                />
              </AField>
            </div>
            <AField label="Mükafatlar" hint="Hər yer öz sətrində. Boş qalsa saytda göstərilmir.">
              <textarea className="input" rows={4} maxLength={1000} value={values.prize} onChange={(e) => set('prize', e.target.value)} />
            </AField>
            <AField label="Əlaqə" hint="Suallar üçün könüllü qeyd (e-mail, telefon). İşlər hər halda saytdan göndərilir.">
              <input className="input" maxLength={500} value={values.contactNote} onChange={(e) => set('contactNote', e.target.value)} />
            </AField>
            <div className="a-form__row">
              <AField label="Bir iştirakçıdan maksimum təklif" hint="Boş: limit yoxdur." error={errors.maxSubmissionsPerUser}>
                <input
                  className="input"
                  type="number"
                  min={1}
                  max={1000}
                  value={values.maxSubmissionsPerUser ?? ''}
                  onChange={(e) => set('maxSubmissionsPerUser', e.target.value ? Number(e.target.value) : null)}
                />
              </AField>
              <AField label="Sıra" hint="Layihə daxilində kiçik rəqəm əvvəl.">
                <input className="input" type="number" min={0} value={values.sortOrder} onChange={(e) => set('sortOrder', Number(e.target.value) || 0)} />
              </AField>
            </div>
            <div className="a-panel" style={{ background: 'var(--paper)' }}>
              <label className="check">
                <input type="checkbox" checked={values.hasCategories} onChange={(e) => set('hasCategories', e.target.checked)} />
                <span>İştirakçı kateqoriya seçir (məsələn, qəsəbə, janr, nominasiya)</span>
              </label>
              {values.hasCategories && (
                <AField label="Kateqoriyaların saytdakı adı" hint="Məsələn: Qəsəbələr, Janrlar, Nominasiyalar. Boş: “Kateqoriyalar”.">
                  <input className="input" maxLength={80} value={values.categoryLabel} onChange={(e) => set('categoryLabel', e.target.value)} />
                </AField>
              )}
            </div>
            <div className="a-actions">
              <button type="submit" className="btn btn--primary" disabled={busy}>
                {competition ? 'Yadda saxla' : 'Müsabiqəni yarat'}
              </button>
              <Link to="/admin/competitions" className="btn btn--ghost">
                Ləğv et
              </Link>
            </div>
          </form>

          {competition && (
            <div className="a-form a-split__aside">
              <StatusPanel competition={competition} onChange={changeStatus} />
              <CoverPanel
                title="Poster"
                hint="Ana səhifədə və müsabiqələr siyahısında bu afişa böyük göstərilir, heç vaxt kəsilmir. PNG, JPG və ya WEBP; şaquli, 1200×1600 ətrafında ən yaxşı oturur."
                image={competition.coverImage}
                preview={<img className="a-poster" src={competition.coverImage ?? undefined} alt="Hazırkı poster" />}
                upload={(file, onProgress) => adminApi.uploadCompetitionCover(competition.id, file, onProgress)}
                remove={() => adminApi.removeCompetitionCover(competition.id)}
                onChange={(c) => {
                  setCompetition(c)
                  setAuditKey((k) => k + 1)
                }}
              />
              <section className="a-panel" aria-labelledby="summary-title">
                <h2 id="summary-title" className="a-panel__title">
                  Hazırlıq
                </h2>
                <dl className="a-kv">
                  <div>
                    <dt>Forma</dt>
                    <dd>{competition.fieldCount > 0 ? `${competition.fieldCount} aktiv sahə` : 'Sahə yoxdur'}</dd>
                  </div>
                  {competition.hasCategories && (
                    <div>
                      <dt>Kateqoriyalar</dt>
                      <dd>
                        {competition.activeCategoryCount} aktiv / {competition.categoryCount}
                      </dd>
                    </div>
                  )}
                  <div>
                    <dt>Qaralamalar</dt>
                    <dd>{competition.draftCount}</dd>
                  </div>
                  <div>
                    <dt>Yenilənib</dt>
                    <dd className="tabular">{formatDateTime(competition.updatedAt)}</dd>
                  </div>
                </dl>
              </section>
              <section className="a-panel" aria-labelledby="delete-title">
                <h2 id="delete-title" className="a-panel__title">
                  Silmək
                </h2>
                <PermanentDelete
                  noun="müsabiqəni"
                  loadImpact={() => adminApi.competitionDeleteImpact(competition.id)}
                  onDelete={() => adminApi.deleteCompetitionPermanently(competition.id)}
                  onDeleted={() => navigate('/admin/competitions', { replace: true, state: { deleted: true } })}
                />
              </section>
            </div>
          )}
        </div>
      )}

      {competition && tab === 'categories' && (
        <CategoriesTab competition={competition} onChanged={refresh} />
      )}
      {competition && tab === 'form' && <FormBuilderTab competition={competition} onChanged={refresh} />}
      {competition && tab === 'voting' && <VotingTab competition={competition} />}
      {competition && tab === 'final' && <FinalTab competition={competition} />}
      {competition && tab === 'jury' && <JuryTab competition={competition} />}
      {competition && tab === 'results' && <ResultsTab competition={competition} onChanged={refresh} />}
      {competition && tab === 'history' && (
        <section className="a-panel">
          <AuditList entityType="Competition" entityId={competition.id} refreshKey={auditKey} />
        </section>
      )}
    </>
  )
}

function StatusPanel({ competition: c, onChange }: { competition: AdminCompetition; onChange: (s: CompetitionStatus) => Promise<void> }) {
  const explanation: Record<CompetitionStatus, string> = {
    Draft: 'Qaralama: saytda görünmür.',
    Published: 'Dərc olunub: saytda görünür, tarixlərə uyğun təklif qəbul edir.',
    Closed: 'Bağlanıb: saytda görünür, təklif qəbul etmir.',
    Archived: 'Arxivdədir: saytda görünmür, bütün təkliflər saxlanılır.',
  }
  return (
    <section className="a-panel" aria-labelledby="c-status-title">
      <div className="a-panel__head">
        <h2 id="c-status-title" className="a-panel__title">
          Status
        </h2>
        <CompetitionStatusBadge status={c.status} />
      </div>
      <p className="a-muted">{explanation[c.status]}</p>
      {c.status === 'Published' && c.projectStatus !== 'Published' && (
        <Notice tone="warn">Layihə dərc olunmayıb — müsabiqə saytda görünməyəcək.</Notice>
      )}
      <div className="a-actions">
        {c.status !== 'Published' && (
          <ConfirmButton label="Dərc et" confirmLabel="Bəli, dərc et" tone="primary" onConfirm={() => onChange('Published')} />
        )}
        {c.status === 'Published' && (
          <ConfirmButton label="Qəbulu bağla" confirmLabel="Bəli, bağla" onConfirm={() => onChange('Closed')} />
        )}
        {(c.status === 'Published' || c.status === 'Closed') && (
          <ConfirmButton label="Qaralamaya qaytar" confirmLabel="Bəli, saytdan götür" onConfirm={() => onChange('Draft')} />
        )}
        {c.status !== 'Archived' && (
          <ConfirmButton label="Arxivləşdir" confirmLabel="Bəli, arxivləşdir" onConfirm={() => onChange('Archived')} />
        )}
      </div>
    </section>
  )
}
