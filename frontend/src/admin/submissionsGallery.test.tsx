import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminFile, AdminSubmissionDetail, AdminSubmissionRow } from './api'
import SubmissionsPage from './pages/SubmissionsPage'

const adminApi = vi.hoisted(() => ({
  projects: vi.fn(),
  competitions: vi.fn(),
  categories: vi.fn(),
  submissions: vi.fn(),
  submission: vi.fn(),
  publicAssetBlob: vi.fn(),
}))
vi.mock('./api', () => ({ adminApi }))
vi.mock('../auth/AuthContext', () => ({ useAuth: () => ({ status: 'authenticated', user: { roles: ['Admin'] } }) }))
const submissionsApi = vi.hoisted(() => ({
  fileBlob: vi.fn(() => Promise.resolve(new Blob())),
  thumbBlob: vi.fn(() => Promise.resolve(new Blob())),
  pageBlob: vi.fn(() => Promise.resolve(new Blob())),
}))
vi.mock('../api/endpoints', () => ({ submissionsApi }))

beforeAll(() => {
  window.scrollTo = vi.fn()
  URL.createObjectURL = vi.fn(() => 'blob:x')
  URL.revokeObjectURL = vi.fn()
})

const row = (id: number, over: Partial<AdminSubmissionRow> = {}): AdminSubmissionRow => ({
  id,
  referenceNumber: `SUB-2026-000${id}`,
  title: `İş ${id}`,
  status: 'Submitted',
  submittedAt: '2026-09-25T10:00:00Z',
  participant: { id: `u${id}`, name: `İştirakçı ${id}`, email: `p${id}@example.test` },
  project: { id: 1, name: 'Abşeronun simvolları' },
  competition: { id: 3, name: 'Əmircanın simvolu' },
  category: null,
  fileCount: 1,
  isWinner: false,
  preview: { fileId: id * 10, thumbnailReady: true },
  isDemo: false,
  ...over,
})

const file = (id: number, name: string, over: Partial<AdminFile> = {}): AdminFile => ({
  id,
  fieldKey: 'files',
  fieldLabel: 'Fayllar',
  originalFileName: name,
  contentType: name.endsWith('.pdf') ? 'application/pdf' : 'image/png',
  sizeBytes: 1000,
  uploadedAt: '2026-09-25T09:00:00Z',
  isPublicField: true,
  publicAsset: 'Ready',
  publicAssetError: null,
  scanStatus: 'NotScanned',
  canBePreview: true,
  hiddenFromPublic: false,
  thumbnailReady: true,
  ...over,
})

const detail = (id: number, over: Partial<AdminSubmissionDetail> = {}): AdminSubmissionDetail => ({
  id,
  referenceNumber: `SUB-2026-000${id}`,
  title: `İş ${id}`,
  status: 'Submitted',
  createdAt: '2026-09-25T09:00:00Z',
  updatedAt: '2026-09-25T10:00:00Z',
  submittedAt: '2026-09-25T10:00:00Z',
  participant: { id: `u${id}`, firstName: 'İştirakçı', lastName: String(id), email: `p${id}@example.test`, phone: '+994501112233', registeredAt: '2026-09-20T08:00:00Z' },
  project: { id: 1, name: 'Abşeronun simvolları' },
  competition: { id: 3, name: 'Əmircanın simvolu' },
  competitionSlug: 'emircanin-simvolu',
  categoryLabel: null,
  category: null,
  answers: [
    { fieldId: 1, key: 'nomination', label: 'Nominasiya', currentLabel: 'Nominasiya', fieldType: 'Select', fieldActive: true, value: 'Simvol və Loqotip Dizaynı' },
    { fieldId: 2, key: 'concept', label: 'Konsepsiya', currentLabel: 'Konsepsiya', fieldType: 'LongText', fieldActive: true, value: `Konsept ${id}` },
    { fieldId: 3, key: 'note', label: 'Əlavə qeyd', currentLabel: 'Əlavə qeyd', fieldType: 'LongText', fieldActive: true, value: null },
  ],
  files: [file(id * 10, 'eskiz.png')],
  notes: [],
  history: [],
  allowedStatuses: ['UnderReview'],
  result: { category: null, isWinner: false, canBeWinner: true, ineligibleReason: null, currentWinner: null, resultPublished: false, resultPublicNow: false, place: null },
  previewFileId: null,
  effectivePreviewFileId: id * 10,
  isDemo: false,
  ...over,
})

function Where() {
  const l = useLocation()
  return <output data-testid="where">{l.pathname + l.search}</output>
}

function renderList(path = '/admin/submissions') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/admin/submissions" element={<><SubmissionsPage /><Where /></>} />
        <Route path="/admin/submissions/:id" element={<Where />} />
      </Routes>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  localStorage.clear()
  adminApi.projects.mockResolvedValue([])
  adminApi.competitions.mockResolvedValue([])
  adminApi.categories.mockResolvedValue([])
  adminApi.publicAssetBlob.mockResolvedValue(new Blob(['x'], { type: 'image/webp' }))
  adminApi.submission.mockImplementation((id: number) => Promise.resolve(detail(id)))
})

describe('submissions: pictures in the table', () => {
  it('draws each work from its small server copy — a picture, a PDF page, "not ready" and "no file" alike', async () => {
    adminApi.submissions.mockResolvedValue({
      items: [
        row(1),
        row(2, { preview: { fileId: 20, thumbnailReady: true } }), // a PDF's first page is its small copy too
        row(3, { preview: { fileId: 30, thumbnailReady: false } }),
        row(4, { preview: null, fileCount: 0 }),
        // A PDF the server pictures by a later page (the admin's choice, or the automatic page 2): that page's small copy.
        row(5, { preview: { fileId: 50, thumbnailReady: true, page: 3 } }),
      ],
      total: 5,
      page: 1,
      pageSize: 25,
    })
    renderList()

    expect(await screen.findByRole('columnheader', { name: 'Önizləmə' })).toBeInTheDocument()
    await waitFor(() => expect(submissionsApi.thumbBlob).toHaveBeenCalledTimes(2))
    expect(submissionsApi.thumbBlob).toHaveBeenCalledWith(1, 10, expect.anything())
    expect(submissionsApi.thumbBlob).toHaveBeenCalledWith(2, 20, expect.anything())
    await waitFor(() => expect(submissionsApi.pageBlob).toHaveBeenCalledWith(5, 50, 3, expect.anything(), true))
    // The originals are never fetched to draw a list.
    expect(submissionsApi.fileBlob).not.toHaveBeenCalled()
    expect(screen.getByText('Önizləmə hazırlanır')).toBeInTheDocument()
    expect(screen.getByText('Fayl yoxdur')).toBeInTheDocument()
  })
})

describe('submissions: gallery', () => {
  const page1 = { items: [row(1), row(2, { fileCount: 3 }), row(3)], total: 3, page: 1, pageSize: 25 }

  it('switches to cards with the few facts that tell works apart, and remembers the choice', async () => {
    adminApi.submissions.mockResolvedValue(page1)
    const { unmount } = renderList()

    await userEvent.click(await screen.findByRole('radio', { name: 'Qalereya' }))
    const gallery = screen.getByRole('list', { name: 'Təkliflər — qalereya' })
    const cards = within(gallery).getAllByRole('listitem')
    expect(cards).toHaveLength(3)
    const second = cards[1]
    expect(within(second).getByText('SUB-2026-0002')).toBeInTheDocument()
    expect(within(second).getByText('İş 2')).toBeInTheDocument()
    expect(within(second).getByText('İştirakçı 2')).toBeInTheDocument()
    expect(within(second).getByText('Əmircanın simvolu')).toBeInTheDocument()
    expect(within(second).getByText('3 fayl')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(localStorage.getItem('prizma.admin.submissions.view')).toBe('gallery')

    unmount()
    renderList()
    expect(await screen.findByRole('list', { name: 'Təkliflər — qalereya' })).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Qalereya' })).toBeChecked()
  })

  it('keeps every filter working in the gallery: the same URL, the same API query', async () => {
    localStorage.setItem('prizma.admin.submissions.view', 'gallery')
    adminApi.submissions.mockResolvedValue(page1)
    renderList()
    await screen.findByRole('list', { name: 'Təkliflər — qalereya' })

    await userEvent.selectOptions(screen.getByLabelText('Status'), 'UnderReview')
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('status=UnderReview'))
    await waitFor(() =>
      expect(adminApi.submissions).toHaveBeenLastCalledWith(expect.objectContaining({ status: 'UnderReview' }), expect.anything()),
    )
    await userEvent.type(screen.getByLabelText('Axtarış'), 'aysel')
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('q=aysel'))
    // Still the gallery after the filters changed.
    expect(await screen.findByRole('list', { name: 'Təkliflər — qalereya' })).toBeInTheDocument()
  })
})

describe('submissions: quick preview', () => {
  it('opens the work that was clicked, from its safe large copy, with everything a reviewer needs', async () => {
    adminApi.submissions.mockResolvedValue({ items: [row(1), row(2), row(3)], total: 3, page: 1, pageSize: 25 })
    renderList()

    await userEvent.click(await screen.findByRole('button', { name: 'SUB-2026-0002: önizləməni aç' }))
    const dialog = await screen.findByRole('dialog', { name: 'İş 2' })

    await waitFor(() => expect(adminApi.submission).toHaveBeenCalledWith(2, expect.anything()))
    await waitFor(() => expect(adminApi.publicAssetBlob).toHaveBeenCalledWith(2, 20, expect.anything()))
    expect(submissionsApi.fileBlob).not.toHaveBeenCalled()

    expect(await within(dialog).findByText('p2@example.test')).toBeInTheDocument()
    expect(within(dialog).getByText('+994501112233')).toBeInTheDocument()
    expect(within(dialog).getByText('Əmircanın simvolu')).toBeInTheDocument()
    expect(within(dialog).getByText('Simvol və Loqotip Dizaynı')).toBeInTheDocument()
    expect(within(dialog).getByText('Konsept 2')).toBeInTheDocument()
    // An empty answer is not a fact worth a line.
    expect(within(dialog).queryByText('Əlavə qeyd')).not.toBeInTheDocument()
    expect(within(dialog).getByText('2 / 3')).toBeInTheDocument()
    expect(within(dialog).getByRole('link', { name: 'Ətraflı səhifə' })).toHaveAttribute('href', '/admin/submissions/2')
  })

  it('steps through the page with the buttons and the arrow keys, and closes with Escape', async () => {
    adminApi.submissions.mockResolvedValue({ items: [row(1), row(2), row(3)], total: 3, page: 1, pageSize: 25 })
    renderList()

    await userEvent.click(await screen.findByRole('button', { name: 'SUB-2026-0001: önizləməni aç' }))
    expect(await screen.findByRole('dialog', { name: 'İş 1' })).toBeInTheDocument()
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: /Əvvəlki/ })).toBeDisabled()

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: /Növbəti/ }))
    expect(await screen.findByRole('dialog', { name: 'İş 2' })).toBeInTheDocument()

    await userEvent.keyboard('{ArrowRight}')
    expect(await screen.findByRole('dialog', { name: 'İş 3' })).toBeInTheDocument()
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: /Növbəti/ })).toBeDisabled()

    await userEvent.keyboard('{ArrowLeft}')
    expect(await screen.findByRole('dialog', { name: 'İş 2' })).toBeInTheDocument()

    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('carries on to the next page of results without closing', async () => {
    adminApi.submissions.mockImplementation((filters: { page?: string }) =>
      Promise.resolve(
        filters.page === '2'
          ? { items: [row(4), row(5)], total: 5, page: 2, pageSize: 3 }
          : { items: [row(1), row(2), row(3)], total: 5, page: 1, pageSize: 3 },
      ),
    )
    renderList()

    await userEvent.click(await screen.findByRole('button', { name: 'SUB-2026-0003: önizləməni aç' }))
    expect(await screen.findByRole('dialog', { name: 'İş 3' })).toBeInTheDocument()
    expect(screen.getByText('3 / 5')).toBeInTheDocument()

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: /Növbəti/ }))
    expect(await screen.findByRole('dialog', { name: 'İş 4' })).toBeInTheDocument()
    expect(screen.getByTestId('where')).toHaveTextContent('page=2')
    expect(await screen.findByText('4 / 5')).toBeInTheDocument()

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: /Əvvəlki/ }))
    expect(await screen.findByRole('dialog', { name: 'İş 3' })).toBeInTheDocument()
    expect(screen.getByTestId('where')).not.toHaveTextContent('page=2')
  })

  it('shows every file of a work and switches between them; a PDF is shown by its first page', async () => {
    adminApi.submissions.mockResolvedValue({ items: [row(7, { fileCount: 3, preview: { fileId: 71, thumbnailReady: true } })], total: 1, page: 1, pageSize: 25 })
    adminApi.submission.mockResolvedValue(
      detail(7, { files: [file(70, 'teqdimat.pdf'), file(71, 'eskiz.png'), file(72, 'maket.png', { publicAsset: 'Pending', thumbnailReady: false })] }),
    )
    renderList()

    await userEvent.click(await screen.findByRole('button', { name: 'SUB-2026-0007: önizləməni aç' }))
    const dialog = await screen.findByRole('dialog', { name: 'İş 7' })
    // It opens on the file the list showed, not on whatever came first.
    await waitFor(() => expect(adminApi.publicAssetBlob).toHaveBeenLastCalledWith(7, 71, expect.anything()))

    const strip = within(dialog).getByRole('list', { name: 'Fayllar' })
    const thumbs = within(strip).getAllByRole('button')
    expect(thumbs).toHaveLength(3)
    expect(thumbs[1]).toHaveAttribute('aria-pressed', 'true')

    await userEvent.click(thumbs[0])
    await waitFor(() => expect(adminApi.publicAssetBlob).toHaveBeenLastCalledWith(7, 70, expect.anything()))
    expect(within(dialog).getByText(/birinci səhifə göstərilir/)).toBeInTheDocument()

    // A file whose safe copy is still being made says so; the original is not fetched in its place.
    await userEvent.click(thumbs[2])
    expect(await within(dialog).findAllByText('Önizləmə hazırlanır')).not.toHaveLength(0)
    expect(submissionsApi.fileBlob).not.toHaveBeenCalled()
  })

  it('says plainly when a work has no file', async () => {
    adminApi.submissions.mockResolvedValue({ items: [row(9, { preview: null, fileCount: 0 })], total: 1, page: 1, pageSize: 25 })
    adminApi.submission.mockResolvedValue(detail(9, { files: [] }))
    renderList()

    await userEvent.click(await screen.findByRole('button', { name: 'SUB-2026-0009: önizləməni aç' }))
    const dialog = await screen.findByRole('dialog', { name: 'İş 9' })
    expect(await within(dialog).findByText('Bu təklifdə fayl yoxdur')).toBeInTheDocument()
    expect(adminApi.publicAssetBlob).not.toHaveBeenCalled()
  })
})
