import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { EmailFirstStart } from './EmailFirstRegistration'

// The real endpoints and API client this time, with only the network replaced by the server's actual answer: the
// other registration tests mock `authApi` away, and so never saw what the browser did with an empty 202.
vi.mock('../../lib/publicConfig', () => ({
  usePublicConfig: () => ({ qrlogLogin: false, passwordReset: true, captchaSiteKey: null, registrationFlow: 'email-first' }),
}))

const fetchMock = vi.fn<typeof fetch>()

beforeEach(() => {
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('email-first registration against the server as it answers', () => {
  it('shows "link sent" for the 202 with no body that the server gives — never "Bağlantı alınmadı"', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }))
    render(
      <MemoryRouter>
        <EmailFirstStart next={null} />
      </MemoryRouter>,
    )
    fireEvent.change(screen.getByLabelText('E-mail'), { target: { value: 'aysel@example.com' } })
    fireEvent.click(screen.getByRole('button', { name: 'Təsdiq linki göndər' }))

    expect(await screen.findByRole('heading', { name: 'Təsdiq linkini göndərdik' })).toBeInTheDocument()
    expect(screen.getByText('aysel@example.com')).toBeInTheDocument()
    expect(screen.queryByText(/Bağlantı alınmadı/)).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledWith('/api/auth/registration/start', expect.objectContaining({ method: 'POST' }))
  })
})
