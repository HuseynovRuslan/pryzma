import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminDisposableEmail } from './api'
import { DisposableEmailPanel } from './pages/DisposableEmailPanel'

const adminApi = vi.hoisted(() => ({ disposableEmailRule: vi.fn(), applyDisposableEmailRule: vi.fn() }))
vi.mock('./api', () => ({ adminApi }))

beforeEach(() => vi.clearAllMocks())

const rule = (over: Partial<AdminDisposableEmail> = {}): AdminDisposableEmail => ({
  policyId: 'D14_DISPOSABLE_EMAIL',
  version: 1,
  evidenceType: 'DisposableEmailDuringVoting',
  enabled: true,
  reason: 'Disposable/temporary email domain used by an account created during the voting window.',
  source: 'Read-only audit 2026-10-07: check-mail.org domain classification + MX records of the named service.',
  domainCount: 37,
  votingStartsAt: '2026-10-04T07:40:00Z',
  votingEndsAt: '2026-10-07T07:40:00Z',
  votingClosed: true,
  eligibleVotes: 244,
  setAsideUnderRule: 0,
  setAsideOtherwise: 0,
  openedBeforeVoting: 0,
  works: [
    { submissionId: 75, reference: 'SUB-2026-00111', validNow: 271, eligible: 162, validAfter: 109, setAsideUnderRule: 0 },
    { submissionId: 100, reference: 'SUB-2026-00061', validNow: 265, eligible: 82, validAfter: 183, setAsideUnderRule: 0 },
  ],
  domains: [
    { domain: 'inboxorigin.com', provider: 'temporarymail.com', votes: 31, eligible: 31, setAsideUnderRule: 0 },
    { domain: 'abowned.com', provider: 'temp-mail.org', votes: 25, eligible: 25, setAsideUnderRule: 0 },
  ],
  evidence: [
    {
      voteId: 9001, reference: 'SUB-2026-00111', maskedEmail: 'a***@inboxorigin.com', domain: 'inboxorigin.com',
      provider: 'temporarymail.com', accountOpenedAt: '2026-10-06T18:25:00Z', votedAt: '2026-10-06T18:26:00Z',
      valid: true, setAsideUnderRule: false,
    },
  ],
  ...over,
})

describe('the disposable-e-mail rule (D14), for admins', () => {
  it('shows what the frozen rule meets, per work and per domain, with masked evidence only', async () => {
    adminApi.disposableEmailRule.mockResolvedValue(rule())
    render(<DisposableEmailPanel competitionId={7} onChanged={() => {}} />)
    expect(await screen.findByRole('heading', { name: /Müvəqqəti e-poçt qaydası/ })).toBeInTheDocument()
    expect(screen.getByText('Aktiv')).toBeInTheDocument()
    expect(screen.getByText('qaydaya uyğun etibarlı səs').previousSibling).toHaveTextContent('244')
    const works = screen.getByRole('table', { name: 'İşlər üzrə' })
    expect(within(works).getAllByRole('row').slice(1).map((r) => r.textContent)).toEqual([
      'SUB-2026-00111271162109' + '0',
      'SUB-2026-00061265821830',
    ])
    await userEvent.click(screen.getByText(/Domenlər/))
    expect(screen.getByText('inboxorigin.com')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Səsləri göstər (1)' }))
    expect(screen.getByText('a***@inboxorigin.com')).toBeInTheDocument()
  })

  it('applies the rule to every work at once with the count it showed, after a confirmation', async () => {
    adminApi.disposableEmailRule.mockResolvedValueOnce(rule()).mockResolvedValue(rule({ eligibleVotes: 0, setAsideUnderRule: 244 }))
    adminApi.applyDisposableEmailRule.mockResolvedValue(rule({ eligibleVotes: 0, setAsideUnderRule: 244 }))
    const changed = vi.fn()
    render(<DisposableEmailPanel competitionId={7} onChanged={changed} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Qaydanı tətbiq et — 244 səs' }))
    const dialog = screen.getByRole('dialog', { name: '244 səs etibarsız sayılsın?' })
    expect(dialog).toHaveTextContent('SUB-2026-00111: 162, SUB-2026-00061: 82')
    expect(dialog).toHaveTextContent('Səslər silinmir')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Bəli, 244 səsi etibarsız say' }))
    expect(adminApi.applyDisposableEmailRule).toHaveBeenCalledWith(7, 244, rule().reason)
    expect(changed).toHaveBeenCalled()
    expect(await screen.findByText('bu qayda ilə etibarsız sayılıb')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Qaydanı tətbiq et/ })).not.toBeInTheDocument()
  })

  it('offers nothing to apply while the rule is off or the vote is open', async () => {
    adminApi.disposableEmailRule.mockResolvedValue(rule({ enabled: false }))
    const { unmount } = render(<DisposableEmailPanel competitionId={7} onChanged={() => {}} />)
    expect(await screen.findByText('Aktiv deyil')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Qaydanı tətbiq et/ })).not.toBeInTheDocument()
    unmount()
    adminApi.disposableEmailRule.mockResolvedValue(rule({ votingClosed: false }))
    render(<DisposableEmailPanel competitionId={7} onChanged={() => {}} />)
    expect(await screen.findByRole('button', { name: 'Qaydanı tətbiq et — 244 səs' })).toBeDisabled()
  })
})
