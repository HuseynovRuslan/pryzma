import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import type { SubmissionMoveOptions, VotingLaunch, VotingLaunchCompetition } from './api'
import { MovePanel } from './MovePanel'
import VotingLaunchPage from './pages/VotingLaunchPage'

const adminApi = vi.hoisted(() => ({
  votingLaunch: vi.fn(),
  launchVoting: vi.fn(),
  submissionMoveOptions: vi.fn(),
  moveSubmission: vi.fn(),
}))
vi.mock('./api', () => ({ adminApi }))

beforeEach(() => vi.clearAllMocks())

const competition = (over: Partial<VotingLaunchCompetition> = {}): VotingLaunchCompetition => ({
  id: 6,
  title: '“Novxanının simvolu” yaradıcılıq müsabiqəsi',
  slug: 'novxaninin-simvolu',
  projectName: 'Abşeronun simvolları',
  votingState: 'NotConfigured',
  votingStartsAt: null,
  votingEndsAt: null,
  finalWorks: 52,
  onBallot: 0,
  withoutPicture: 0,
  createsNomination: true,
  problems: [],
  warnings: [],
  ...over,
})

const screenData = (competitions: VotingLaunchCompetition[]): VotingLaunch => ({ serverNow: new Date().toISOString(), competitions })

const showLaunch = () =>
  render(
    <MemoryRouter>
      <VotingLaunchPage />
    </MemoryRouter>,
  )

describe('starting the vote', () => {
  it('starts every ready competition together, after a confirmation, and names their pages', async () => {
    adminApi.votingLaunch.mockResolvedValue(screenData([
      competition(),
      competition({ id: 8, title: '“Maştağanın simvolu” yaradıcılıq müsabiqəsi', slug: 'mastaganin-simvolu', finalWorks: 50 }),
    ]))
    adminApi.launchVoting.mockResolvedValue({
      startsAt: '2026-10-31T08:00:00Z',
      endsAt: '2026-11-07T16:00:00Z',
      competitions: [
        { id: 6, title: '“Novxanının simvolu” yaradıcılıq müsabiqəsi', slug: 'novxaninin-simvolu', onBallot: 52, nominationCreated: true },
        { id: 8, title: '“Maştağanın simvolu” yaradıcılıq müsabiqəsi', slug: 'mastaganin-simvolu', onBallot: 50, nominationCreated: true },
      ],
    })
    showLaunch()

    expect(await screen.findAllByText(/“Bütün işlər” nominasiyası yaradılacaq/)).toHaveLength(2)
    await userEvent.click(screen.getByRole('button', { name: 'Səsverməni başlat' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/Novxanının simvolu/)).toBeInTheDocument()
    expect(within(dialog).getByText(/Maştağanın simvolu/)).toBeInTheDocument()
    expect(adminApi.launchVoting).not.toHaveBeenCalled()

    await userEvent.click(within(dialog).getByRole('button', { name: 'Bəli, başlat' }))
    await waitFor(() => expect(adminApi.launchVoting).toHaveBeenCalledTimes(1))
    const body = adminApi.launchVoting.mock.calls[0][0]
    expect(body.competitionIds).toEqual([6, 8])
    expect(Date.parse(body.endsAt)).toBeGreaterThan(Date.now())

    expect(await screen.findByText('Səsvermə başladı.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: '“Novxanının simvolu” yaradıcılıq müsabiqəsi' })).toHaveAttribute('href', '/competitions/novxaninin-simvolu/voting')
    expect(screen.getByRole('link', { name: '“Maştağanın simvolu” yaradıcılıq müsabiqəsi' })).toHaveAttribute('href', '/competitions/mastaganin-simvolu/voting')
  })

  it('leaves out what is not ready and says why, and starts only what the admin keeps chosen', async () => {
    adminApi.votingLaunch.mockResolvedValue(screenData([
      competition(),
      competition({ id: 9, title: 'Açıq müsabiqə', slug: 'aciq', problems: ['İşlərin qəbulu hələ açıqdır — əvvəlcə qəbulu bağlayın.'] }),
      competition({ id: 8, title: 'Maştağa', slug: 'mastaganin-simvolu', warnings: ['3 işin saytda göstəriləcək şəkli hazır deyil — kartında yer tutucu görünəcək.'] }),
    ]))
    adminApi.launchVoting.mockResolvedValue({ startsAt: '', endsAt: '2026-11-07T16:00:00Z', competitions: [] })
    showLaunch()

    expect(await screen.findByText(/qəbulu hələ açıqdır/)).toBeInTheDocument()
    expect(screen.getByText(/3 işin saytda göstəriləcək şəkli hazır deyil/)).toBeInTheDocument()
    const boxes = screen.getAllByRole('checkbox')
    expect(boxes[1]).toBeDisabled()
    await userEvent.click(boxes[2]) // Maştağa out
    await userEvent.click(screen.getByRole('button', { name: 'Səsverməni başlat' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Bəli, başlat' }))
    await waitFor(() => expect(adminApi.launchVoting).toHaveBeenCalledTimes(1))
    expect(adminApi.launchVoting.mock.calls[0][0].competitionIds).toEqual([6])
  })

  it('shows the server’s refusal in the dialog', async () => {
    adminApi.votingLaunch.mockResolvedValue(screenData([competition()]))
    adminApi.launchVoting.mockRejectedValue(new ApiError(400, 'Formada düzəliş tələb olunur.', { endsAt: ['Bitmə vaxtı indidən ən azı 10 dəqiqə sonra olmalıdır.'] }))
    showLaunch()

    await userEvent.click(await screen.findByRole('button', { name: 'Səsverməni başlat' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Bəli, başlat' }))
    expect(await screen.findByText('Bitmə vaxtı indidən ən azı 10 dəqiqə sonra olmalıdır.')).toBeInTheDocument()
  })
})

const moveOptions = (over: Partial<SubmissionMoveOptions> = {}): SubmissionMoveOptions => ({
  submissionId: 294,
  reference: 'SUB-2026-00257',
  competitionId: 6,
  competitionTitle: '“Novxanının simvolu” yaradıcılıq müsabiqəsi',
  blockers: [],
  targets: [
    { id: 8, title: '“Maştağanın simvolu” yaradıcılıq müsabiqəsi', projectName: 'Abşeronun simvolları', problems: [] },
    { id: 3, title: 'Köhnə forma', projectName: 'Bakı kəndləri', problems: ['Formada “Konseptin izahı” (concept) sahəsi yoxdur.'] },
  ],
  ...over,
})

describe('moving a work to another competition', () => {
  it('moves it to the competition chosen, after a confirmation', async () => {
    adminApi.submissionMoveOptions.mockResolvedValue(moveOptions())
    adminApi.moveSubmission.mockResolvedValue({ submissionId: 294, reference: 'SUB-2026-00257', fromCompetitionId: 6, toCompetitionId: 8, toCompetitionTitle: 'Maştağa' })
    const onMoved = vi.fn()
    render(<MovePanel submissionId={294} onMoved={onMoved} />)

    await userEvent.click(screen.getByRole('button', { name: 'Köçürməyə başla' }))
    await userEvent.selectOptions(await screen.findByRole('combobox'), '8')
    await userEvent.click(screen.getByRole('button', { name: 'Köçür' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('SUB-2026-00257')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bəli, köçür' }))

    await waitFor(() => expect(onMoved).toHaveBeenCalledTimes(1))
    expect(adminApi.moveSubmission).toHaveBeenCalledWith(294, 8)
  })

  it('says why a competition cannot take it and does not offer to move it there', async () => {
    adminApi.submissionMoveOptions.mockResolvedValue(moveOptions())
    render(<MovePanel submissionId={294} onMoved={vi.fn()} />)

    await userEvent.click(screen.getByRole('button', { name: 'Köçürməyə başla' }))
    await userEvent.selectOptions(await screen.findByRole('combobox'), '3')
    expect(screen.getByText(/\(concept\) sahəsi yoxdur/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Köçür' })).toBeDisabled()
  })

  it('offers nothing for a work that already has votes', async () => {
    adminApi.submissionMoveOptions.mockResolvedValue(moveOptions({ blockers: ['İşə səs verilib.'] }))
    render(<MovePanel submissionId={294} onMoved={vi.fn()} />)

    await userEvent.click(screen.getByRole('button', { name: 'Köçürməyə başla' }))
    expect(await screen.findByText(/Bu təklifi köçürmək olmaz: İşə səs verilib\./)).toBeInTheDocument()
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
  })
})
