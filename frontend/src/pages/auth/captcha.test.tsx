import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../api/client'
import { TURNSTILE_SRC } from '../../components/Turnstile'
import RegisterPage from './RegisterPage'

const auth = vi.hoisted(() => ({ register: vi.fn(), status: 'anonymous' as const }))
const config = vi.hoisted(() => ({ value: { qrlogLogin: false, passwordReset: false, captchaSiteKey: null as string | null, ready: true } }))
vi.mock('../../auth/AuthContext', () => ({ useAuth: () => auth }))
vi.mock('../../lib/publicConfig', () => ({ usePublicConfig: () => config.value }))

// Cloudflare's widget, as the page sees it: render() hands back an id and later calls back with a token.
const widget = { render: vi.fn(), remove: vi.fn(), callbacks: [] as ((t: string) => void)[] }

const fill = () => {
  const type = (label: RegExp, value: string) => fireEvent.change(screen.getByLabelText(label), { target: { value } })
  type(/^Ad/, 'Aysel')
  type(/^Soyad/, 'Məmmədova')
  type(/^Telefon/, '050 123 45 67')
  type(/^E-mail/, 'aysel@example.com')
  type(/^Şifrə$/, 'secret123')
  type(/^Şifrəni təkrar/, 'secret123')
  fireEvent.click(screen.getByRole('checkbox'))
}

const show = () =>
  render(
    <MemoryRouter>
      <RegisterPage />
    </MemoryRouter>,
  )

beforeEach(() => {
  vi.clearAllMocks()
  widget.callbacks = []
  widget.render.mockImplementation((_el: HTMLElement, o: { callback: (t: string) => void }) => {
    widget.callbacks.push(o.callback)
    return `w${widget.callbacks.length}`
  })
  window.turnstile = { render: widget.render, remove: widget.remove }
  // jsdom has no layout: a refused form brings its first invalid field into view.
  Element.prototype.scrollIntoView = vi.fn()
})
afterEach(() => {
  delete window.turnstile
  config.value.captchaSiteKey = null
})

describe('the registration robot check', () => {
  it('is not there, nor its script, while the server does not ask for it', async () => {
    show()
    fill()
    auth.register.mockResolvedValue({})
    fireEvent.click(screen.getByRole('button', { name: 'Hesab yarat' }))
    await waitFor(() => expect(auth.register).toHaveBeenCalled())
    expect(auth.register.mock.calls[0][0]).not.toHaveProperty('captchaToken')
    expect(widget.render).not.toHaveBeenCalled()
    expect(document.querySelector(`script[src="${TURNSTILE_SRC}"]`)).toBeNull()
  })

  it('must pass before the form goes, and its token goes with it', async () => {
    config.value.captchaSiteKey = '0x4AAAAAAAtestsitekey'
    show()
    await waitFor(() => expect(widget.render).toHaveBeenCalledTimes(1))
    expect(widget.render.mock.calls[0][1]).toMatchObject({ sitekey: '0x4AAAAAAAtestsitekey', action: 'register' })
    fill()

    fireEvent.click(screen.getByRole('button', { name: 'Hesab yarat' }))
    expect(await screen.findByText(/Robot olmadığınızı təsdiqləyin/)).toBeInTheDocument()
    expect(auth.register).not.toHaveBeenCalled()

    act(() => widget.callbacks[0]('token-1'))
    auth.register.mockResolvedValue({})
    fireEvent.click(screen.getByRole('button', { name: 'Hesab yarat' }))
    await waitFor(() => expect(auth.register).toHaveBeenCalledTimes(1))
    expect(auth.register.mock.calls[0][0]).toMatchObject({ captchaToken: 'token-1', email: 'aysel@example.com' })
  })

  it('asks afresh after a refused submit, since a token works only once', async () => {
    config.value.captchaSiteKey = '0x4AAAAAAAtestsitekey'
    show()
    await waitFor(() => expect(widget.render).toHaveBeenCalledTimes(1))
    fill()
    act(() => widget.callbacks[0]('token-1'))
    auth.register.mockRejectedValue(new ApiError(400, 'Yoxlama', { phone: ['Bu telefon nömrəsi ilə artıq hesab açılıb.'] }))
    fireEvent.click(screen.getByRole('button', { name: 'Hesab yarat' }))
    await waitFor(() => expect(widget.render).toHaveBeenCalledTimes(2))
    expect(widget.remove).toHaveBeenCalledWith('w1')

    // The old token is gone: without a new one the form does not go again.
    fireEvent.click(screen.getByRole('button', { name: 'Hesab yarat' }))
    expect(await screen.findByText(/Robot olmadığınızı təsdiqləyin/)).toBeInTheDocument()
    expect(auth.register).toHaveBeenCalledTimes(1)
  })
})
