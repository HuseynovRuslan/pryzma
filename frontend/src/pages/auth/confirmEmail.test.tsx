import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../api/client'
import { voteCopy } from '../voting/lib'
import { VoteButton } from '../voting/shared'
import ConfirmEmailPage from './ConfirmEmailPage'

const api = vi.hoisted(() => ({ confirmEmail: vi.fn(), resendConfirmation: vi.fn() }))
vi.mock('../../api/endpoints', () => ({ authApi: api }))

const open = (hash: string) => {
  window.history.replaceState(null, '', `/confirm-email${hash}`)
  render(
    <MemoryRouter>
      <ConfirmEmailPage />
    </MemoryRouter>,
  )
}

beforeEach(() => vi.clearAllMocks())
afterEach(() => window.history.replaceState(null, '', '/'))

describe('the confirmation link', () => {
  it('confirms at once, with the address and token from the fragment, and strips them from the address bar', async () => {
    api.confirmEmail.mockResolvedValue(undefined)
    open('#email=aysel%40example.com&token=abc123')
    expect(await screen.findByRole('heading', { name: 'E-mail təsdiqləndi' })).toBeInTheDocument()
    expect(api.confirmEmail).toHaveBeenCalledTimes(1)
    expect(api.confirmEmail).toHaveBeenCalledWith({ email: 'aysel@example.com', token: 'abc123' })
    expect(window.location.hash).toBe('')
  })

  it('says why when the link is wrong or old', async () => {
    api.confirmEmail.mockRejectedValue(
      new ApiError(400, 'Yoxlama', { token: ['Təsdiq linki etibarsızdır və ya vaxtı bitib. Hesabınıza daxil olub yenisini istəyin.'] }),
    )
    open('#email=aysel%40example.com&token=bad')
    expect(await screen.findByRole('alert')).toHaveTextContent('Təsdiq linki etibarsızdır')
  })

  it('does not call the server for a cut-off link', async () => {
    open('#email=aysel%40example.com')
    expect(await screen.findByRole('heading', { name: 'Link işləmədi' })).toBeInTheDocument()
    expect(api.confirmEmail).not.toHaveBeenCalled()
  })
})

describe('a vote waiting for a confirmed e-mail', () => {
  const work = { eligibility: 'EmailNotConfirmed' as const, reference: 'SUB-2026-00001' }

  it('says so instead of a vote button, and sends a new link on request', async () => {
    api.resendConfirmation.mockResolvedValue(undefined)
    render(
      <MemoryRouter>
        <VoteButton work={work} onVote={() => undefined} />
      </MemoryRouter>,
    )
    expect(screen.getByText(voteCopy.EmailNotConfirmed.label)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Səs ver' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Linki yenidən göndər' }))
    expect(await screen.findByRole('status')).toHaveTextContent('Yeni link e-mailinizə göndərildi.')
    expect(api.resendConfirmation).toHaveBeenCalledTimes(1)
  })

  it('shows the server’s answer when asked too often', async () => {
    api.resendConfirmation.mockRejectedValue(new ApiError(429, 'Məktub az əvvəl göndərildi. Bir dəqiqə sonra yenidən istəyin.'))
    render(
      <MemoryRouter>
        <VoteButton work={work} onVote={() => undefined} />
      </MemoryRouter>,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Linki yenidən göndər' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Bir dəqiqə sonra')
    expect(screen.getByRole('button', { name: 'Linki yenidən göndər' })).toBeEnabled()
  })
})
