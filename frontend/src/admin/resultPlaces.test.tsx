import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { placeName } from '../lib/text'
import type { AdminAwardPodium, AdminAwards, AdminCompetition, AdminPlaceCandidate, AdminResult } from './api'
import { placesFromVotes, placesName, placesProblem } from './pages/places'
import { ResultsTab } from './pages/ResultsTab'

const adminApi = vi.hoisted(() => ({ result: vi.fn(), setPlaces: vi.fn(), awards: vi.fn(), setAwards: vi.fn(), clearAwards: vi.fn() }))
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  adminApi,
}))

const podium = (category: 'Jury' | 'Public', over: Partial<AdminAwardPodium> = {}): AdminAwardPodium => ({
  category, places: [], source: null, recordedAt: null, recordedBy: null, ...over,
})

/** The two podiums of the result; none recorded unless the test says so. */
const awards = (over: Partial<AdminAwards> = {}): AdminAwards => ({
  competitionId: 7, resultPublished: false, voting: 'Closed', jury: podium('Jury'), public: podium('Public'), candidates: ranked(9, 7, 5, 1), ...over,
})

beforeEach(() => {
  vi.clearAllMocks()
  adminApi.awards.mockResolvedValue(awards())
})

/** Works in the order the server ranks them: most votes first, level works sharing a rank. */
const ranked = (...votes: number[]): AdminPlaceCandidate[] =>
  votes.map((v, i) => ({
    submissionId: 100 + i,
    reference: `SUB-2026-000${10 + i}`,
    title: `İş ${i + 1}`,
    participantName: `İştirakçı ${i + 1}`,
    preview: null,
    voteCount: v,
    voteRank: votes.indexOf(v) + 1,
    lateVoteCount: 0,
    invalidatedVoteCount: 0,
  }))

describe('places from the vote', () => {
  it('fills every place the tally decides on its own', () => {
    expect(placesFromVotes(ranked(9, 5, 2, 1))).toEqual({ places: [100, 101, 102], notes: [] })
  })

  it('leaves level works unordered and says which places they share', () => {
    const { places, notes } = placesFromVotes(ranked(9, 4, 4, 1))
    expect(places).toEqual([100, null, null])
    expect(notes).toEqual(['2-ci və 3-cü yer: SUB-2026-00011 və SUB-2026-00012 bərabər səs toplayıb (4 səs) — sıranı siz təyin edin.'])
  })

  it('names the last place when more works are level than there are places left', () => {
    const { places, notes } = placesFromVotes(ranked(9, 5, 1, 1, 1))
    expect(places).toEqual([100, 101, null])
    expect(notes).toEqual(['3-cü yer: 3 iş bərabər səs toplayıb (1 səs) — bu yeri hansının tutacağını siz seçin.'])
  })

  it('a tie for first leaves the top open, and a work without a vote is never placed', () => {
    const { places, notes } = placesFromVotes(ranked(3, 3, 0, 0))
    expect(places).toEqual([null, null, null])
    expect(notes[0]).toMatch(/^1-ci və 2-ci yer:/)
    expect(placesFromVotes(ranked(0, 0)).notes).toEqual(['Hələ heç bir işə səs verilməyib — yerləri özünüz seçin.'])
  })

  it('says why a choice cannot be saved before the server does', () => {
    expect(placesProblem([1, null, 3])).toBe('2-ci yer boş qalarkən 3-cü yer seçilə bilməz.')
    expect(placesProblem([1, 1, null])).toBe('Bir iş yalnız bir yer tuta bilər.')
    expect(placesProblem([1, 2, null])).toBeNull()
    expect([1, 2, 3].map(placeName)).toEqual(['1-ci yer', '2-ci yer', '3-cü yer'])
    expect(placesName([2, 3])).toBe('2-ci və 3-cü yer')
  })
})

describe('results tab without categories', () => {
  const candidates = ranked(9, 4, 4, 1)
  const result = (over: Partial<AdminResult> = {}): AdminResult => ({
    competitionId: 7,
    competitionTitle: 'Əmircanın simvolu',
    competitionSlug: 'emircanin-simvolu',
    hasCategories: false,
    categoryLabel: null,
    state: 'NoWinner',
    categoryCount: 0,
    winnerCount: 0,
    slots: [{ category: null, categoryActive: true, eligibleCount: 4, winner: null }],
    publishedAt: null,
    publishedBy: null,
    announcement: null,
    coverImage: null,
    isPublicNow: false,
    publishProblems: ['Heç bir qalib seçilməyib.'],
    eligibleCount: 4,
    publicFields: ['İşin adı'],
    hasPublicFileField: true,
    assetsPending: 0,
    assetFailures: [],
    places: [1, 2, 3].map((place) => ({ place, winner: null })),
    placeCandidates: candidates,
    voting: 'Closed',
    ...over,
  })
  const renderTab = () =>
    render(
      <MemoryRouter>
        <ResultsTab competition={{ id: 7 } as AdminCompetition} onChanged={vi.fn().mockResolvedValue(undefined)} />
      </MemoryRouter>,
    )

  it('fills from the vote, leaves the level works to the admin, and saves the three places together', async () => {
    adminApi.result.mockResolvedValue(result())
    adminApi.setPlaces.mockResolvedValue(result({ state: 'Prepared' }))
    renderTab()

    expect(await screen.findByRole('heading', { name: 'Qaliblər: 1-ci, 2-ci və 3-cü yer' })).toBeInTheDocument()
    const table = screen.getByRole('table')
    expect(within(table).getAllByText('Bərabər')).toHaveLength(2)

    await userEvent.click(screen.getByRole('button', { name: 'Yerləri təyin et' }))
    await userEvent.click(screen.getByRole('button', { name: 'Səs sıralamasından doldur' }))
    expect(screen.getByText(/2-ci və 3-cü yer: SUB-2026-00011 və SUB-2026-00012 bərabər səs toplayıb/)).toBeInTheDocument()
    expect(screen.getByLabelText('1-ci yer')).toHaveValue('100')
    expect(screen.getByLabelText('2-ci yer')).toHaveValue('')

    // The admin orders the level works — here the later reference second.
    await userEvent.selectOptions(screen.getByLabelText('2-ci yer'), '102')
    await userEvent.selectOptions(screen.getByLabelText('3-cü yer'), '101')
    await userEvent.click(screen.getByRole('button', { name: 'Yadda saxla' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('İştirakçı 3')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bəli, yadda saxla' }))
    expect(adminApi.setPlaces).toHaveBeenCalledWith(7, [100, 102, 101], [null, null, null], false)
  })

  it('will not save a second place without a first, and warns while the vote is still open', async () => {
    adminApi.result.mockResolvedValue(result({ voting: 'Open' }))
    renderTab()
    await userEvent.click(await screen.findByRole('button', { name: 'Yerləri təyin et' }))
    expect(screen.getByText(/Səsvermə hələ davam edir/)).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByLabelText('2-ci yer'), '101')
    expect(screen.getByText('1-ci yer boş qalarkən 2-ci yer seçilə bilməz.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Yadda saxla' })).toBeDisabled()
  })
})

describe('the two podiums of a two-choice result (D18)', () => {
  const result = { competitionId: 7, competitionTitle: 'Əmircanın simvolu', competitionSlug: 'emircanin-simvolu', hasCategories: false,
    categoryLabel: null, state: 'NoWinner', categoryCount: 0, winnerCount: 0, slots: [], publishedAt: null, publishedBy: null,
    announcement: null, coverImage: null, isPublicNow: false, publishProblems: [], eligibleCount: 4, publicFields: ['İşin adı'],
    hasPublicFileField: true, assetsPending: 0, assetFailures: [], places: [1, 2, 3].map((place) => ({ place, winner: null })),
    placeCandidates: ranked(9, 7, 5, 1), voting: 'Closed' } as AdminResult
  const renderTab = () =>
    render(
      <MemoryRouter>
        <ResultsTab competition={{ id: 7 } as AdminCompetition} onChanged={vi.fn().mockResolvedValue(undefined)} />
      </MemoryRouter>,
    )
  const group = async (name: string) => screen.findByRole('group', { name })

  it('records the jury’s podium from its paper protocol: the work, the announced name, the points and their source', async () => {
    adminApi.result.mockResolvedValue(result)
    adminApi.setAwards.mockResolvedValue(awards())
    renderTab()
    const jury = await group('Münsiflərin seçimi')
    await userEvent.click(within(jury).getByRole('button', { name: 'Yerləri qeyd et' }))
    const pick = [[102, 'Kamal Məmmədli', '30'], [100, 'Məryəm Əhmədova', '26'], [103, 'Nərgiz Əsədova', '16']] as const
    for (const [i, [id, name, points]] of pick.entries()) {
      const place = placeName(i + 1)
      await userEvent.selectOptions(within(jury).getByLabelText(`${place} — iş`), String(id))
      await userEvent.clear(within(jury).getByLabelText(`${place} — elan olunan ad`))
      await userEvent.type(within(jury).getByLabelText(`${place} — elan olunan ad`), name)
      await userEvent.type(within(jury).getByLabelText(`${place} — bal`), points)
    }
    await userEvent.type(within(jury).getByLabelText('Balların mənbəyi (ictimai deyil)'), 'Kağız protokol')
    await userEvent.click(within(jury).getByRole('button', { name: 'Yadda saxla' }))
    expect(adminApi.setAwards).toHaveBeenCalledWith(7, 'jury', [
      { submissionId: 102, participantName: 'Kamal Məmmədli', points: 30 },
      { submissionId: 100, participantName: 'Məryəm Əhmədova', points: 26 },
      { submissionId: 103, participantName: 'Nərgiz Əsədova', points: 16 },
    ], [null, null, null], 'Kağız protokol', false)
  })

  it('fills the public podium from the valid vote — the numbers are the server’s and cannot be typed', async () => {
    adminApi.result.mockResolvedValue(result)
    adminApi.setAwards.mockResolvedValue(awards())
    renderTab()
    const pub = await group('Xalqın seçimi')
    await userEvent.click(within(pub).getByRole('button', { name: 'Yerləri qeyd et' }))
    await userEvent.click(within(pub).getByRole('button', { name: 'Etibarlı səs sıralamasından doldur' }))
    expect(within(pub).getByLabelText('1-ci yer — etibarlı səs')).toHaveValue('9')
    expect(within(pub).getByLabelText('1-ci yer — etibarlı səs')).toHaveAttribute('readonly')
    expect(within(pub).queryByLabelText(/mənbəyi/)).not.toBeInTheDocument()
    await userEvent.click(within(pub).getByRole('button', { name: 'Yadda saxla' }))
    expect(adminApi.setAwards).toHaveBeenCalledWith(7, 'public', [
      { submissionId: 100, participantName: 'İştirakçı 1', points: 9 },
      { submissionId: 101, participantName: 'İştirakçı 2', points: 7 },
      { submissionId: 102, participantName: 'İştirakçı 3', points: 5 },
    ], [null, null, null], null, false)
  })

  it('shows the announced name beside the account’s and says when a public count no longer matches', async () => {
    adminApi.result.mockResolvedValue(result)
    const place = (p: number, id: number, name: string, account: string, points: number, valid: number) => ({
      place: p, submissionId: id, reference: `SUB-2026-000${id - 90}`, title: `İş ${id - 99}`, participantName: name, accountName: account,
      points, status: 'Submitted' as const, preview: null, validVotes: valid,
    })
    adminApi.awards.mockResolvedValue(awards({
      jury: podium('Jury', { places: [place(1, 102, 'Kamal Məmmədli', 'Kamal Məmmədli', 30, 5)], source: 'Kağız protokol' }),
      public: podium('Public', { places: [place(1, 100, 'Şəbnəm Ələkbərli', 'Shabnam Alakbarli', 200, 199)], source: 'Etibarlı səslər (audit sonrası)' }),
    }))
    renderTab()
    const pub = await group('Xalqın seçimi')
    expect(within(pub).getByText('(hesabda: Shabnam Alakbarli)')).toBeInTheDocument()
    expect(within(pub).getByText(/İndi 199 etibarlı səs — yeniləyin/)).toBeInTheDocument()
    const jury = await group('Münsiflərin seçimi')
    expect(within(jury).queryByText(/hesabda/)).not.toBeInTheDocument()
    expect(within(jury).getByText(/Mənbə: Kağız protokol/)).toBeInTheDocument()
  })
})
