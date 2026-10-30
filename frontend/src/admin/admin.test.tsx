import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import type {
  AdminCategory,
  AdminCompetition,
  AdminParticipant,
  AdminField,
  AdminSubmissionDetail,
  AdminSubmissionRow,
  CompetitionInput,
  DeleteImpact,
  UploadPolicy,
} from './api'
import { RequireAdmin } from './AdminLayout'
import { VOTED_ACCOUNT } from './PermanentDelete'
import { fromBakuInput, move, parseCategoryImport, toBakuInput } from './lib'
import type { AdminResult, AdminWinner, AdminWinnerSlot } from './api'
import { CategoriesTab } from './pages/CategoriesTab'
import { ResultsTab } from './pages/ResultsTab'
import { FormBuilderTab } from './pages/FormBuilderTab'
import CompetitionEditPage from './pages/CompetitionEditPage'
import ParticipantsPage from './pages/ParticipantsPage'
import ProjectEditPage from './pages/ProjectEditPage'
import SubmissionDetailPage from './pages/SubmissionDetailPage'
import SubmissionsPage from './pages/SubmissionsPage'
import { slugify } from './slug'
import { hasErrors, validateCompetition, validateProject } from './validation'

const adminApi = vi.hoisted(() => ({
  audit: vi.fn(),
  participants: vi.fn(),
  competition: vi.fn(),
  uploadCompetitionCover: vi.fn(),
  removeCompetitionCover: vi.fn(),
  createProject: vi.fn(),
  project: vi.fn(),
  projects: vi.fn(),
  competitions: vi.fn(),
  categories: vi.fn(),
  importCategories: vi.fn(),
  createCategory: vi.fn(),
  fields: vi.fn(),
  createField: vi.fn(),
  uploadPolicy: vi.fn(),
  submissions: vi.fn(),
  submission: vi.fn(),
  submissionStatus: vi.fn(),
  addNote: vi.fn(),
  result: vi.fn(),
  selectWinner: vi.fn(),
  changeWinner: vi.fn(),
  removeWinner: vi.fn(),
  uploadResultCover: vi.fn(),
  removeResultCover: vi.fn(),
  publishResult: vi.fn(),
  unpublishResult: vi.fn(),
  resultAnnouncement: vi.fn(),
  setPreview: vi.fn(),
  setFileHidden: vi.fn(),
  setPdfPreviewPage: vi.fn(),
  publicAssetBlob: vi.fn(),
  projectDeleteImpact: vi.fn(),
  deleteProjectPermanently: vi.fn(),
  competitionDeleteImpact: vi.fn(),
  participantDeleteImpact: vi.fn(),
  deleteParticipantPermanently: vi.fn(),
  deleteCompetitionPermanently: vi.fn(),
  submissionDeleteImpact: vi.fn(),
  deleteSubmissionPermanently: vi.fn(),
  awards: vi.fn(),
  setAwards: vi.fn(),
  clearAwards: vi.fn(),
}))
vi.mock('./api', () => ({ adminApi }))

/** No podium recorded: the results tab's two-choice panel, empty. */
const noAwards = (competitionId = 5) => {
  const empty = (category: 'Jury' | 'Public') => ({ category, places: [], source: null, recordedAt: null, recordedBy: null })
  return { competitionId, resultPublished: false, voting: 'Closed', jury: empty('Jury'), public: empty('Public'), candidates: [] }
}

const auth = vi.hoisted(() => ({ value: { status: 'authenticated', user: { roles: ['Admin'] } } as Record<string, unknown> }))
vi.mock('../auth/AuthContext', () => ({ useAuth: () => auth.value }))
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

beforeEach(() => {
  adminApi.awards.mockResolvedValue(noAwards())
  vi.clearAllMocks()
  adminApi.audit.mockResolvedValue([])
})

function Where() {
  const l = useLocation()
  return <output data-testid="where">{l.pathname + l.search}</output>
}

const competition = (over: Partial<AdminCompetition> = {}): AdminCompetition => ({
  id: 5,
  projectId: 2,
  projectName: 'Laçın',
  projectStatus: 'Published',
  title: 'Laçın loqosu',
  slug: 'lacin-loqosu',
  shortDescription: 'Loqo',
  description: null,
  rules: null,
  requirements: null,
  nominations: null,
  judgingCriteria: null,
  usageTerms: null,
  contactNote: null,
  coverImage: null,
  startDate: null,
  endDate: null,
  resultsAnnouncedOn: null,
  exhibitionOn: null,
  prize: null,
  status: 'Draft',
  phase: 'Open',
  isAcceptingSubmissions: false,
  hasCategories: true,
  categoryLabel: 'Nominasiyalar',
  maxSubmissionsPerUser: null,
  sortOrder: 0,
  categoryCount: 0,
  activeCategoryCount: 0,
  fieldCount: 0,
  submissionCount: 0,
  draftCount: 0,
  createdAt: '2026-09-18T10:00:00Z',
  updatedAt: '2026-09-18T10:00:00Z',
  canDelete: true,
  resultState: 'NoWinner',
  isDemo: false,
  votingStartsAt: null,
  votingEndsAt: null,
  votingState: 'NotConfigured',
  ...over,
})

// ---- Pure helpers -------------------------------------------------------------------------------

describe('admin helpers', () => {
  it('slugifies Azerbaijani names like the server', () => {
    expect(slugify('Laçın')).toBe('lacin')
    expect(slugify('Bakı kəndləri — şevron')).toBe('baki-kendleri-sevron')
    expect(slugify('  Əmircan, Güzdək  ')).toBe('emircan-guzdek')
  })

  it('parses a pasted category list and flags duplicates before sending', () => {
    const { items, problems } = parseCategoryImport('Loqo\n\nPlakat; plakat-dizayni\nSuvenir\tsuvenir\tKiçik əşyalar\nLOQO\n; boş-ad')
    expect(items).toEqual([
      { name: 'Loqo' },
      { name: 'Plakat', slug: 'plakat-dizayni' },
      { name: 'Suvenir', slug: 'suvenir', description: 'Kiçik əşyalar' },
      { name: 'LOQO' },
      { name: '', slug: 'boş-ad' },
    ])
    expect(problems[3]).toMatch(/iki dəfə var \(1-ci sətir\)/)
    expect(problems[4]).toBe('Ad boşdur.')
    expect(problems[1]).toBeUndefined()
  })

  it('treats dotted and dotless I as Azerbaijani letters when comparing', () => {
    expect(parseCategoryImport('İçərişəhər\niçərişəhər').problems[1]).toBeDefined()
    expect(parseCategoryImport('Ilıq\nılıq').problems[1]).toBeDefined()
  })

  it('moves items up and down without mutating', () => {
    const list = ['a', 'b', 'c']
    expect(move(list, 2, -1)).toEqual(['a', 'c', 'b'])
    expect(move(list, 0, -1)).toBe(list)
    expect(list).toEqual(['a', 'b', 'c'])
  })

  it('converts datetime-local values to and from Baku time', () => {
    expect(toBakuInput('2026-12-31T19:59:00Z')).toBe('2026-12-31T23:59')
    expect(fromBakuInput('2026-12-31T23:59')).toBe('2026-12-31T23:59:00+04:00')
    expect(fromBakuInput('')).toBeNull()
  })

  it('validates project and competition forms without inventing defaults', () => {
    const project = validateProject({ name: ' ', slug: '', shortDescription: '', description: '', accentColor: 'red', sortOrder: 0 })
    expect(project.name).toBeTruthy()
    expect(project.shortDescription).toBeTruthy()
    expect(project.accentColor).toBeTruthy()

    const base = {
      projectId: 1,
      title: 'Laçın loqosu',
      slug: '',
      shortDescription: 'Qısa',
      description: '',
      rules: '',
      requirements: '',
      nominations: '',
    judgingCriteria: '',
    usageTerms: '',
      contactNote: '',
      startDate: null,
      endDate: null,
      resultsAnnouncedOn: null,
      exhibitionOn: null,
      prize: '',
      hasCategories: false,
      categoryLabel: '',
      maxSubmissionsPerUser: null,
      sortOrder: 0,
      votingStartsAt: null,
      votingEndsAt: null,
    }
    // Dates, prize and limit are all optional.
    expect(hasErrors(validateCompetition(base))).toBe(false)
    expect(validateCompetition({ ...base, startDate: '2026-10-02T00:00:00+04:00', endDate: '2026-10-01T00:00:00+04:00' }).endDate).toBeTruthy()
    expect(validateCompetition({ ...base, maxSubmissionsPerUser: 0 }).maxSubmissionsPerUser).toBeTruthy()
  })

  it('wants the voting window whole, and running forwards', () => {
    const base: CompetitionInput = {
      projectId: 2,
      title: 'Laçın loqosu',
      slug: 'lacin-logosu',
      shortDescription: 'Qısa',
      description: '',
      rules: '',
      requirements: '',
      nominations: '',
      judgingCriteria: '',
      usageTerms: '',
      contactNote: '',
      startDate: null,
      endDate: null,
      resultsAnnouncedOn: null,
      exhibitionOn: null,
      prize: '',
      hasCategories: false,
      categoryLabel: '',
      maxSubmissionsPerUser: null,
      sortOrder: 0,
      votingStartsAt: null,
      votingEndsAt: null,
    }
    const from = '2026-10-10T09:00:00+04:00'
    const to = '2026-10-20T21:00:00+04:00'

    // No window at all is the ordinary case, and a whole one is fine.
    expect(hasErrors(validateCompetition(base))).toBe(false)
    expect(hasErrors(validateCompetition({ ...base, votingStartsAt: from, votingEndsAt: to }))).toBe(false)

    // Half a window is refused from whichever side is missing, and a backwards one is refused outright.
    expect(validateCompetition({ ...base, votingStartsAt: from }).votingEndsAt).toBeTruthy()
    expect(validateCompetition({ ...base, votingEndsAt: to }).votingStartsAt).toBeTruthy()
    expect(validateCompetition({ ...base, votingStartsAt: to, votingEndsAt: from }).votingEndsAt).toBeTruthy()
    expect(validateCompetition({ ...base, votingStartsAt: from, votingEndsAt: from }).votingEndsAt).toBeTruthy()
  })
})

// ---- Route guard ---------------------------------------------------------------------------------

describe('RequireAdmin', () => {
  const renderGuard = () =>
    render(
      <MemoryRouter initialEntries={['/admin/submissions?status=Submitted']}>
        <Routes>
          <Route path="/admin/*" element={<RequireAdmin>Panel</RequireAdmin>} />
          <Route path="*" element={<Where />} />
        </Routes>
      </MemoryRouter>,
    )

  it('sends anonymous visitors to login and remembers where they were going', () => {
    auth.value = { status: 'anonymous', user: null }
    renderGuard()
    expect(screen.getByTestId('where')).toHaveTextContent('/login?next=%2Fadmin%2Fsubmissions%3Fstatus%3DSubmitted')
  })

  it('shows a denial to signed-in participants', () => {
    auth.value = { status: 'authenticated', user: { roles: [] } }
    renderGuard()
    expect(screen.getByRole('alert')).toHaveTextContent('yalnız administratorlar üçündür')
    expect(screen.queryByText('Panel')).not.toBeInTheDocument()
  })

  it('lets admins in', () => {
    auth.value = { status: 'authenticated', user: { roles: ['Admin'] } }
    renderGuard()
    expect(screen.getByText('Panel')).toBeInTheDocument()
  })
})

// ---- Project form --------------------------------------------------------------------------------

describe('ProjectEditPage (new)', () => {
  const renderNew = () =>
    render(
      <MemoryRouter initialEntries={['/admin/projects/new']}>
        <Routes>
          <Route path="/admin/projects/new" element={<ProjectEditPage />} />
          <Route path="/admin/projects/:id" element={<Where />} />
        </Routes>
      </MemoryRouter>,
    )

  it('blocks an empty form on the client', async () => {
    renderNew()
    await userEvent.click(await screen.findByRole('button', { name: 'Layihəni yarat' }))
    expect(screen.getByText('Layihənin adını yazın.')).toBeInTheDocument()
    expect(screen.getByText('Qısa təsvir yazın.')).toBeInTheDocument()
    expect(adminApi.createProject).not.toHaveBeenCalled()
  })

  it('derives the slug from the name and creates a draft', async () => {
    adminApi.createProject.mockResolvedValue({ id: 9 })
    renderNew()
    await userEvent.type(await screen.findByLabelText('Ad'), 'Laçın')
    expect(screen.getByLabelText('Ünvan (slug)')).toHaveValue('lacin')
    await userEvent.type(screen.getByLabelText('Qısa təsvir'), 'Sınaq')
    await userEvent.click(screen.getByRole('button', { name: 'Layihəni yarat' }))
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/admin/projects/9'))
    expect(adminApi.createProject).toHaveBeenCalledWith(expect.objectContaining({ name: 'Laçın', slug: 'lacin', shortDescription: 'Sınaq' }))
  })

  it('shows the server duplicate-slug error on the field', async () => {
    const { ApiError } = await import('../api/client')
    adminApi.createProject.mockRejectedValue(new ApiError(400, 'Yoxlayın.', { slug: ['Bu ünvan artıq istifadə olunur.'] }))
    renderNew()
    await userEvent.type(await screen.findByLabelText('Ad'), 'Bakı kəndləri')
    await userEvent.type(screen.getByLabelText('Qısa təsvir'), 'x')
    await userEvent.click(screen.getByRole('button', { name: 'Layihəni yarat' }))
    expect(await screen.findByText('Bu ünvan artıq istifadə olunur.')).toBeInTheDocument()
  })
})

// ---- Categories ----------------------------------------------------------------------------------

describe('CategoriesTab', () => {
  const cat = (id: number, name: string, over: Partial<AdminCategory> = {}): AdminCategory => ({
    id,
    competitionId: 5,
    name,
    slug: slugify(name),
    description: null,
    sortOrder: id,
    isActive: true,
    submissionCount: 0,
    ...over,
  })

  it('previews an import, blocks duplicates, then imports all at once', async () => {
    adminApi.categories.mockResolvedValue([cat(1, 'Loqo')])
    adminApi.importCategories.mockResolvedValue([cat(1, 'Loqo'), cat(2, 'Plakat'), cat(3, 'Mural')])
    const onChanged = vi.fn().mockResolvedValue(undefined)
    render(<CategoriesTab competition={competition({ categoryCount: 1 })} onChanged={onChanged} />)

    expect(await screen.findByText('Loqo')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Toplu idxal' }))
    const box = screen.getByLabelText('Hər sətirdə bir kateqoriya')
    await userEvent.type(box, 'Plakat{enter}plakat')
    expect(screen.getByText(/siyahıda iki dəfə var/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '2 kateqoriyanı əlavə et' })).toBeDisabled()

    await userEvent.clear(box)
    await userEvent.type(box, 'Plakat{enter}Mural')
    await userEvent.click(screen.getByRole('button', { name: '2 kateqoriyanı əlavə et' }))
    expect(adminApi.importCategories).toHaveBeenCalledWith(5, [{ name: 'Plakat' }, { name: 'Mural' }])
    expect(await screen.findByText('2 kateqoriya əlavə olundu.')).toBeInTheDocument()
    expect(screen.getByText('(3 aktiv / 3)')).toBeInTheDocument()
    expect(onChanged).toHaveBeenCalled()
  })

  it('maps server errors back to the offending lines and adds nothing', async () => {
    const { ApiError } = await import('../api/client')
    adminApi.categories.mockResolvedValue([cat(1, 'Loqo')])
    adminApi.importCategories.mockRejectedValue(new ApiError(400, 'Yoxlayın.', { 'items[0]': ['“Loqo” artıq var.'] }))
    render(<CategoriesTab competition={competition()} onChanged={vi.fn()} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Toplu idxal' }))
    await userEvent.type(screen.getByLabelText('Hər sətirdə bir kateqoriya'), 'Loqo{enter}Plakat')
    await userEvent.click(screen.getByRole('button', { name: '2 kateqoriyanı əlavə et' }))
    expect(await screen.findByText(/Heç nə əlavə olunmadı/)).toBeInTheDocument()
    expect(screen.getByText(/artıq var/)).toBeInTheDocument()
  })
})

// ---- Form builder --------------------------------------------------------------------------------

describe('FormBuilderTab', () => {
  const policy: UploadPolicy = {
    maxFileSizeMb: 10,
    maxFilesPerSubmission: 5,
    allowedTypes: [
      { key: 'png', label: 'PNG', extensions: ['.png'] },
      { key: 'pdf', label: 'PDF', extensions: ['.pdf'] },
    ],
  }
  const field = (over: Partial<AdminField>): AdminField => ({
    id: 1,
    competitionId: 5,
    key: 'title',
    label: 'İşin adı',
    fieldType: 'ShortText',
    placeholder: null,
    helpText: null,
    isRequired: true,
    isSubmissionTitle: true,
    isActive: true,
    isPublicAfterResults: false,
    optionsJson: null,
    sortOrder: 1,
    answerCount: 0,
    isLocked: false,
    ...over,
  })

  it('adds a file field that can only tighten the global upload policy', async () => {
    adminApi.fields.mockResolvedValueOnce([]).mockResolvedValue([field({ id: 2, key: 'files', label: 'Eskizlər', fieldType: 'File' })])
    adminApi.uploadPolicy.mockResolvedValue(policy)
    adminApi.createField.mockResolvedValue(field({ id: 2, key: 'files', label: 'Eskizlər', fieldType: 'File' }))
    render(<FormBuilderTab competition={competition()} onChanged={vi.fn().mockResolvedValue(undefined)} />)

    await userEvent.click(await screen.findByRole('button', { name: '+ Sahə əlavə et' }))
    const editor = screen.getByRole('form', { name: 'Yeni sahə' })
    await userEvent.click(within(editor).getByRole('button', { name: 'Sahəni əlavə et' }))
    expect(within(editor).getByText('Sahənin adını yazın.')).toBeInTheDocument()

    await userEvent.type(within(editor).getByLabelText('Sahənin adı'), 'Eskizlər')
    await userEvent.selectOptions(within(editor).getByLabelText('Növ'), 'File')
    expect(within(editor).getByText(/SVG təhlükəsizlik səbəbindən bağlıdır/)).toBeInTheDocument()
    const size = within(editor).getByLabelText('Maksimum ölçü (MB)')
    await userEvent.clear(size)
    await userEvent.type(size, '20')
    await userEvent.click(within(editor).getByRole('button', { name: 'Sahəni əlavə et' }))
    expect(within(editor).getByText('Fayl ölçüsü 1–10 MB olmalıdır.')).toBeInTheDocument()
    expect(adminApi.createField).not.toHaveBeenCalled()

    await userEvent.clear(size)
    await userEvent.type(size, '5')
    await userEvent.click(within(editor).getByLabelText('PDF'))
    await userEvent.click(within(editor).getByRole('button', { name: 'Sahəni əlavə et' }))
    await waitFor(() => expect(adminApi.createField).toHaveBeenCalled())
    const [, input] = adminApi.createField.mock.calls[0]
    expect(input).toMatchObject({ label: 'Eskizlər', fieldType: 'File', isSubmissionTitle: false })
    // A new upload field asks for one file unless the admin says otherwise: an entry is one work.
    expect(JSON.parse(input.optionsJson)).toEqual({ maxFiles: 1, maxFileSizeMb: 5, accept: ['png'] })
    expect(await screen.findByText('Sahə əlavə olundu.')).toBeInTheDocument()
  })

  it('locks the type and key of a field that already has answers', async () => {
    adminApi.fields.mockResolvedValue([field({ answerCount: 3, isLocked: true })])
    adminApi.uploadPolicy.mockResolvedValue(policy)
    render(<FormBuilderTab competition={competition()} onChanged={vi.fn()} />)
    expect(await screen.findByText('Cavablanıb: 3')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Redaktə' }))
    expect(screen.getByLabelText('Növ')).toBeDisabled()
    expect(screen.getByLabelText('Texniki açar')).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Sil' })).not.toBeInTheDocument()
  })
})

// ---- Submissions ---------------------------------------------------------------------------------

const row: AdminSubmissionRow = {
  id: 41,
  referenceNumber: 'SUB-2026-00041',
  title: 'Qartal və dağ',
  status: 'Submitted',
  submittedAt: '2026-09-18T10:00:00Z',
  participant: { id: 'u1', name: 'Aysel Qəhrəmanlı', email: 'aysel@example.test' },
  project: { id: 2, name: 'Laçın' },
  competition: { id: 5, name: 'Laçın loqosu' },
  category: { id: 7, name: 'Plakat' },
  fileCount: 2,
  isWinner: false,
  preview: null,
  isDemo: false,
}

describe('SubmissionsPage', () => {
  it('lists submissions and turns filters into URL state and API queries', async () => {
    adminApi.projects.mockResolvedValue([{ id: 2, name: 'Laçın' }])
    adminApi.competitions.mockResolvedValue([{ id: 5, title: 'Laçın loqosu', projectId: 2 }])
    adminApi.categories.mockResolvedValue([{ id: 7, name: 'Plakat' }])
    adminApi.submissions.mockResolvedValue({ items: [row], total: 1, page: 1, pageSize: 25 })

    render(
      <MemoryRouter initialEntries={['/admin/submissions']}>
        <Routes>
          <Route path="/admin/submissions" element={<><SubmissionsPage /><Where /></>} />
        </Routes>
      </MemoryRouter>,
    )
    expect(await screen.findByRole('link', { name: 'SUB-2026-00041' })).toHaveAttribute('href', '/admin/submissions/41')
    expect(screen.getByText('Aysel Qəhrəmanlı')).toBeInTheDocument()
    expect(screen.getByText('Plakat')).toBeInTheDocument()

    await userEvent.selectOptions(screen.getByLabelText('Status'), 'UnderReview')
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('status=UnderReview'))
    await waitFor(() => expect(adminApi.submissions).toHaveBeenLastCalledWith(expect.objectContaining({ status: 'UnderReview' }), expect.anything()))

    await userEvent.selectOptions(await screen.findByLabelText('Müsabiqə'), '5')
    await waitFor(() => expect(screen.getByLabelText('Kateqoriya')).toBeEnabled())
    await userEvent.selectOptions(screen.getByLabelText('Kateqoriya'), '7')
    await waitFor(() =>
      expect(adminApi.submissions).toHaveBeenLastCalledWith(expect.objectContaining({ competitionId: '5', categoryId: '7' }), expect.anything()),
    )

    await userEvent.type(screen.getByLabelText('Axtarış'), 'aysel')
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('q=aysel'))
  })
})

describe('SubmissionDetailPage', () => {
  const detail = (over: Partial<AdminSubmissionDetail> = {}): AdminSubmissionDetail => ({
    id: 41,
    referenceNumber: 'SUB-2026-00041',
    title: 'Qartal və dağ',
    status: 'Submitted',
    createdAt: '2026-09-18T09:00:00Z',
    updatedAt: '2026-09-18T10:00:00Z',
    submittedAt: '2026-09-18T10:00:00Z',
    participant: { id: 'u1', firstName: 'Aysel', lastName: 'Qəhrəmanlı', email: 'aysel@example.test', phone: null, registeredAt: '2026-09-18T08:00:00Z' },
    project: { id: 2, name: 'Laçın' },
    competition: { id: 5, name: 'Laçın loqosu' },
    competitionSlug: 'lacin-loqosu',
    categoryLabel: 'Nominasiyalar',
    category: { id: 7, name: 'Plakat' },
    answers: [
      { fieldId: 1, key: 'title', label: 'İşin adı', currentLabel: 'İşin adı', fieldType: 'ShortText', fieldActive: true, value: 'Qartal və dağ' },
      { fieldId: 2, key: 'idea', label: 'Konsept', currentLabel: 'Konseptin izahı', fieldType: 'LongText', fieldActive: false, value: 'Dağlar' },
    ],
    files: [{ id: 3, fieldKey: 'files', fieldLabel: 'Eskizlər', originalFileName: 'izah.pdf', contentType: 'application/pdf', sizeBytes: 2048, uploadedAt: '2026-09-18T09:30:00Z', isPublicField: false, publicAsset: 'Pending', publicAssetError: null, scanStatus: 'NotScanned', canBePreview: false, thumbnailReady: false, hiddenFromPublic: false }],
    notes: [],
    history: [{ from: 'Draft', to: 'Submitted', changedBy: 'Aysel Qəhrəmanlı', changedAt: '2026-09-18T10:00:00Z', comment: null }],
    allowedStatuses: ['UnderReview', 'Approved', 'Rejected'],
    result: { category: { id: 7, name: 'Plakat' }, isWinner: false, canBeWinner: true, ineligibleReason: null, currentWinner: null, resultPublished: false, resultPublicNow: false, place: null },
    previewFileId: null,
    effectivePreviewFileId: null,
    isDemo: false,
    ...over,
  })

  it('shows frozen answers and changes the status with an audit comment', async () => {
    adminApi.submission.mockResolvedValue(detail())
    adminApi.submissionStatus.mockResolvedValue(
      detail({
        status: 'UnderReview',
        history: [
          ...detail().history,
          { from: 'Submitted', to: 'UnderReview', changedBy: 'Admin', changedAt: '2026-09-18T11:00:00Z', comment: 'Baxılır' },
        ],
      }),
    )
    render(
      <MemoryRouter initialEntries={['/admin/submissions/41']}>
        <Routes>
          <Route path="/admin/submissions/:id" element={<SubmissionDetailPage />} />
        </Routes>
      </MemoryRouter>,
    )
    expect(await screen.findByRole('heading', { name: 'Qartal və dağ' })).toBeInTheDocument()
    expect(screen.getByText(/indiki adı: Konseptin izahı/)).toBeInTheDocument()
    expect(screen.getByText(/sahə deaktivdir/)).toBeInTheDocument()
    expect(screen.getByText('izah.pdf')).toBeInTheDocument()

    const button = screen.getByRole('button', { name: 'Statusu dəyiş' })
    expect(button).toBeDisabled()
    await userEvent.selectOptions(screen.getByLabelText('Yeni status'), 'UnderReview')
    await userEvent.type(screen.getByLabelText('Şərh (istəyə bağlı)'), 'Baxılır')
    await userEvent.click(button)
    expect(adminApi.submissionStatus).toHaveBeenCalledWith(41, 'UnderReview', 'Baxılır')
    expect(await screen.findByText('Status dəyişdi: Yoxlanılır.')).toBeInTheDocument()
    expect(screen.getByText('Göndərilib → Yoxlanılır')).toBeInTheDocument()
  })

  it('adds an internal note', async () => {
    adminApi.submission.mockResolvedValue(detail())
    adminApi.addNote.mockResolvedValue(detail({ notes: [{ id: 1, text: 'PDF yoxlanmalıdır.', authorName: 'Admin', createdAt: '2026-09-18T11:00:00Z' }] }))
    render(
      <MemoryRouter initialEntries={['/admin/submissions/41']}>
        <Routes>
          <Route path="/admin/submissions/:id" element={<SubmissionDetailPage />} />
        </Routes>
      </MemoryRouter>,
    )
    await userEvent.click(await screen.findByRole('button', { name: 'Qeydi əlavə et' }))
    expect(screen.getByText('Qeydin mətnini yazın.')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Yeni qeyd'), 'PDF yoxlanmalıdır.')
    await userEvent.click(screen.getByRole('button', { name: 'Qeydi əlavə et' }))
    expect(await screen.findByText('PDF yoxlanmalıdır.')).toBeInTheDocument()
    expect(adminApi.addNote).toHaveBeenCalledWith(41, 'PDF yoxlanmalıdır.')
  })
})

// ---- Winners per settlement & results (Phase 7) --------------------------------------------------

describe('winner selection on a submission (one winner per settlement)', () => {
  const base = (): AdminSubmissionDetail => ({
    id: 41,
    referenceNumber: 'SUB-2026-00041',
    title: 'Maştağa: nar və külək',
    status: 'UnderReview',
    createdAt: '2026-09-18T09:00:00Z',
    updatedAt: '2026-09-18T10:00:00Z',
    submittedAt: '2026-09-18T10:00:00Z',
    participant: { id: 'u1', firstName: 'Günel', lastName: 'Şükürova', email: 'g@example.test', phone: null, registeredAt: '2026-09-18T08:00:00Z' },
    project: { id: 1, name: 'Bakı kəndləri' },
    competition: { id: 7, name: 'Şevron' },
    competitionSlug: 'sevron',
    categoryLabel: 'Qəsəbələr',
    category: { id: 3, name: 'Maştağa' },
    answers: [],
    files: [],
    notes: [],
    history: [],
    allowedStatuses: ['Submitted', 'Approved', 'Rejected'],
    result: { category: { id: 3, name: 'Maştağa' }, isWinner: false, canBeWinner: true, ineligibleReason: null, currentWinner: null, resultPublished: false, resultPublicNow: false, place: null },
    previewFileId: null,
    effectivePreviewFileId: null,
    isDemo: false,
  })
  const withResult = (over: Partial<AdminSubmissionDetail['result']>): AdminSubmissionDetail => ({ ...base(), result: { ...base().result, ...over } })
  const current = { submissionId: 12, reference: 'SUB-2026-00012', title: 'Maştağa bağları', participantName: 'Rəşad Quliyev' }
  const renderDetail = () =>
    render(
      <MemoryRouter initialEntries={['/admin/submissions/41']}>
        <Routes>
          <Route path="/admin/submissions/:id" element={<SubmissionDetailPage />} />
        </Routes>
      </MemoryRouter>,
    )

  it('is a separate action, named after the settlement, not a moderation status', async () => {
    adminApi.submission.mockResolvedValue(base())
    renderDetail()
    const status = await screen.findByLabelText('Yeni status')
    expect(within(status).queryByRole('option', { name: /Qalib/ })).not.toBeInTheDocument()
    const panel = screen.getByRole('heading', { name: 'Müsabiqə nəticəsi' }).closest('section')!
    expect(within(panel).getByText('Qəsəbə')).toBeInTheDocument()
    expect(within(panel).getByText('Maştağa')).toBeInTheDocument()
    expect(within(panel).getByRole('button', { name: 'Maştağa üzrə qalib seç' })).toBeInTheDocument()
  })

  it('asks for confirmation showing reference, title, settlement and participant, then saves', async () => {
    adminApi.submission.mockResolvedValueOnce(base()).mockResolvedValue(withResult({ isWinner: true }))
    adminApi.selectWinner.mockResolvedValue({})
    renderDetail()
    await userEvent.click(await screen.findByRole('button', { name: 'Maştağa üzrə qalib seç' }))
    const dialog = screen.getByRole('dialog', { name: 'Bu işi Maştağa üzrə qalibi seçmək istəyirsiniz?' })
    for (const text of ['SUB-2026-00041', 'Maştağa: nar və külək', 'Maştağa', 'Günel Şükürova']) expect(within(dialog).getByText(text)).toBeInTheDocument()
    expect(within(dialog).queryByText(/dərhal görünəcək/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Ləğv et' })).toHaveFocus()

    await userEvent.click(within(dialog).getByRole('button', { name: 'Bəli, qalib seç' }))
    expect(adminApi.selectWinner).toHaveBeenCalledWith(7, 41, false)
    expect(await screen.findByText('Qalib seçildi. Nəticə hələ dərc olunmayıb.')).toBeInTheDocument()
    expect(screen.getByText('Bu iş Maştağa üzrə qalibdir.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('cancels with Escape without saving', async () => {
    adminApi.submission.mockResolvedValue(base())
    renderDetail()
    await userEvent.click(await screen.findByRole('button', { name: 'Maştağa üzrə qalib seç' }))
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(adminApi.selectWinner).not.toHaveBeenCalled()
  })

  it("protects the settlement's existing winner: no silent overwrite, an explicit 'Qalibi dəyiş'", async () => {
    adminApi.submission.mockResolvedValue(withResult({ currentWinner: current }))
    adminApi.changeWinner.mockResolvedValue({})
    renderDetail()
    expect(await screen.findByText(/Maştağa üzrə qalib artıq seçilib/)).toBeInTheDocument()
    expect(screen.getByText(/Rəşad Quliyev/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'SUB-2026-00012' })).toHaveAttribute('href', '/admin/submissions/12')
    expect(screen.queryByRole('button', { name: 'Maştağa üzrə qalib seç' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Qalibi dəyiş' }))
    const dialog = screen.getByRole('dialog', { name: 'Maştağa üzrə qalibi dəyişmək istəyirsiniz?' })
    expect(within(dialog).getByText('SUB-2026-00012')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bəli, qalibi dəyiş' }))
    expect(adminApi.changeWinner).toHaveBeenCalledWith(7, 41, 12, false)
  })

  it('once results are public, the dialog says the change goes public at once and the request confirms it', async () => {
    adminApi.submission.mockResolvedValue(withResult({ resultPublished: true, resultPublicNow: true }))
    adminApi.selectWinner.mockResolvedValue({})
    renderDetail()
    await userEvent.click(await screen.findByRole('button', { name: 'Maştağa üzrə qalib seç' }))
    const dialog = screen.getByRole('dialog', { name: 'Bu işi Maştağa üzrə qalibi seçmək istəyirsiniz?' })
    expect(within(dialog).getByText(/Nəticələr artıq dərc olunub: bu dəyişiklik saytda dərhal görünəcək/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bəli, qalib seç' }))
    expect(adminApi.selectWinner).toHaveBeenCalledWith(7, 41, true)
  })

  it('a published result that is hidden right now says so instead of "public at once"', async () => {
    adminApi.submission.mockResolvedValue(withResult({ isWinner: true, resultPublished: true, resultPublicNow: false }))
    renderDetail()
    expect(await screen.findByText(/Nəticələr dərc olunub, amma hazırda saytda görünmür/)).toBeInTheDocument()
    expect(screen.queryByText(/qalibin adı ictimaidir/)).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Qalibi ləğv et' }))
    const dialog = screen.getByRole('dialog', { name: 'Maştağa üzrə qalibi ləğv etmək istəyirsiniz?' })
    expect(within(dialog).getByText(/hazırda saytda görünmür/)).toBeInTheDocument()
  })

  it('a winner can be withdrawn, with its own confirmation', async () => {
    adminApi.submission.mockResolvedValueOnce(withResult({ isWinner: true })).mockResolvedValue(base())
    adminApi.removeWinner.mockResolvedValue({})
    renderDetail()
    expect(await screen.findByText('Bu iş Maştağa üzrə qalibdir.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Qalibi ləğv et' }))
    const dialog = screen.getByRole('dialog', { name: 'Maştağa üzrə qalibi ləğv etmək istəyirsiniz?' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bəli, ləğv et' }))
    expect(adminApi.removeWinner).toHaveBeenCalledWith(7, 41, false)
    expect(await screen.findByText('Qalib ləğv edildi. Nəticə hələ dərc olunmayıb.')).toBeInTheDocument()
  })

  it('shows the real state when another admin chose this settlement’s winner meanwhile (409)', async () => {
    const { ApiError } = await import('../api/client')
    adminApi.submission.mockResolvedValueOnce(base()).mockResolvedValue(withResult({ currentWinner: current }))
    adminApi.selectWinner.mockRejectedValue(new ApiError(409, 'Maştağa üzrə qalib artıq seçilib.'))
    renderDetail()
    await userEvent.click(await screen.findByRole('button', { name: 'Maştağa üzrə qalib seç' }))
    await userEvent.click(screen.getByRole('button', { name: 'Bəli, qalib seç' }))
    expect(await screen.findByText('Maştağa üzrə qalib artıq seçilib.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'SUB-2026-00012' })).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('explains why a rejected entry cannot win', async () => {
    adminApi.submission.mockResolvedValue({
      ...base(),
      status: 'Rejected',
      result: { ...base().result, canBeWinner: false, ineligibleReason: 'Rədd edilmiş təklif qalib seçilə bilməz.' },
    })
    renderDetail()
    expect(await screen.findByText('Rədd edilmiş təklif qalib seçilə bilməz.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /qalib seç/ })).not.toBeInTheDocument()
  })
})

describe('results tab (winners per settlement)', () => {
  const winner = (id: number, name: string, title: string, preview: AdminWinner['preview']): AdminWinner => ({
    submissionId: id,
    reference: `SUB-2026-000${id}`,
    title,
    participantName: name,
    status: 'Approved',
    preview,
    selectedAt: '2026-09-18T10:00:00Z',
    selectedBy: 'Admin',
  })
  const slots = (): AdminWinnerSlot[] => [
    { category: { id: 11, name: 'Balaxanı' }, categoryActive: true, eligibleCount: 2, winner: winner(41, 'Leyla Quliyeva', 'Balaxanı şevronu', { fileId: 90, thumbnailReady: true }) },
    { category: { id: 12, name: 'Biləcəri' }, categoryActive: true, eligibleCount: 2, winner: null },
    { category: { id: 13, name: 'Maştağa' }, categoryActive: true, eligibleCount: 3, winner: winner(52, 'Rəşad Quliyev', 'Maştağa bağları', null) },
    { category: { id: 14, name: 'Şüvəlan' }, categoryActive: true, eligibleCount: 0, winner: null },
  ]
  const result = (over: Partial<AdminResult> = {}): AdminResult => ({
    competitionId: 5,
    competitionTitle: 'Şevron',
    competitionSlug: 'sevron',
    hasCategories: true,
    categoryLabel: 'Qəsəbələr',
    state: 'Prepared',
    categoryCount: 4,
    winnerCount: 2,
    slots: slots(),
    publishedAt: null,
    publishedBy: null,
    announcement: null,
    coverImage: null,
    isPublicNow: false,
    publishProblems: [],
    eligibleCount: 7,
    publicFields: ['İşin adı'],
    hasPublicFileField: false,
    assetsPending: 0,
    assetFailures: [],
    places: [],
    placeCandidates: [],
    voting: 'NotConfigured',
    ...over,
  })
  const renderTab = () =>
    render(
      <MemoryRouter>
        <ResultsTab competition={competition()} onChanged={vi.fn().mockResolvedValue(undefined)} />
      </MemoryRouter>,
    )
  const rowOf = (name: string) => screen.getAllByRole('listitem').find((li) => li.classList.contains('a-slot') && within(li).queryByText(name))!

  it('shows every settlement with its winner, the counts, and a tolerant search and filter', async () => {
    adminApi.result.mockResolvedValue(result())
    renderTab()
    const balaxani = await waitFor(() => rowOf('Balaxanı'))
    expect(within(balaxani).getByText('Leyla Quliyeva')).toBeInTheDocument()
    expect(within(balaxani).getByRole('link', { name: 'SUB-2026-00041' })).toHaveAttribute('href', '/admin/submissions/41')
    expect(within(rowOf('Biləcəri')).getByText(/Qalib seçilməyib/)).toBeInTheDocument()
    expect(within(rowOf('Şüvəlan')).getByText(/iş yoxdur/)).toBeInTheDocument()
    expect(screen.getByText('4 qəsəbədən 2-si üzrə qalib seçilib. 2 qəsəbədə qalib yoxdur.')).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Qəsəbə axtar'), 'balaxani')
    expect(screen.getAllByRole('listitem').filter((li) => li.classList.contains('a-slot'))).toHaveLength(1)
    await userEvent.clear(screen.getByLabelText('Qəsəbə axtar'))
    await userEvent.click(screen.getByRole('radio', { name: /Qalib yoxdur/ }))
    const names = screen.getAllByRole('listitem').filter((li) => li.classList.contains('a-slot')).map((li) => li.querySelector('.a-slot__name')?.textContent)
    expect(names).toEqual(['Biləcəri', 'Şüvəlan'])
  })

  it('shows winners as small server-made copies, never by downloading the original', async () => {
    adminApi.result.mockResolvedValue(result())
    renderTab()
    await waitFor(() => expect(submissionsApi.thumbBlob).toHaveBeenCalledWith(41, 90, expect.anything()))
    expect(submissionsApi.fileBlob).not.toHaveBeenCalled()
    expect(within(rowOf('Maştağa')).getByText('Şəkil yoxdur')).toBeInTheDocument()
  })

  it('chooses a settlement’s winner among its eligible works, in place', async () => {
    adminApi.result.mockResolvedValue(result())
    adminApi.submissions.mockResolvedValue({
      items: [
        { ...row, id: 61, referenceNumber: 'SUB-2026-00061', title: 'Biləcəri qalxanı', status: 'Approved', participant: { id: 'u6', name: 'Tural Əliyev', email: null }, category: { id: 12, name: 'Biləcəri' }, preview: { fileId: 7, thumbnailReady: true } },
        { ...row, id: 62, referenceNumber: 'SUB-2026-00062', title: 'Rədd', status: 'Rejected', category: { id: 12, name: 'Biləcəri' } },
      ],
      total: 2,
      page: 1,
      pageSize: 100,
    })
    adminApi.selectWinner.mockResolvedValue(result({ winnerCount: 3 }))
    renderTab()
    await userEvent.click(await screen.findByRole('button', { name: 'Biləcəri üzrə qalib seç' }))
    const dialog = screen.getByRole('dialog', { name: 'Biləcəri üzrə qalib seçin' })
    expect(adminApi.submissions).toHaveBeenCalledWith(expect.objectContaining({ competitionId: '5', categoryId: '12' }), expect.anything())
    expect(await within(dialog).findByText('Tural Əliyev')).toBeInTheDocument()
    expect(within(dialog).queryByText('Rədd')).not.toBeInTheDocument()       // only eligible works
    expect(within(dialog).getByRole('button', { name: 'Qalib seç' })).toBeDisabled()
    await userEvent.click(within(dialog).getByRole('radio', { name: /Biləcəri qalxanı/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Qalib seç' }))
    expect(adminApi.selectWinner).toHaveBeenCalledWith(5, 61, false)
  })

  it('without any winner publishing is disabled', async () => {
    adminApi.result.mockResolvedValue(result({ state: 'NoWinner', winnerCount: 0, slots: slots().map((s) => ({ ...s, winner: null })) }))
    renderTab()
    expect(await screen.findByRole('button', { name: 'Nəticəni dərc et' })).toBeDisabled()
  })

  it('lists what blocks publication and warns that no file field is public', async () => {
    adminApi.result.mockResolvedValue(result({ publishProblems: ['Qəbul hələ açıqdır: nəticəni dərc etmək üçün əvvəlcə qəbulu bağlayın.'] }))
    renderTab()
    expect(await screen.findByText(/əvvəlcə qəbulu bağlayın/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Nəticəni dərc et' })).toBeDisabled()
    expect(screen.getByText(/Heç bir fayl sahəsi ictimai deyil/)).toBeInTheDocument()
  })

  it('publishes what exists after a confirmation that says how many settlements have winners', async () => {
    adminApi.result.mockResolvedValue(result())
    adminApi.publishResult.mockResolvedValue(result({ state: 'Published', publishedAt: '2026-09-18T12:00:00Z', publishedBy: 'Admin', isPublicNow: true }))
    renderTab()
    await userEvent.click(await screen.findByRole('button', { name: 'Nəticəni dərc et' }))
    const dialog = screen.getByRole('dialog', { name: 'Mövcud nəticələri dərc etmək istəyirsiniz?' })
    expect(within(dialog).getByText('4 qəsəbədən 2-si üzrə qalib seçilib.')).toBeInTheDocument()
    expect(within(dialog).getByText(/2 qəsəbədə qalib yoxdur/)).toBeInTheDocument()
    expect(within(dialog).getByText(/Qaliblərin adı və soyadı nəticə səhifəsində göstəriləcək/)).toBeInTheDocument()
    expect(adminApi.publishResult).not.toHaveBeenCalled()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bəli, dərc et' }))
    expect(adminApi.publishResult).toHaveBeenCalledWith(5)
    expect((await screen.findAllByText('Dərc olunub')).length).toBeGreaterThan(0)
    expect(screen.getByRole('link', { name: 'Nəticə səhifəsi' })).toHaveAttribute('href', '/competitions/sevron/results')
  })

  it('withdrawing a winner of a published result warns and confirms it to the server', async () => {
    adminApi.result.mockResolvedValue(result({ state: 'Published', publishedAt: '2026-09-18T12:00:00Z', isPublicNow: true }))
    adminApi.removeWinner.mockResolvedValue(result({ state: 'Published' }))
    renderTab()
    await userEvent.click(await screen.findByRole('button', { name: 'Balaxanı: qalibi ləğv et' }))
    const dialog = screen.getByRole('dialog', { name: 'Balaxanı üzrə qalibi ləğv etmək istəyirsiniz?' })
    expect(within(dialog).getByText(/dərhal görünəcək/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bəli, ləğv et' }))
    expect(adminApi.removeWinner).toHaveBeenCalledWith(5, 41, true)
  })

  it('results cover: previews the chosen picture before saving, then uploads it', async () => {
    adminApi.result.mockResolvedValue(result())
    adminApi.uploadResultCover.mockReturnValue({ promise: Promise.resolve(result({ coverImage: '/api/media/covers/2026/09/x.webp' })), abort: vi.fn() })
    renderTab()
    const panel = (await screen.findByRole('heading', { name: 'Nəticə cover şəkli' })).closest('section')!
    const file = new File(['x'], 'urra.png', { type: 'image/png' })
    await userEvent.upload(within(panel).getByLabelText('Nəticə cover şəklini seçin'), file)
    expect(within(panel).getByAltText('Seçilmiş şəklin önizləməsi')).toBeInTheDocument()
    expect(within(panel).getByText('Önizləmə — hələ yadda saxlanmayıb')).toBeInTheDocument()
    expect(adminApi.uploadResultCover).not.toHaveBeenCalled()
    await userEvent.click(within(panel).getByRole('button', { name: 'Yadda saxla' }))
    expect(adminApi.uploadResultCover).toHaveBeenCalledWith(5, file, expect.any(Function))
    expect(await within(panel).findByAltText('Hazırkı nəticə cover şəkli')).toHaveAttribute('src', '/api/media/covers/2026/09/x.webp')
  })
})

describe('submissions list winner indicator', () => {
  it('marks the winner and filters winners through the URL', async () => {
    adminApi.projects.mockResolvedValue([])
    adminApi.competitions.mockResolvedValue([])
    adminApi.submissions.mockResolvedValue({
      items: [{ ...row, isWinner: true }, { ...row, id: 42, referenceNumber: 'SUB-2026-00042' }],
      total: 2,
      page: 1,
      pageSize: 25,
    })
    render(
      <MemoryRouter initialEntries={['/admin/submissions']}>
        <Routes>
          <Route
            path="/admin/submissions"
            element={
              <>
                <SubmissionsPage />
                <Where />
              </>
            }
          />
        </Routes>
      </MemoryRouter>,
    )
    const winnerRow = (await screen.findByRole('link', { name: 'SUB-2026-00041' })).closest('tr')!
    expect(within(winnerRow).getByText('Qalib')).toBeInTheDocument()
    expect(within(screen.getByRole('link', { name: 'SUB-2026-00042' }).closest('tr')!).queryByText('Qalib')).not.toBeInTheDocument()
    await userEvent.selectOptions(screen.getByLabelText('Nəticə'), 'true')
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('winner=true'))
    await waitFor(() =>
      expect(adminApi.submissions).toHaveBeenLastCalledWith(expect.objectContaining({ winner: 'true' }), expect.anything()),
    )
  })
})

// ---- Public-safe files & preview (Phase 5) --------------------------------------------------------

describe('submission files: public-safe copies and gallery preview', () => {
  const file = (id: number, name: string, over: Partial<AdminSubmissionDetail['files'][number]> = {}): AdminSubmissionDetail['files'][number] => ({
    id,
    fieldKey: 'files',
    fieldLabel: 'Fayllar',
    originalFileName: name,
    contentType: name.endsWith('.pdf') ? 'application/pdf' : 'image/png',
    sizeBytes: 1000,
    uploadedAt: '2026-09-18T10:00:00Z',
    isPublicField: true,
    publicAsset: 'Ready',
    publicAssetError: null,
    scanStatus: 'NotScanned',
    canBePreview: true,
    hiddenFromPublic: false,
    thumbnailReady: true,
    ...over,
  })
  const detail = (over: Partial<AdminSubmissionDetail> = {}): AdminSubmissionDetail => ({
    id: 41,
    referenceNumber: 'SUB-2026-00041',
    title: 'İş',
    status: 'Submitted',
    createdAt: '2026-09-18T09:00:00Z',
    updatedAt: '2026-09-18T10:00:00Z',
    submittedAt: '2026-09-18T10:00:00Z',
    participant: { id: 'u1', firstName: 'Leyla', lastName: 'Əsgərova', email: 'l@example.test', phone: null, registeredAt: '2026-09-18T08:00:00Z' },
    project: { id: 1, name: 'Bakı kəndləri' },
    competition: { id: 7, name: 'Şevron' },
    competitionSlug: 'sevron',
    categoryLabel: 'Qəsəbələr',
    category: { id: 3, name: 'Maştağa' },
    answers: [],
    files: [
      file(1, 'telefon.jpg'),
      file(2, 'eskiz.png'),
      file(3, 'izah.pdf', { publicAsset: 'Failed', publicAssetError: 'pdf-unreadable', canBePreview: false }),
      file(4, 'gizli.png', { isPublicField: false, canBePreview: false }),
    ],
    notes: [],
    history: [],
    allowedStatuses: ['UnderReview'],
    result: { category: { id: 3, name: 'Maştağa' }, isWinner: false, canBeWinner: true, ineligibleReason: null, currentWinner: null, resultPublished: false, resultPublicNow: false, place: null },
    previewFileId: null,
    effectivePreviewFileId: 1,
    isDemo: false,
    ...over,
  })
  const renderDetail = () =>
    render(
      <MemoryRouter initialEntries={['/admin/submissions/41']}>
        <Routes>
          <Route path="/admin/submissions/:id" element={<SubmissionDetailPage />} />
        </Routes>
      </MemoryRouter>,
    )

  it('shows each file\'s public state honestly, including failures and the missing antivirus', async () => {
    adminApi.submission.mockResolvedValue(detail())
    renderDetail()
    const files = await screen.findByRole('heading', { name: /Fayllar/ })
    const panel = files.closest('section')!
    expect(within(panel).getAllByText('Təhlükəsiz nüsxə hazırdır')).toHaveLength(2)
    expect(within(panel).getByText(/Təhlükəsiz nüsxə yaradılmadı \(PDF oxunmadı\)/)).toBeInTheDocument()
    expect(within(panel).getByText(/Gizli: sahə nəticədən sonra göstərilmir/)).toBeInTheDocument()
    expect(within(panel).getByText(/Antivirus yoxlaması qoşulmayıb/)).toBeInTheDocument()
    // The automatic preview (first image) is marked; only other public-safe images offer "Önizləmə et".
    expect(within(panel).getAllByText('Önizləmə şəkli')).toHaveLength(1)
    expect(within(panel).getAllByRole('button', { name: 'Önizləmə et' })).toHaveLength(1)
  })

  it('draws file previews from the small server-made copies, never the originals', async () => {
    adminApi.submission.mockResolvedValue(detail({ files: [file(1, 'telefon.jpg'), file(2, 'eskiz.png', { thumbnailReady: false, publicAsset: 'Pending' })] }))
    renderDetail()
    await waitFor(() => expect(submissionsApi.thumbBlob).toHaveBeenCalledWith(41, 1, expect.anything()))
    expect(submissionsApi.thumbBlob).toHaveBeenCalledTimes(1)
    expect(submissionsApi.fileBlob).not.toHaveBeenCalled()
    expect(screen.getByText('Önizləmə hazırlanır')).toBeInTheDocument()
  })

  it('chooses another public-safe image as the gallery preview and can return to automatic', async () => {
    adminApi.submission.mockResolvedValue(detail())
    adminApi.setPreview.mockResolvedValueOnce(detail({ previewFileId: 2, effectivePreviewFileId: 2 })).mockResolvedValueOnce(detail())
    renderDetail()
    await userEvent.click(await screen.findByRole('button', { name: 'Önizləmə et' }))
    expect(adminApi.setPreview).toHaveBeenCalledWith(41, 2)
    await userEvent.click(await screen.findByRole('button', { name: 'Önizləməni avtomatik seçimə qaytar' }))
    expect(adminApi.setPreview).toHaveBeenLastCalledWith(41, null)
  })

  it('takes one file off the public pages after a confirmation, and can put it back', async () => {
    adminApi.submission.mockResolvedValue(detail())
    adminApi.setFileHidden.mockResolvedValueOnce(
      detail({ files: [file(1, 'telefon.jpg', { hiddenFromPublic: true, canBePreview: false }), file(2, 'eskiz.png'), file(3, 'izah.pdf', { publicAsset: 'Failed', publicAssetError: 'pdf-unreadable', canBePreview: false }), file(4, 'gizli.png', { isPublicField: false, canBePreview: false })], effectivePreviewFileId: 2 }),
    )
    renderDetail()
    // Only files the public could see can be hidden; a file of a private field has nothing to hide from.
    const hide = await screen.findAllByRole('button', { name: 'Saytda gizlət' })
    expect(hide).toHaveLength(3)
    await userEvent.click(hide[0])
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/önizləmə avtomatik olaraq növbəti faylla əvəz olunacaq/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bəli, gizlət' }))
    expect(adminApi.setFileHidden).toHaveBeenCalledWith(41, 1, true)
    expect(await screen.findByText('Saytda gizlədilib')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    adminApi.setFileHidden.mockResolvedValueOnce(detail())
    await userEvent.click(screen.getByRole('button', { name: 'Saytda göstər' }))
    expect(adminApi.setFileHidden).toHaveBeenLastCalledWith(41, 1, false)
  })

  it('picks the page that stands for a PDF from all its pages, shows it, and can give it back to automatic', async () => {
    // A six-page brand book: automatic is page 2 (the first is often a cover).
    const pdf = (over: Partial<AdminSubmissionDetail['files'][number]> = {}) =>
      file(5, 'brendbuk.pdf', { pdfPages: 6, previewPage: null, representativePage: 2, automaticPage: 2, ...over })
    adminApi.submission.mockResolvedValue(detail({ files: [pdf()], effectivePreviewFileId: 5 }))
    adminApi.setPdfPreviewPage
      .mockResolvedValueOnce(detail({ files: [pdf({ previewPage: 1, representativePage: 1 })], effectivePreviewFileId: 5 }))
      .mockResolvedValueOnce(detail({ files: [pdf()], effectivePreviewFileId: 5 }))
    renderDetail()

    // The file's card is drawn with that page and says which one it is, and that the choice is automatic.
    expect(await screen.findByText('PDF · 6 səhifə · Əsas önizləmə: səhifə 2')).toBeInTheDocument()
    expect(screen.getByText('(avtomatik)')).toBeInTheDocument()
    await waitFor(() => expect(submissionsApi.pageBlob).toHaveBeenCalledWith(41, 5, 2, expect.anything(), true))

    await userEvent.click(screen.getByRole('button', { name: 'Önizləmə səhifəsini seç' }))
    const dialog = await screen.findByRole('dialog', { name: 'Önizləmə səhifəsini seç' })
    const pages = within(dialog).getAllByRole('radio')
    expect(pages).toHaveLength(6)
    expect(within(dialog).getByRole('radio', { name: 'Səhifə 2 · avtomatik' })).toBeChecked()
    expect(within(dialog).getByText(/Əsas önizləmə: səhifə/)).toHaveTextContent('Əsas önizləmə: səhifə 2 (avtomatik seçim)')
    // Nothing to save until another page is chosen; nothing to undo while the choice is automatic.
    expect(within(dialog).getByRole('button', { name: 'Bu səhifəni seç' })).toBeDisabled()
    expect(within(dialog).queryByRole('button', { name: 'Avtomatik seçimə qaytar' })).not.toBeInTheDocument()

    await userEvent.click(within(dialog).getByRole('radio', { name: 'Səhifə 1' }))
    expect(within(dialog).getByText(/Əsas önizləmə: səhifə/)).toHaveTextContent('Əsas önizləmə: səhifə 1')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bu səhifəni seç' }))
    expect(adminApi.setPdfPreviewPage).toHaveBeenCalledWith(41, 5, 1)
    expect(await screen.findByText('PDF · 6 səhifə · Əsas önizləmə: səhifə 1')).toBeInTheDocument()
    expect(screen.getByText('(admin seçib)')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    // Back to automatic, from the same picker.
    await userEvent.click(screen.getByRole('button', { name: 'Önizləmə səhifəsini seç' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Avtomatik seçimə qaytar' }))
    expect(adminApi.setPdfPreviewPage).toHaveBeenLastCalledWith(41, 5, null)
    expect(await screen.findByText('PDF · 6 səhifə · Əsas önizləmə: səhifə 2')).toBeInTheDocument()
  })

  it('offers the page picker only for a PDF whose pages can be shown', async () => {
    adminApi.submission.mockResolvedValue(detail())
    renderDetail()
    await screen.findByRole('heading', { name: /Fayllar/ })
    expect(screen.queryByRole('button', { name: 'Önizləmə səhifəsini seç' })).not.toBeInTheDocument()
  })

  it('opens the metadata-free copy exactly as the public will see it', async () => {
    adminApi.submission.mockResolvedValue(detail())
    adminApi.publicAssetBlob.mockResolvedValue(new Blob(['x'], { type: 'image/webp' }))
    window.open = vi.fn()
    renderDetail()
    await userEvent.click((await screen.findAllByRole('button', { name: 'Təhlükəsiz nüsxəyə bax' }))[1])
    await waitFor(() => expect(adminApi.publicAssetBlob).toHaveBeenCalledWith(41, 2))
    expect(window.open).toHaveBeenCalled()
  })
})

// ---- Demo content (`seed-demo`) -------------------------------------------------------------------

describe('demo content in the admin', () => {
  it('filters submissions into real and demo, and marks demo rows', async () => {
    adminApi.projects.mockResolvedValue([])
    adminApi.competitions.mockResolvedValue([])
    adminApi.submissions.mockResolvedValue({
      items: [row, { ...row, id: 42, referenceNumber: 'SUB-2026-00042', isDemo: true }],
      total: 2,
      page: 1,
      pageSize: 25,
    })
    render(
      <MemoryRouter initialEntries={['/admin/submissions']}>
        <Routes>
          <Route path="/admin/submissions" element={<><SubmissionsPage /><Where /></>} />
        </Routes>
      </MemoryRouter>,
    )
    const demo = (await screen.findByRole('link', { name: 'SUB-2026-00042' })).closest('tr')!
    expect(within(demo).getByText('Nümunə')).toBeInTheDocument()
    const real = screen.getByRole('link', { name: 'SUB-2026-00041' }).closest('tr')!
    expect(within(real).queryByText('Nümunə')).not.toBeInTheDocument()

    await userEvent.selectOptions(screen.getByLabelText('Məlumat'), 'Real')
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('demo=false'))
    await waitFor(() => expect(adminApi.submissions).toHaveBeenLastCalledWith(expect.objectContaining({ demo: 'false' }), expect.anything()))
    await userEvent.selectOptions(screen.getByLabelText('Məlumat'), 'Nümunə məlumat')
    await waitFor(() => expect(adminApi.submissions).toHaveBeenLastCalledWith(expect.objectContaining({ demo: 'true' }), expect.anything()))
    await userEvent.selectOptions(screen.getByLabelText('Məlumat'), 'Hamısı')
    await waitFor(() => expect(screen.getByTestId('where')).not.toHaveTextContent('demo='))
  })

  it('filters participants into real and demo, and marks demo participants', async () => {
    const person = (id: string, name: string, isDemo: boolean) => ({
      id, name, email: `${id}@example.invalid`, phone: null, registeredAt: '2026-09-01T10:00:00Z', submissionCount: 1, isAdmin: false, isDemo, hasVotes: false, canDelete: false,
    })
    adminApi.participants.mockResolvedValue({
      items: [person('u1', 'Aysel Məmmədova', true), person('u2', 'Real İştirakçı', false)],
      total: 2,
      page: 1,
      pageSize: 25,
    })
    render(
      <MemoryRouter initialEntries={['/admin/participants']}>
        <Routes>
          <Route path="/admin/participants" element={<><ParticipantsPage /><Where /></>} />
        </Routes>
      </MemoryRouter>,
    )
    expect(within((await screen.findByText('Aysel Məmmədova')).closest('tr')!).getByText('Nümunə')).toBeInTheDocument()
    expect(within(screen.getByText('Real İştirakçı').closest('tr')!).queryByText('Nümunə')).not.toBeInTheDocument()
    expect(adminApi.participants).toHaveBeenLastCalledWith('', 1, undefined, expect.anything())

    await userEvent.selectOptions(screen.getByLabelText('Məlumat'), 'Nümunə məlumat')
    await waitFor(() => expect(adminApi.participants).toHaveBeenLastCalledWith('', 1, 'true', expect.anything()))
    expect(screen.getByTestId('where')).toHaveTextContent('demo=true')
  })

  it('never offers to delete an account that voted, and says why — the server refuses it too', async () => {
    const person = (id: string, name: string, over: Partial<AdminParticipant> = {}): AdminParticipant => ({
      id, name, email: `${id}@example.invalid`, phone: null, registeredAt: '2026-09-01T10:00:00Z', submissionCount: 0,
      isAdmin: false, isDemo: false, hasVotes: false, canDelete: true, ...over,
    })
    adminApi.participants.mockResolvedValue({
      items: [
        person('u1', 'Səs Verən', { hasVotes: true, canDelete: false }),
        person('u2', 'Boş Hesab'),
        person('u3', 'Köhnə Siyahı'), // the list was read before this account voted
      ],
      total: 3,
      page: 1,
      pageSize: 25,
    })
    adminApi.participantDeleteImpact.mockImplementation((id: string) =>
      id === 'u3'
        ? Promise.reject(new ApiError(409, VOTED_ACCOUNT))
        : Promise.resolve<DeleteImpact>({
            title: 'Boş Hesab', context: null, competitions: 0, categories: 0, fields: 0, submissions: 0, answers: 0,
            files: 0, results: 0, winners: 0, notes: 0, requiresTypedConfirmation: true,
          }),
    )
    render(
      <MemoryRouter initialEntries={['/admin/participants']}>
        <Routes>
          <Route path="/admin/participants" element={<ParticipantsPage />} />
        </Routes>
      </MemoryRouter>,
    )
    const voter = (await screen.findByText('Səs Verən')).closest('tr')!
    expect(within(voter).getByText('Bu hesab səsvermədə iştirak etdiyi üçün həmişəlik silinə bilməz.')).toBeInTheDocument()
    expect(within(voter).queryByRole('button', { name: 'Həmişəlik sil' })).not.toBeInTheDocument()
    const clean = screen.getByText('Boş Hesab').closest('tr')!
    expect(within(clean).getByRole('button', { name: 'Həmişəlik sil' })).toBeInTheDocument()
    expect(within(clean).queryByText(VOTED_ACCOUNT)).not.toBeInTheDocument()

    // A stale row: the dialog shows the server's refusal and offers nothing to confirm.
    await userEvent.click(within(screen.getByText('Köhnə Siyahı').closest('tr')!).getByRole('button', { name: 'Həmişəlik sil' }))
    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(VOTED_ACCOUNT)
    expect(within(dialog).getByRole('button', { name: 'Həmişəlik sil' })).toBeDisabled()
    expect(adminApi.deleteParticipantPermanently).not.toHaveBeenCalled()
  })

  it('uploads, shows and removes a competition poster', async () => {
    const poster = '/api/media/covers/2026/09/poster.webp'
    adminApi.competition.mockResolvedValue(competition())
    adminApi.projects.mockResolvedValue([{ id: 2, name: 'Laçın', status: 'Published' }])
    adminApi.uploadCompetitionCover.mockReturnValue({ promise: Promise.resolve(competition({ coverImage: poster })), abort: vi.fn() })
    adminApi.removeCompetitionCover.mockResolvedValue(competition({ coverImage: null }))
    render(
      <MemoryRouter initialEntries={['/admin/competitions/5']}>
        <Routes>
          <Route path="/admin/competitions/:id" element={<CompetitionEditPage />} />
        </Routes>
      </MemoryRouter>,
    )
    const panel = (await screen.findByRole('heading', { name: 'Poster' })).closest('section')!
    expect(within(panel).queryByRole('img')).not.toBeInTheDocument()
    const file = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], 'poster.png', { type: 'image/png' })
    await userEvent.upload(within(panel).getByLabelText('Poster seçin'), file)
    expect(adminApi.uploadCompetitionCover).toHaveBeenCalledWith(5, file, expect.any(Function))
    expect(await within(panel).findByRole('img', { name: 'Hazırkı poster' })).toHaveAttribute('src', poster)

    await userEvent.click(within(panel).getByRole('button', { name: 'Şəkli sil' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Bəli, sil' }))
    await waitFor(() => expect(within(panel).queryByRole('img')).not.toBeInTheDocument())
    expect(adminApi.removeCompetitionCover).toHaveBeenCalledWith(5)
  })

  it('marks a demo competition in its header', async () => {
    adminApi.competition.mockResolvedValue(competition({ isDemo: true }))
    adminApi.projects.mockResolvedValue([{ id: 2, name: 'Laçın', status: 'Published' }])
    render(
      <MemoryRouter initialEntries={['/admin/competitions/5']}>
        <Routes>
          <Route path="/admin/competitions/:id" element={<CompetitionEditPage />} />
        </Routes>
      </MemoryRouter>,
    )
    const header = (await screen.findByRole('heading', { level: 1 })).closest('header')!
    expect(within(header).getByText('Nümunə')).toBeInTheDocument()
  })
})

// ---- Permanent deletion -------------------------------------------------------------------------

describe('permanent delete', () => {
  const impact = (over: Partial<DeleteImpact> = {}): DeleteImpact => ({
    title: 'Laçın loqosu',
    context: 'Laçın',
    competitions: 0,
    categories: 3,
    fields: 5,
    submissions: 7,
    answers: 14,
    files: 9,
    results: 1,
    winners: 2,
    notes: 0,
    requiresTypedConfirmation: true,
    ...over,
  })

  const openCompetition = async () => {
    adminApi.competition.mockResolvedValue(competition())
    adminApi.projects.mockResolvedValue([{ id: 2, name: 'Laçın', status: 'Published' }])
    render(
      <MemoryRouter initialEntries={['/admin/competitions/5']}>
        <Routes>
          <Route path="/admin/competitions/:id" element={<CompetitionEditPage />} />
          <Route path="/admin/competitions" element={<Where />} />
        </Routes>
      </MemoryRouter>,
    )
    await userEvent.click(await screen.findByRole('button', { name: 'Həmişəlik sil' }))
    return await screen.findByRole('dialog')
  }

  it('shows what the server says would go, and asks for the word before enabling the button', async () => {
    adminApi.competitionDeleteImpact.mockResolvedValue(impact())
    const dialog = await openCompetition()

    expect(adminApi.competitionDeleteImpact).toHaveBeenCalledWith(5)
    expect(within(dialog).getByText('Laçın loqosu')).toBeInTheDocument()
    expect(within(dialog).getByText('7 təklif')).toBeInTheDocument()
    expect(within(dialog).getByText('9 yüklənmiş fayl')).toBeInTheDocument()
    expect(within(dialog).getByText('2 qalib qeydi')).toBeInTheDocument()
    expect(within(dialog).queryByText(/admin qeydi/)).not.toBeInTheDocument() // a zero is not listed
    expect(within(dialog).getByText('Bu əməliyyat geri qaytarıla bilməz.')).toBeInTheDocument()

    const confirm = within(dialog).getByRole('button', { name: 'Həmişəlik sil' })
    expect(confirm).toBeDisabled()
    await userEvent.type(within(dialog).getByLabelText(/SİL yazın/), 'sil')
    expect(confirm).toBeEnabled() // the word, whatever case it is typed in
    expect(adminApi.deleteCompetitionPermanently).not.toHaveBeenCalled()
  })

  it('cancelling deletes nothing and leaves the page where it was', async () => {
    adminApi.competitionDeleteImpact.mockResolvedValue(impact())
    const dialog = await openCompetition()

    await userEvent.click(within(dialog).getByRole('button', { name: 'Ləğv et' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(adminApi.deleteCompetitionPermanently).not.toHaveBeenCalled()
    expect(screen.getByRole('heading', { level: 1, name: /Laçın loqosu/ })).toBeInTheDocument()
  })

  it('deletes only after the server confirms, then leaves the page', async () => {
    adminApi.competitionDeleteImpact.mockResolvedValue(impact())
    adminApi.deleteCompetitionPermanently.mockResolvedValue({
      removed: impact(), storedFilesDeleted: 9, storedFilesFailed: 0,
    })
    const dialog = await openCompetition()

    await userEvent.type(within(dialog).getByLabelText(/SİL yazın/), 'SİL')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Həmişəlik sil' }))

    expect(adminApi.deleteCompetitionPermanently).toHaveBeenCalledWith(5)
    expect(await screen.findByTestId('where')).toHaveTextContent('/admin/competitions')
  })

  it('a failure keeps everything on screen and says why', async () => {
    adminApi.competitionDeleteImpact.mockResolvedValue(impact())
    adminApi.deleteCompetitionPermanently.mockRejectedValue(new ApiError(500, 'Silmək alınmadı.', {}))
    const dialog = await openCompetition()

    await userEvent.type(within(dialog).getByLabelText(/SİL yazın/), 'SİL')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Həmişəlik sil' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Silmək alınmadı.')
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    expect(screen.queryByTestId('where')).not.toBeInTheDocument() // still on the competition
  })

  it('asks for no typed word when nothing depends on it', async () => {
    adminApi.competitionDeleteImpact.mockResolvedValue(
      impact({ categories: 0, fields: 0, submissions: 0, answers: 0, files: 0, results: 0, winners: 0, requiresTypedConfirmation: false }),
    )
    const dialog = await openCompetition()

    expect(within(dialog).queryByLabelText(/SİL yazın/)).not.toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Həmişəlik sil' })).toBeEnabled()
    expect(within(dialog).getByText('Buna bağlı başqa məlumat yoxdur.')).toBeInTheDocument()
  })
})
