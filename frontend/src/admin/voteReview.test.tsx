import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import type { AdminReviewVote, AdminWorkReview } from './api'

const adminApi = vi.hoisted(() => ({ voteReview: vi.fn(), invalidateVotes: vi.fn(), restoreVotes: vi.fn() }))
vi.mock('./api', async (original) => ({ ...(await original<typeof import('./api')>()), adminApi }))

const { VoteReviewPanel } = await import('./pages/VoteReviewPanel')

const vote = (id: number, over: Partial<AdminReviewVote> = {}): AdminReviewVote => ({
  voteId: id,
  voterName: `Saxta Hesab ${id}`,
  signedUpAt: '2026-10-05T11:33:00Z',
  votedAt: '2026-10-05T11:33:20Z',
  score: 99,
  evidence: 'PhoneCluster',
  reasons: ['Telefonu səs verən başqa 76 hesabla eynidir'],
  phoneGroup: 'T1',
  phoneGroupSize: 77,
  nameGroup: 'A1',
  nameGroupSize: 74,
  ipGroup: 'IP-1',
  ipGroupSize: 77,
  invalidatedAt: null,
  invalidatedBy: null,
  invalidationReason: null,
  ...over,
})

const review = (votes: AdminReviewVote[]): AdminWorkReview => ({
  evidence: {
    submissionId: 134, reference: 'SUB-2026-00134', title: 'Əmircan', rawVotes: votes.length,
    validVotes: votes.filter((v) => !v.invalidatedAt).length, invalidatedVotes: votes.filter((v) => v.invalidatedAt).length,
    phoneClusterVotes: votes.filter((v) => v.evidence === 'PhoneCluster' && !v.invalidatedAt).length,
    repeatedPhoneVotes: 3, largestPhoneGroup: 77, repeatedNameVotes: 3, largestNameGroup: 74, largestIpGroup: 77,
    authorIpVotes: 7, authorPhoneVotes: 0, newAccountVotes: votes.length, medianSecondsToVote: 16, largestBurst15Min: 12,
    firstSignUp: '2026-10-05T11:33:00Z', lastSignUp: '2026-10-05T17:17:00Z', firstVote: '2026-10-05T11:33:20Z', lastVote: '2026-10-05T17:18:00Z',
  },
  votes,
})

const ring = [vote(1), vote(2), vote(3)]
const honest = vote(9, { voterName: 'Orxan Məmmədov', score: 25, evidence: 'Weak', reasons: ['Bu işə eyni IP-dən daha 3 hesab səs verib'], phoneGroup: null, phoneGroupSize: 0, nameGroup: null, nameGroupSize: 0 })

const show = (data: AdminWorkReview) => {
  adminApi.voteReview.mockResolvedValue(data)
  const onChanged = vi.fn()
  render(
    <MemoryRouter>
      <VoteReviewPanel competitionId={7} submissionId={134} onClose={() => undefined} onChanged={onChanged} />
    </MemoryRouter>,
  )
  return { onChanged }
}

beforeEach(() => vi.clearAllMocks())

describe('reviewing one work’s votes', () => {
  it('shows the evidence in numbers and groups as labels, never a phone or an address', async () => {
    show(review([...ring, honest]))
    expect(await screen.findByRole('heading', { name: /Səslərin yoxlanışı: İş #134/ })).toBeInTheDocument()
    expect(screen.getByText('Təkrarlanan telefonla').nextSibling).toHaveTextContent('3 səs · ən böyük qrup 77 hesab')
    expect(screen.getByText('Eyni IP').nextSibling).toHaveTextContent('tək başına sübut deyil')
    expect(screen.getAllByText('T1 · telefon, 77 hesab')).toHaveLength(3)
    expect(document.body.textContent).not.toMatch(/\+994|@|\d+\.\d+\.\d+\.\d+/)
  })

  it('lets only phone-cluster votes be picked, by hand or by their phone group — never by a name group', async () => {
    show(review([...ring, honest]))
    fireEvent.click(await screen.findByRole('button', { name: 'Hamısı' }))
    expect(screen.getByRole('checkbox', { name: /Orxan Məmmədov/ })).toBeDisabled()
    expect(screen.getByRole('checkbox', { name: /Saxta Hesab 1/ })).toBeEnabled()

    // A name group is a review sign: it gets no select button.
    expect(screen.queryByRole('button', { name: /A1 qrupunu seç/ })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /T1 qrupunu seç/ }))
    expect(screen.getByText('3 səs seçilib')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Seçilmiş səsləri etibarsız say' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Seçilmiş səsləri bərpa et' })).toBeDisabled()
  })

  it('shows R2 as a sign only while the server has it switched off', async () => {
    const r2 = (id: number, evidence: AdminReviewVote['evidence']) =>
      vote(id, { voterName: `Fədayə Qurbanova ${id}`, evidence, coordinatedIdentity: true, phoneGroup: null, phoneGroupSize: 0, nameGroup: 'A2', nameGroupSize: 5 })
    const off = review([r2(21, 'Weak'), r2(22, 'Weak')])
    off.evidence.coordinatedIdentityVotes = 2
    show(off)
    expect(await screen.findByText('R2 koordinasiyalı kimlik (sayılan)')).toBeInTheDocument()
    expect(screen.getByText('R2 koordinasiyalı kimlik (sayılan)').nextSibling).toHaveTextContent('2 səs · qayda aktiv deyil — yalnız əlamət')
    fireEvent.click(screen.getByRole('button', { name: 'Hamısı' }))
    expect(screen.getByRole('checkbox', { name: /Fədayə Qurbanova 21/ })).toBeDisabled()
    expect(screen.queryByRole('button', { name: /A2 \(R2\) qrupunu seç/ })).not.toBeInTheDocument()
  })

  it('lets R2 votes be picked, one by one or by their name group, once the server has it switched on', async () => {
    const on = review([
      vote(31, { evidence: 'CoordinatedIdentity', coordinatedIdentity: true, phoneGroup: null, phoneGroupSize: 0, nameGroup: 'A2', nameGroupSize: 5 }),
      vote(32, { evidence: 'CoordinatedIdentity', coordinatedIdentity: true, phoneGroup: null, phoneGroupSize: 0, nameGroup: 'A2', nameGroupSize: 5 }),
      honest,
    ])
    on.evidence.coordinatedIdentityEnabled = true
    on.evidence.coordinatedIdentityVotes = 2
    show(on)
    expect(await screen.findByRole('button', { name: 'Sübutlu, sayılan' })).toBeInTheDocument()
    expect(screen.getByText('R2 koordinasiyalı kimlik (sayılan)').nextSibling).toHaveTextContent('etibarsız sayıla bilər')
    expect(screen.getAllByText('R2 · koordinasiyalı kimlik')).toHaveLength(2)
    fireEvent.click(screen.getByRole('button', { name: /A2 \(R2\) qrupunu seç/ }))
    expect(screen.getByText('2 səs seçilib')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Seçilmiş səsləri etibarsız say' })).toBeEnabled()
  })

  it('confirms with the count, the work and the consequence, and will not go without a reason', async () => {
    adminApi.invalidateVotes.mockResolvedValue({ changed: 3, references: ['SUB-2026-00134'] })
    const { onChanged } = show(review(ring))
    fireEvent.click(await screen.findByRole('button', { name: /T1 qrupunu seç/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Seçilmiş səsləri etibarsız say' }))

    const dialog = screen.getByRole('dialog', { name: 'Seçilmiş səsləri etibarsız say' })
    expect(within(dialog).getByText('İş #134 — Əmircan')).toBeInTheDocument()
    expect(within(dialog).getByText('3')).toBeInTheDocument()
    expect(within(dialog).getByText(/Səslər silinmir/)).toBeInTheDocument()
    const confirm = within(dialog).getByRole('button', { name: '3 səsi etibarsız say' })
    expect(confirm).toBeDisabled()

    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'qısa' } })
    expect(confirm).toBeDisabled()
    const reason = 'Eyni telefonla açılmış 77 hesab, eyni ad-soyad.'
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: reason } })
    fireEvent.click(confirm)

    await waitFor(() => expect(adminApi.invalidateVotes).toHaveBeenCalledWith(7, [1, 2, 3], reason))
    expect(await screen.findByText(/3 səs etibarsız sayıldı/)).toBeInTheDocument()
    expect(onChanged).toHaveBeenCalled()
    expect(adminApi.voteReview).toHaveBeenCalledTimes(2)
  })

  it('keeps the dialog and says why when the server refuses, changing nothing', async () => {
    adminApi.invalidateVotes.mockRejectedValue(new ApiError(422, '1 səs telefon klasterinə aid deyil. Heç nə dəyişmədi.'))
    const { onChanged } = show(review(ring))
    fireEvent.click(await screen.findByRole('checkbox', { name: /Saxta Hesab 2/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Seçilmiş səsləri etibarsız say' }))
    const dialog = screen.getByRole('dialog')
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'Eyni telefonla açılmış hesablar.' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '1 səsi etibarsız say' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('telefon klasterinə aid deyil')
    expect(onChanged).not.toHaveBeenCalled()
  })

  it('restores set-aside votes the same way, with a reason', async () => {
    adminApi.restoreVotes.mockResolvedValue({ changed: 1, references: ['SUB-2026-00134'] })
    show(review([vote(4, { invalidatedAt: '2026-10-05T18:00:00Z', invalidatedBy: 'Admin', invalidationReason: 'Eyni telefon.' })]))
    fireEvent.click(await screen.findByRole('button', { name: 'Etibarsız sayılmış' }))
    expect(screen.getByText(/Etibarsız sayılıb .*Admin: Eyni telefon\./)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('checkbox', { name: /Saxta Hesab 4/ }))
    expect(screen.getByRole('button', { name: 'Seçilmiş səsləri etibarsız say' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Seçilmiş səsləri bərpa et' }))
    const dialog = screen.getByRole('dialog', { name: 'Seçilmiş səsləri bərpa et' })
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'Ayrıca şəxs olduğu təsdiqləndi.' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '1 səsi bərpa et' }))
    await waitFor(() => expect(adminApi.restoreVotes).toHaveBeenCalledWith(7, [4], 'Ayrıca şəxs olduğu təsdiqləndi.'))
  })
})
