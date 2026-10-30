import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { FormField, MySubmission, Submission } from '../../api/types'
import { FileField } from '../../components/submission/FileField'
import MySubmissionsPage from '../account/MySubmissionsPage'
import SubmissionPage from './SubmissionPage'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  save: vi.fn(),
  check: vi.fn(),
  submit: vi.fn(),
  upload: vi.fn(),
  removeFile: vi.fn(),
  fileBlob: vi.fn(),
  mySubmissions: vi.fn(),
}))

vi.mock('../../api/endpoints', () => ({
  submissionsApi: {
    get: api.get,
    save: api.save,
    check: api.check,
    submit: api.submit,
    upload: api.upload,
    removeFile: api.removeFile,
    fileBlob: api.fileBlob,
  },
  meApi: { submissions: api.mySubmissions },
}))

vi.mock('../../auth/AuthContext', () => ({
  useAuth: () => ({ user: { firstName: 'Nigar', lastName: 'Əliyeva' }, status: 'authenticated' }),
}))

beforeAll(() => {
  URL.createObjectURL = vi.fn(() => 'blob:preview')
  URL.revokeObjectURL = vi.fn()
  window.scrollTo = vi.fn()
  Element.prototype.scrollIntoView = vi.fn()
})

beforeEach(() => vi.clearAllMocks())

const fileRules = {
  maxFiles: 5,
  maxFileSizeBytes: 10 * 1024 * 1024,
  accept: [
    { key: 'png', label: 'PNG', mimeType: 'image/png', extensions: ['.png'] },
    { key: 'pdf', label: 'PDF', mimeType: 'application/pdf', extensions: ['.pdf'] },
  ],
}

const fields: FormField[] = [
  { id: 1, key: 'title', label: 'İşin adı', fieldType: 'ShortText', placeholder: null, helpText: null, isRequired: true, sortOrder: 1, options: null, maxLength: 150, files: null },
  { id: 2, key: 'concept', label: 'Konseptin izahı', fieldType: 'LongText', placeholder: null, helpText: null, isRequired: true, sortOrder: 2, options: null, maxLength: 5000, files: null },
  { id: 3, key: 'notes', label: 'Əlavə qeyd', fieldType: 'LongText', placeholder: null, helpText: null, isRequired: false, sortOrder: 3, options: null, maxLength: 5000, files: null },
  { id: 4, key: 'files', label: 'Fayllar', fieldType: 'File', placeholder: null, helpText: null, isRequired: true, sortOrder: 4, options: null, maxLength: null, files: fileRules },
]

const draft = (over: Partial<Submission> = {}): Submission => ({
  id: 7,
  status: 'Draft',
  referenceNumber: null,
  title: '',
  isEditable: true,
  createdAt: '2026-09-18T10:00:00Z',
  updatedAt: '2026-09-18T10:00:00Z',
  submittedAt: null,
  project: { slug: 'baki-kendleri', name: 'Bakı kəndləri', accentColor: '#56652A' },
  competition: {
    slug: 'baki-kendleri-sevron-dizayni',
    title: 'Bakı kəndləri üçün şevron dizayn müsabiqəsi',
    phase: 'Open',
    isAcceptingSubmissions: true,
    hasCategories: true,
    categoryLabel: 'Qəsəbələr',
  },
  category: { slug: 'mastaga', name: 'Maştağa' },
  fields,
  answers: { title: null, concept: null, notes: null },
  files: [],
  ...over,
})

const complete = draft({
  title: 'Maştağa: nar və külək',
  answers: { title: 'Maştağa: nar və külək', concept: 'Bağlar və Xəzər küləyi.', notes: null },
  files: [{ id: 11, fieldKey: 'files', originalFileName: 'eskiz.png', contentType: 'image/png', sizeBytes: 6000, uploadedAt: '2026-09-18T10:05:00Z' }],
})

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/submissions/:id" element={<SubmissionPage />} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('submission form', () => {
  it('blocks “Davam et” until required fields are filled, naming each field', async () => {
    api.get.mockResolvedValue(draft())
    renderAt('/submissions/7')
    await userEvent.click(await screen.findByRole('button', { name: 'Davam et' }))

    expect(await screen.findByText('“İşin adı” sahəsini doldurun.')).toBeInTheDocument()
    expect(screen.getByText('“Konseptin izahı” sahəsini doldurun.')).toBeInTheDocument()
    expect(screen.queryByText(/“Əlavə qeyd”/)).not.toBeInTheDocument()
    expect(screen.getByLabelText(/İşin adı/)).toHaveAttribute('aria-invalid', 'true')
    expect(api.save).not.toHaveBeenCalled()
  })

  it('shows the whole submission on the preview and submits it on purpose', async () => {
    api.get.mockResolvedValue(complete)
    api.check.mockResolvedValue(complete)
    api.fileBlob.mockResolvedValue(new Blob(['x'], { type: 'image/png' }))
    api.submit.mockResolvedValue({ ...complete, status: 'Submitted', isEditable: false, referenceNumber: 'SUB-2026-00001', submittedAt: '2026-09-18T10:10:00Z' })
    renderAt('/submissions/7?step=preview')

    expect(await screen.findByRole('heading', { name: 'Önizləmə' })).toBeInTheDocument()
    expect(screen.getByText('Qəsəbə')).toBeInTheDocument()
    expect(screen.getAllByText('Maştağa').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Maştağa: nar və külək').length).toBeGreaterThan(0)
    expect(screen.getByText('eskiz.png')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Geri qayıt və düzəliş et' })).toBeInTheDocument()

    const submit = screen.getByRole('button', { name: 'Təklifi təqdim et' })
    await waitFor(() => expect(submit).toBeEnabled())
    await userEvent.click(submit)

    expect(await screen.findByRole('heading', { name: 'Təklifiniz qəbul edildi.' })).toBeInTheDocument()
    expect(screen.getByText('SUB-2026-00001')).toBeInTheDocument()
    const receipt = within(document.querySelector('.receipt') as HTMLElement)
    expect(receipt.getByText('Maştağa')).toBeInTheDocument()
    expect(receipt.getByRole('link', { name: 'İşlərimə keç' })).toHaveAttribute('href', '/my/submissions')
    expect(receipt.getByRole('link', { name: 'Müsabiqəyə qayıt' })).toHaveAttribute('href', '/competitions/baki-kendleri-sevron-dizayni')
    expect(api.submit).toHaveBeenCalledTimes(1)
  })

  it('lists server-side problems on the preview and keeps the submit button disabled', async () => {
    api.get.mockResolvedValue(draft())
    const { ApiError } = await import('../../api/client')
    api.check.mockRejectedValue(new ApiError(400, 'Formada düzəliş tələb olunur.', { files: ['“Fayllar” üçün ən azı bir fayl əlavə edin.'] }))
    renderAt('/submissions/7?step=preview')

    expect(await screen.findByText('“Fayllar” üçün ən azı bir fayl əlavə edin.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Təklifi təqdim et' })).toBeDisabled()
  })

  it('opens submitted work read-only', async () => {
    api.get.mockResolvedValue({ ...complete, status: 'Submitted', isEditable: false, referenceNumber: 'SUB-2026-00002' })
    api.fileBlob.mockResolvedValue(new Blob(['x']))
    renderAt('/submissions/7')
    expect(await screen.findByText('Təklif #SUB-2026-00002')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Davam et' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Təklifi təqdim et' })).not.toBeInTheDocument()
  })
})

describe('file field feedback', () => {
  const setup = () => {
    const onUploaded = vi.fn()
    render(
      <FileField submissionId={7} field={fields[3]} files={[]} onUploaded={onUploaded} onRemoved={vi.fn()} />,
    )
    return { onUploaded, input: document.querySelector('input[type="file"]') as HTMLInputElement }
  }

  it('describes the policy and rejects an SVG without uploading it', async () => {
    const { input } = setup()
    // Built from the rules; number and unit are joined by a no-break space.
    expect(document.querySelector('.dropzone__rules')?.textContent).toBe('PNG və ya PDF. Hər fayl ən çox 10 MB, cəmi 5 fayl.')
    await userEvent.upload(input, new File(['<svg/>'], 'logo.svg', { type: 'image/svg+xml' }), { applyAccept: false })
    expect(await screen.findByText(/Bu format qəbul olunmur/)).toBeInTheDocument()
    expect(api.upload).not.toHaveBeenCalled()
  })

  it('rejects an oversized file with its size', async () => {
    const { input } = setup()
    const big = new File([new Uint8Array(10 * 1024 * 1024 + 1)], 'boyuk.png', { type: 'image/png' })
    await userEvent.upload(input, big)
    expect(await screen.findByText(/Fayl çox böyükdür/)).toBeInTheDocument()
    expect(api.upload).not.toHaveBeenCalled()
  })

  it('uploads an accepted file and reports it', async () => {
    const saved = { id: 12, fieldKey: 'files', originalFileName: 'eskiz.png', contentType: 'image/png', sizeBytes: 3, uploadedAt: '' }
    api.upload.mockReturnValue({ promise: Promise.resolve(saved), abort: vi.fn() })
    const { input, onUploaded } = setup()
    await userEvent.upload(input, new File(['png'], 'eskiz.png', { type: 'image/png' }))
    await waitFor(() => expect(onUploaded).toHaveBeenCalledWith(saved))
    expect(api.upload).toHaveBeenCalledWith(7, 'files', expect.any(File), expect.any(Function))
  })
})

describe('İşlərim', () => {
  const row = (over: Partial<MySubmission>): MySubmission => ({
    id: 1,
    referenceNumber: null,
    title: '',
    status: 'Draft',
    isEditable: true,
    submittedAt: null,
    createdAt: '2026-09-18T10:00:00Z',
    updatedAt: '2026-09-18T10:00:00Z',
    projectName: 'Bakı kəndləri',
    projectSlug: 'baki-kendleri',
    competitionTitle: 'Bakı kəndləri üçün şevron dizayn müsabiqəsi',
    competitionSlug: 'baki-kendleri-sevron-dizayni',
    categoryLabel: 'Qəsəbələr',
    categoryName: 'Maştağa',
    resultsPublished: false,
    isWinner: false,
    place: null,
    ...over,
  })

  it('offers “Davam et” for drafts and “Bax” for submitted work, with Azerbaijani statuses', async () => {
    api.mySubmissions.mockResolvedValue([
      row({ id: 1 }),
      row({ id: 2, status: 'Submitted', isEditable: false, referenceNumber: 'SUB-2026-00001', title: 'Maştağa: nar', submittedAt: '2026-09-18T11:00:00Z' }),
      row({ id: 3, status: 'Winner', isEditable: false, referenceNumber: 'SUB-2026-00002', title: 'Qalib iş' }),
    ])
    render(
      <MemoryRouter>
        <MySubmissionsPage />
      </MemoryRouter>,
    )

    expect(await screen.findByText('Təklif #SUB-2026-00001')).toBeInTheDocument()
    expect(screen.getByText('İstinad nömrəsi təqdimdən sonra verilir')).toBeInTheDocument()
    const statuses = [...document.querySelectorAll('.subs__status')].map((e) => e.textContent)
    expect(statuses).toEqual(['Qaralama', 'Göndərilib', 'Qalib'])
    expect(screen.getByRole('link', { name: /^Davam et/ })).toHaveAttribute('href', '/submissions/1')
    expect(screen.getByRole('link', { name: 'Bax: Maştağa: nar' })).toHaveAttribute('href', '/submissions/2')
  })

  it('invites to the open competitions when empty', async () => {
    api.mySubmissions.mockResolvedValue([])
    render(
      <MemoryRouter>
        <MySubmissionsPage />
      </MemoryRouter>,
    )
    expect(await screen.findByText('Hələ təklifiniz yoxdur')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Açıq müsabiqələrə bax' })).toBeInTheDocument()
  })
})
