import { useMemo, useState, type FormEvent } from 'react'
import { PageError, PageLoading } from '../../components/PageState'
import { useApi } from '../../lib/useApi'
import { matchesSearch } from '../../lib/text'
import { adminApi, type AdminCategory, type AdminCompetition } from '../api'
import { AField } from '../forms'
import { readErrors } from '../errors'
import { move, parseCategoryImport } from '../lib'
import { ConfirmButton, Empty, Notice } from '../ui'

/** Generic categories (settlements, genres, nominations…): add, edit, order, (de)activate, bulk import. */
export function CategoriesTab({ competition, onChanged }: { competition: AdminCompetition; onChanged: () => Promise<void> }) {
  const state = useApi((signal) => adminApi.categories(competition.id, signal), [competition.id])
  const [list, setList] = useState<AdminCategory[]>()
  const [query, setQuery] = useState('')
  const [message, setMessage] = useState<{ tone: 'error' | 'success'; text: string }>()
  const [importOpen, setImportOpen] = useState(false)
  const [editing, setEditing] = useState<number | null>(null)
  const categories = useMemo(() => list ?? state.data ?? [], [list, state.data])

  const visible = useMemo(() => categories.filter((c) => matchesSearch(c.name, query)), [categories, query])

  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />

  const apply = async (next: AdminCategory[], text?: string) => {
    setList(next)
    if (text) setMessage({ tone: 'success', text })
    await onChanged()
  }

  const fail = (err: unknown, fallback: string) => setMessage({ tone: 'error', text: readErrors(err, fallback).message })

  const reorder = async (index: number, delta: -1 | 1) => {
    const next = move(categories, index, delta)
    setList(next)
    try {
      setList(await adminApi.reorderCategories(competition.id, next.map((c) => c.id)))
    } catch (err) {
      setList(categories)
      fail(err, 'Sıra dəyişmədi.')
    }
  }

  const toggle = async (c: AdminCategory) => {
    try {
      const updated = await adminApi.updateCategory(competition.id, c.id, {
        name: c.name,
        slug: c.slug,
        description: c.description ?? undefined,
        isActive: !c.isActive,
      })
      await apply(categories.map((x) => (x.id === c.id ? updated : x)))
    } catch (err) {
      fail(err, 'Dəyişmədi.')
    }
  }

  return (
    <div className="a-form" style={{ gap: 'var(--s-5)' }}>
      {!competition.hasCategories && (
        <Notice tone="warn">
          Bu müsabiqədə kateqoriya seçimi söndürülüb. Siyahını hazırlaya bilərsiniz, amma iştirakçıya göstərilməsi üçün “Əsas
          məlumat” bölməsində seçimi aktiv edin.
        </Notice>
      )}
      {message && <Notice tone={message.tone}>{message.text}</Notice>}

      <section className="a-panel" aria-labelledby="cats-title">
        <div className="a-panel__head">
          <h2 id="cats-title" className="a-panel__title">
            {competition.categoryLabel || 'Kateqoriyalar'}{' '}
            <span className="a-muted tabular">
              ({categories.filter((c) => c.isActive).length} aktiv / {categories.length})
            </span>
          </h2>
          <div className="a-inline">
            <button type="button" className="btn btn--sm" onClick={() => setImportOpen((v) => !v)} aria-expanded={importOpen}>
              Toplu idxal
            </button>
          </div>
        </div>

        {importOpen && (
          <ImportPanel
            competitionId={competition.id}
            onDone={async (next, count) => {
              setImportOpen(false)
              await apply(next, `${count} kateqoriya əlavə olundu.`)
            }}
          />
        )}

        <AddCategory
          competitionId={competition.id}
          onAdded={async (c) => apply([...categories, c], `“${c.name}” əlavə olundu.`)}
        />

        {categories.length > 8 && (
          <AField label="Siyahıda axtar">
            <input className="input" type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder="məs. mastaga" />
          </AField>
        )}

        {categories.length === 0 ? (
          <Empty title="Hələ kateqoriya yoxdur.">
            <p className="a-muted">Bir-bir əlavə edin və ya “Toplu idxal” ilə bütün siyahını birdən yapışdırın.</p>
          </Empty>
        ) : (
          <table className="a-table a-table--stack">
            <thead>
              <tr>
                <th>Sıra</th>
                <th>Ad</th>
                <th>Ünvan</th>
                <th className="num">Təkliflər</th>
                <th>Aktiv</th>
                <th>
                  <span className="visually-hidden">Əməliyyatlar</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {visible.map((c) => {
                const index = categories.findIndex((x) => x.id === c.id)
                return editing === c.id ? (
                  <EditRow
                    key={c.id}
                    competitionId={competition.id}
                    category={c}
                    onCancel={() => setEditing(null)}
                    onSaved={async (u) => {
                      setEditing(null)
                      await apply(categories.map((x) => (x.id === u.id ? u : x)), 'Yadda saxlanıldı.')
                    }}
                  />
                ) : (
                  <tr key={c.id} className={c.isActive ? undefined : 'is-muted'}>
                    <td data-label="Sıra">
                      <span className="a-inline">
                        <span className="tabular a-muted">{String(index + 1).padStart(2, '0')}</span>
                        {!query && (
                          <span className="a-order">
                            <button type="button" aria-label={`${c.name}: yuxarı`} disabled={index === 0} onClick={() => reorder(index, -1)}>
                              ↑
                            </button>
                            <button
                              type="button"
                              aria-label={`${c.name}: aşağı`}
                              disabled={index === categories.length - 1}
                              onClick={() => reorder(index, 1)}
                            >
                              ↓
                            </button>
                          </span>
                        )}
                      </span>
                    </td>
                    <td data-label="Ad">
                      <span className="a-table__primary">{c.name}</span>
                      {c.description && <span className="a-table__sub">{c.description}</span>}
                    </td>
                    <td data-label="Ünvan" className="a-muted">
                      {c.slug}
                    </td>
                    <td data-label="Təkliflər" className="num">
                      {c.submissionCount}
                    </td>
                    <td data-label="Aktiv">
                      <label className="check" style={{ minHeight: 32 }}>
                        <input type="checkbox" checked={c.isActive} onChange={() => toggle(c)} aria-label={`${c.name} aktivdir`} />
                        <span className="visually-hidden">{c.isActive ? 'Aktiv' : 'Deaktiv'}</span>
                      </label>
                    </td>
                    <td data-label="">
                      <span className="a-inline">
                        <button type="button" className="btn btn--sm btn--ghost" onClick={() => setEditing(c.id)}>
                          Redaktə
                        </button>
                        {c.submissionCount === 0 && (
                          <ConfirmButton
                            label="Sil"
                            confirmLabel="Sil"
                            tone="danger"
                            onConfirm={async () => {
                              try {
                                await adminApi.deleteCategory(competition.id, c.id)
                                await apply(categories.filter((x) => x.id !== c.id), `“${c.name}” silindi.`)
                              } catch (err) {
                                fail(err, 'Silinmədi.')
                              }
                            }}
                          />
                        )}
                      </span>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        )}
      </section>
    </div>
  )
}

function AddCategory({ competitionId, onAdded }: { competitionId: number; onAdded: (c: AdminCategory) => Promise<void> }) {
  const [name, setName] = useState('')
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!name.trim()) return setError('Adı yazın.')
    setBusy(true)
    try {
      await onAdded(await adminApi.createCategory(competitionId, { name: name.trim(), isActive: true }))
      setName('')
      setError(undefined)
    } catch (err) {
      const { message, fields } = readErrors(err, 'Əlavə olunmadı.')
      setError(fields.name ?? fields.slug ?? message)
    } finally {
      setBusy(false)
    }
  }
  return (
    <form className="a-inline" onSubmit={submit} noValidate style={{ alignItems: 'flex-end' }}>
      <div style={{ flex: '1 1 240px' }}>
        <AField label="Yeni kateqoriya" error={error}>
          <input className="input" value={name} onChange={(e) => setName(e.target.value)} maxLength={160} />
        </AField>
      </div>
      <button type="submit" className="btn btn--primary btn--sm" disabled={busy} style={{ minHeight: 44 }}>
        Əlavə et
      </button>
    </form>
  )
}

function EditRow({
  competitionId,
  category,
  onCancel,
  onSaved,
}: {
  competitionId: number
  category: AdminCategory
  onCancel: () => void
  onSaved: (c: AdminCategory) => Promise<void>
}) {
  const [values, setValues] = useState({ name: category.name, slug: category.slug, description: category.description ?? '' })
  const [error, setError] = useState<string>()
  const save = async () => {
    try {
      await onSaved(await adminApi.updateCategory(competitionId, category.id, { ...values, isActive: category.isActive }))
    } catch (err) {
      const { message, fields } = readErrors(err, 'Yadda saxlanılmadı.')
      setError(fields.name ?? fields.slug ?? message)
    }
  }
  return (
    <tr>
      <td colSpan={6} data-label="">
        <div className="a-form" style={{ padding: 'var(--s-2) 0' }}>
          <div className="a-form__row a-form__row--3">
            <AField label="Ad" error={error}>
              <input className="input" value={values.name} onChange={(e) => setValues({ ...values, name: e.target.value })} />
            </AField>
            <AField label="Ünvan">
              <input className="input" value={values.slug} onChange={(e) => setValues({ ...values, slug: e.target.value })} spellCheck={false} />
            </AField>
            <AField label="Açıqlama (istəyə bağlı)">
              <input className="input" value={values.description} onChange={(e) => setValues({ ...values, description: e.target.value })} />
            </AField>
          </div>
          <div className="a-inline">
            <button type="button" className="btn btn--primary btn--sm" onClick={save}>
              Yadda saxla
            </button>
            <button type="button" className="btn btn--ghost btn--sm" onClick={onCancel}>
              Ləğv et
            </button>
          </div>
        </div>
      </td>
    </tr>
  )
}

function ImportPanel({ competitionId, onDone }: { competitionId: number; onDone: (next: AdminCategory[], count: number) => Promise<void> }) {
  const [text, setText] = useState('')
  const [serverErrors, setServerErrors] = useState<Record<number, string>>({})
  const [message, setMessage] = useState<string>()
  const [busy, setBusy] = useState(false)
  const parsed = useMemo(() => parseCategoryImport(text), [text])
  const problems = { ...parsed.problems, ...serverErrors }
  const problemCount = Object.keys(problems).length

  const submit = async () => {
    setServerErrors({})
    setMessage(undefined)
    setBusy(true)
    try {
      const next = await adminApi.importCategories(competitionId, parsed.items)
      await onDone(next, parsed.items.length)
    } catch (err) {
      const { message: text, lists } = readErrors(err, 'İdxal alınmadı.')
      const byLine: Record<number, string> = {}
      for (const [key, messages] of Object.entries(lists)) {
        const m = /^items\[(\d+)\]$/.exec(key)
        if (m) byLine[Number(m[1])] = messages[0]
      }
      setServerErrors(byLine)
      setMessage(Object.keys(byLine).length ? 'Heç nə əlavə olunmadı. Aşağıdakı sətirləri düzəldin:' : text)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="a-panel" style={{ background: 'var(--paper)' }}>
      <AField
        label="Hər sətirdə bir kateqoriya"
        hint="Format: Ad  —  və ya  Ad; ünvan  —  və ya  Ad; ünvan; açıqlama. Excel sütununu birbaşa yapışdırmaq olar. Ünvan boşdursa avtomatik yaradılır."
      >
        <textarea
          className="input"
          rows={8}
          value={text}
          onChange={(e) => {
            setText(e.target.value)
            setServerErrors({})
          }}
          placeholder={'Biləcəri\nBinəqədi\nXocəsən'}
        />
      </AField>
      {message && <Notice tone="error">{message}</Notice>}
      {parsed.items.length > 0 && (
        <>
          <p className="a-muted">
            {parsed.items.length} sətir tanındı{problemCount > 0 ? `, ${problemCount} problem` : ', problem yoxdur'}.
          </p>
          <ol className="a-muted" style={{ maxHeight: 220, overflowY: 'auto', margin: 0, paddingLeft: '2.2em', fontSize: 'var(--fs-200)' }}>
            {parsed.items.map((item, i) => (
              <li key={i} style={{ color: problems[i] ? 'var(--danger)' : undefined }}>
                <strong>{item.name || '(boş)'}</strong>
                {item.slug && <span> ({item.slug})</span>}
                {problems[i] && <span> — {problems[i]}</span>}
              </li>
            ))}
          </ol>
        </>
      )}
      <div className="a-inline">
        <button
          type="button"
          className="btn btn--primary btn--sm"
          disabled={busy || parsed.items.length === 0 || Object.keys(parsed.problems).length > 0}
          onClick={submit}
        >
          {parsed.items.length ? `${parsed.items.length} kateqoriyanı əlavə et` : 'Əlavə et'}
        </button>
      </div>
    </div>
  )
}
