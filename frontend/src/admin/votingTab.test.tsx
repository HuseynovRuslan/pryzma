import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminCompetition, AdminPrintItem, AdminVoteSuspicion, AdminVoteWork, AdminVoting } from './api'
import { VotingTab } from './pages/VotingTab'

const adminApi = vi.hoisted(() => ({
  voting: vi.fn(),
  voteSuspicion: vi.fn(),
  workMaterial: vi.fn(),
  publicAssetBlob: vi.fn(),
  documentPageBlob: vi.fn(),
}))
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  adminApi,
}))
const submissionsApi = vi.hoisted(() => ({
  thumbBlob: vi.fn(() => Promise.resolve(new Blob())),
  pageBlob: vi.fn(() => Promise.resolve(new Blob())),
}))
vi.mock('../api/endpoints', () => ({ submissionsApi }))

beforeAll(() => {
  URL.createObjectURL = vi.fn(() => 'blob:work')
  URL.revokeObjectURL = vi.fn()
})

beforeEach(() => {
  vi.clearAllMocks()
  adminApi.publicAssetBlob.mockResolvedValue(new Blob())
  adminApi.documentPageBlob.mockResolvedValue(new Blob())
})

const competition = { id: 5, title: 'Əmircanın simvolu' } as AdminCompetition

const work = (over: Partial<AdminVoteWork> = {}): AdminVoteWork => ({
  submissionId: 1,
  reference: 'SUB-2026-00001',
  title: 'Əmircanın Ruhu',
  participantName: 'Nigar Həsənova',
  status: 'Submitted',
  voteCount: 12,
  place: 1,
  isWinner: false,
  lateVoteCount: 0,
  invalidatedVoteCount: 0,
  ...over,
})

const tally = (over: Partial<AdminVoting> = {}): AdminVoting => ({
  state: 'Open',
  startsAt: '2026-10-10T05:00:00Z',
  endsAt: '2026-10-20T17:00:00Z',
  voterCount: 14,
  voteCount: 18,
  lateVoterCount: 0,
  nominations: [
    {
      categoryId: 1,
      slug: 'simvol-loqotip',
      name: 'Simvol və Loqotip Dizaynı',
      isActive: true,
      workCount: 2,
      voteCount: 18,
      isTie: false,
      works: [work(), work({ submissionId: 2, reference: 'SUB-2026-00002', title: 'Tarixi Körpü', participantName: 'Anar Əliyev', voteCount: 6, place: 2 })],
    },
  ],
  unplaced: [],
  invalidatedVoteCount: 0,
  ...over,
})

const quiet: AdminVoteSuspicion = { voteCount: 0, high: 0, medium: 0, low: 0, works: [], votes: [], invalidatedCount: 0 }

const show = (v: AdminVoting, suspicion: AdminVoteSuspicion = quiet) => {
  adminApi.voting.mockResolvedValue(v)
  adminApi.voteSuspicion.mockResolvedValue(suspicion)
  return render(
    <MemoryRouter>
      <VotingTab competition={competition} />
    </MemoryRouter>,
  )
}

describe('the vote, for the administration', () => {
  it('scores suspicious votes with their reasons, sums them per work, and folds the milder ones away', async () => {
    show(tally(), {
      voteCount: 18,
      high: 1,
      medium: 1,
      low: 16,
      works: [
        { submissionId: 1, reference: 'SUB-2026-00001', title: 'Əmircanın Ruhu', votes: 12, high: 1, medium: 1, low: 10, expectedFake: 1.6, cleanEstimate: 10.4, rawVotes: 12, invalidatedVotes: 0, phoneClusterVotes: 1 },
      ],
      votes: [
        {
          score: 99, submissionId: 1, reference: 'SUB-2026-00001', voterName: 'Tofiq Vəliyev',
          signedUpAt: '2026-10-04T12:45:00Z', votedAt: '2026-10-04T12:46:00Z',
          reasons: ['Telefonu işin müəllifi ilə eynidir — öz işinə ikinci hesabla səs ola bilər', 'Hesab səsvermə başlayandan sonra açılıb'],
          voteId: 501, evidence: 'PhoneCluster',
        },
        {
          score: 35, submissionId: 1, reference: 'SUB-2026-00001', voterName: 'Aynur Ələsgərova',
          signedUpAt: '2026-10-04T12:50:00Z', votedAt: '2026-10-04T12:51:00Z',
          reasons: ['Bu işə eyni IP-dən daha 3 hesab səs verib'],
          voteId: 502, evidence: 'Weak',
        },
      ],
      invalidatedCount: 0,
    })

    const panel = (await screen.findByRole('heading', { name: 'Şübhəli səslər' })).closest('section')!
    expect(within(panel).getByText(/ölçülmüş ehtimal və ya sübut deyil/)).toBeInTheDocument()
    expect(within(panel).getByText('~10,4')).toBeInTheDocument()
    expect(within(panel).getByText('Yüksək şübhəli səslər (1)')).toBeInTheDocument()
    expect(within(panel).getByText('99%')).toBeInTheDocument()
    expect(within(panel).getByText('Telefonu işin müəllifi ilə eynidir — öz işinə ikinci hesabla səs ola bilər')).toBeVisible()
    const milder = within(panel).getByText('Orta şübhəli səslər (1)').closest('details')!
    expect(milder).not.toHaveAttribute('open')
    expect(within(milder).getByText('Aynur Ələsgərova')).toBeInTheDocument()
  })


  it('counts the votes and the voters, and names the participants but never a voter', async () => {
    show(tally())

    expect(await screen.findByText('Simvol və Loqotip Dizaynı')).toBeInTheDocument()
    const rows = screen.getAllByRole('row').slice(1)
    expect(within(rows[0]).getByText('Əmircanın Ruhu')).toBeInTheDocument()
    expect(within(rows[0]).getByText('Nigar Həsənova')).toBeInTheDocument()
    expect(within(rows[0]).getByText('12')).toBeInTheDocument()
    expect(within(rows[0]).getByText('Ən çox səs')).toBeInTheDocument()
    // A work with fewer votes is second, not hidden.
    expect(within(rows[1]).getByText('Tarixi Körpü')).toBeInTheDocument()
    expect(screen.getByText('səsverən')).toBeInTheDocument()
  })

  it('says how many votes came from accounts opened after voting began, overall and per work', async () => {
    const late = tally()
    late.lateVoterCount = 3
    late.nominations[0].works[0] = work({ lateVoteCount: 5 })
    show(late)

    expect(await screen.findByText(/hesabını səsvermə başlayandan sonra açıb/)).toHaveTextContent('Səs verənlərdən 3 nəfər')
    const rows = screen.getAllByRole('row').slice(1)
    expect(within(rows[0]).getByText('5-i yeni hesabdan')).toBeInTheDocument()
    expect(within(rows[1]).queryByText(/yeni hesabdan/)).not.toBeInTheDocument()
  })

  it('shows a tie as a tie and implies no winner', async () => {
    show(
      tally({
        nominations: [
          {
            categoryId: 1,
            slug: 'simvol-loqotip',
            name: 'Simvol və Loqotip Dizaynı',
            isActive: true,
            workCount: 2,
            voteCount: 24,
            isTie: true,
            works: [work({ voteCount: 12 }), work({ submissionId: 2, reference: 'SUB-2026-00002', title: 'Tarixi Körpü', voteCount: 12, place: 1 })],
          },
        ],
      }),
    )

    // Once at the top, as a warning; once beside the nomination it belongs to.
    expect(await screen.findByText(/Qalibi say deyil/)).toBeInTheDocument()
    expect(screen.getAllByText(/səslər bərabərdir/)).toHaveLength(2)
    expect(screen.getAllByText('Bərabər')).toHaveLength(2)
    expect(screen.queryByText('Ən çox səs')).not.toBeInTheDocument()
  })

  it('names the works no nomination holds, because nobody can vote for them', async () => {
    show(tally({ unplaced: [work({ submissionId: 9, reference: 'SUB-2026-00009', title: 'Yersiz iş', place: 0, voteCount: 0 })] }))

    expect(await screen.findByText(/heç bir nominasiyaya aid deyil/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /SUB-2026-00009/ })).toHaveAttribute('href', '/admin/submissions/9')
  })

  it('makes each ranked work reviewable: picture, PDF badge, the official place apart from the rank, and "İşə bax"', async () => {
    const v = tally()
    v.nominations[0].works = [
      work({ image: { fileId: 10, large: true, width: null, height: null, page: 2, pages: 4 }, pdfPages: 4, officialPlace: 1, isWinner: true }),
      work({ submissionId: 2, reference: 'SUB-2026-00002', title: 'Tarixi Körpü', voteCount: 6, place: 2, image: { fileId: 20, large: true, width: 800, height: 600 } }),
    ]
    show(v)

    const table = await screen.findByRole('table')
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Sıra', 'İş', 'İştirakçı', 'Səs', 'Yer', 'Baxış'])
    const [first, second] = within(table).getAllByRole('row').slice(1)
    // The vote's rank and the official place are two different columns.
    expect(within(first).getAllByRole('cell')[0]).toHaveTextContent('1')
    expect(within(first).getByText('1-ci yer')).toBeInTheDocument()
    expect(within(second).getByLabelText('Rəsmi yer yoxdur')).toHaveTextContent('—')
    // A PDF says so, and which of its pages the thumbnail is — the page the server chose.
    expect(within(first).getByText('PDF · 4 səhifə')).toBeInTheDocument()
    expect(within(first).getByText('Önizləmə: 2/4')).toBeInTheDocument()
    expect(within(second).queryByText(/PDF ·/)).not.toBeInTheDocument()
    await waitFor(() => expect(submissionsApi.pageBlob).toHaveBeenCalledWith(1, 10, 2, expect.anything(), true))
    expect(submissionsApi.thumbBlob).toHaveBeenCalledWith(2, 20, expect.anything())
    // The picture, the title and the reference open the work, as does "İşə bax".
    expect(within(first).getByRole('button', { name: 'SUB-2026-00001 — Əmircanın Ruhu: işə bax' })).toBeInTheDocument()
    expect(within(first).getByRole('button', { name: 'İşə bax: SUB-2026-00001' })).toBeInTheDocument()
  })

  it('opens the whole work over the ranking, steps through it in ranking order, and gives the table back as it was', async () => {
    const v = tally()
    v.nominations[0].works = [
      work({ image: { fileId: 10, large: true, width: null, height: null, page: 2, pages: 4 }, pdfPages: 4, invalidatedVoteCount: 3 }),
      work({ submissionId: 2, reference: 'SUB-2026-00002', title: 'Tarixi Körpü', voteCount: 6, place: 2 }),
    ]
    const material = (id: number): AdminPrintItem => ({
      id,
      reference: `SUB-2026-0000${id}`,
      title: id === 1 ? 'Əmircanın Ruhu' : 'Tarixi Körpü',
      category: null,
      facts: [{ label: 'Nominasiya', value: 'Simvol və Loqotip Dizaynı' }],
      conceptLabel: 'Konseptin izahı',
      concept: 'Qala, dəniz və neft.\nİkinci abzas.',
      image: { fileId: id * 10, large: true, width: null, height: null, page: 2, pages: 4 },
      author: id === 1 ? 'Nigar Həsənova' : 'Anar Əliyev',
      pdfPages: 4,
      material: [1, 2, 3, 4].map((page) => ({ fileId: id * 10, kind: 'pdf' as const, page, pages: 4 })),
    })
    adminApi.workMaterial.mockImplementation((id: number) => Promise.resolve(material(id)))
    show(v)

    const opener = (await screen.findAllByRole('button', { name: /^İşə bax:/ }))[0]
    await userEvent.click(opener)
    const dialog = await screen.findByRole('dialog', { name: 'Əmircanın Ruhu' })
    expect(within(dialog).getByText('1 / 2')).toBeInTheDocument()
    // It opens on the page that stands for the work — and every page is one step away.
    expect(within(dialog).getByText(/PDF, səhifə 2 \/ 4/)).toBeInTheDocument()
    await waitFor(() => expect(adminApi.documentPageBlob).toHaveBeenCalledWith(1, 10, 2, expect.anything()))
    expect(within(dialog).getAllByRole('button', { name: /^PDF, səhifə \d \/ 4$/ })).toHaveLength(4)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Növbəti səhifə' }))
    expect(within(dialog).getByText(/PDF, səhifə 3 \/ 4/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'PDF, səhifə 1 / 4' }))
    await waitFor(() => expect(adminApi.publicAssetBlob).toHaveBeenCalledWith(1, 10, expect.anything()))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Tam ölçü' }))
    expect(within(dialog).getByRole('button', { name: 'Ekrana sığdır' })).toHaveAttribute('aria-pressed', 'true')

    // The work itself: author, nomination, the whole concept; and the ranking's own facts — never who voted.
    expect(within(dialog).getByText('Nigar Həsənova')).toBeInTheDocument()
    expect(within(dialog).getByText('Simvol və Loqotip Dizaynı')).toBeInTheDocument()
    expect(within(dialog).getByText(/İkinci abzas/)).toBeInTheDocument()
    const context = within(dialog).getByRole('region', { name: 'Xalq səsverməsi' })
    expect(within(context).getByText('Xalq sıralaması').nextSibling).toHaveTextContent('1')
    expect(within(context).getByText('Etibarlı səs').nextSibling).toHaveTextContent('12')
    expect(within(context).getByText('Etibarsız sayılmış').nextSibling).toHaveTextContent('3')
    expect(dialog).not.toHaveTextContent(/@|\+994|e-mail|telefon/i)
    expect(within(dialog).getByRole('link', { name: 'Ətraflı səhifə' })).toHaveAttribute('href', '/admin/submissions/1?competitionId=5')

    // The next work in the ranking's order, by button or arrow key.
    await userEvent.click(within(dialog).getByRole('button', { name: /Növbəti iş/ }))
    expect(await screen.findByRole('dialog', { name: 'Tarixi Körpü' })).toHaveTextContent('2 / 2')
    expect(adminApi.workMaterial).toHaveBeenLastCalledWith(2, expect.anything())
    expect(within(screen.getByRole('dialog')).queryByText('Etibarsız sayılmış')).not.toBeInTheDocument()
    await userEvent.keyboard('{ArrowLeft}')
    expect(await screen.findByRole('dialog', { name: 'Əmircanın Ruhu' })).toHaveTextContent('1 / 2')

    // Escape gives the table back, the focus where it was.
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(opener).toHaveFocus()
  })

  it('says plainly when the competition has no vote at all', async () => {
    show(tally({ state: 'NotConfigured', startsAt: null, endsAt: null, nominations: [], voteCount: 0, voterCount: 0 }))

    expect(await screen.findByText('Bu müsabiqədə səsvermə yoxdur')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })
})
