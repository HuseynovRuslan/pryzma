import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import { Footer } from '../components/Footer'
import { Header } from '../components/Header'
import { PlatformContext } from '../platform/context'
import { platformDefaults } from '../platform/defaults'
import { ContactPage } from './InfoPages'

const api = vi.hoisted(() => ({ send: vi.fn() }))
vi.mock('../api/endpoints', () => ({ contactApi: { send: api.send } }))
vi.mock('../auth/AuthContext', () => ({ useAuth: () => ({ status: 'anonymous', user: null }) }))
const config = vi.hoisted(() => ({ value: { captchaSiteKey: null as string | null, ready: true } }))
vi.mock('../lib/publicConfig', () => ({ usePublicConfig: () => config.value }))

// Cloudflare's widget, as the page sees it: render() hands back an id and later calls back with a token.
const widget = { render: vi.fn(), remove: vi.fn(), callbacks: [] as ((t: string) => void)[] }

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

const show = (node: React.ReactNode) =>
  render(
    <PlatformContext.Provider value={platformDefaults}>
      <MemoryRouter>{node}</MemoryRouter>
    </PlatformContext.Provider>,
  )

const type = (label: RegExp, value: string) => fireEvent.change(screen.getByLabelText(label), { target: { value } })
const fill = () => {
  type(/^Adınız/, '  Aysel Məmmədova ')
  type(/^E-poçt/, ' aysel@example.com ')
  type(/^Mesaj/, '  Salam! Növbəti müsabiqə haqqında sualım var.  ')
}
const send = () => fireEvent.click(screen.getByRole('button', { name: 'Göndər' }))

describe('the way to the contact page', () => {
  it('is in the header at the top of every page', () => {
    show(<Header />)
    const nav = screen.getByRole('navigation', { name: 'Əsas naviqasiya' })
    expect(within(nav).getByRole('link', { name: 'Əlaqə' })).toHaveAttribute('href', '/contact')
  })

  it('is in the footer too, once', () => {
    show(<Footer />)
    const nav = screen.getByRole('navigation', { name: 'Alt naviqasiya' })
    expect(within(nav).getAllByRole('link', { name: 'Əlaqə' })).toHaveLength(1)
    expect(within(nav).getByRole('link', { name: 'Əlaqə' })).toHaveAttribute('href', '/contact')
  })
})

describe('the contact page', () => {
  it('gives the address too, as a link that opens the reader’s mail', () => {
    show(<ContactPage />)
    expect(screen.getByRole('heading', { level: 1, name: 'Əlaqə' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'huseynovruslaninfo@gmail.com' })).toHaveAttribute(
      'href',
      'mailto:huseynovruslaninfo@gmail.com',
    )
  })

  it('says what is missing before sending anything', async () => {
    show(<ContactPage />)
    type(/^E-poçt/, 'ünvan-deyil')
    type(/^Mesaj/, 'qısa')
    send()
    expect(await screen.findByText('Adınızı yazın.')).toBeInTheDocument()
    expect(screen.getByText(/E-poçt ünvanı düzgün deyil/)).toBeInTheDocument()
    expect(screen.getByText('Mesaj 10–4000 simvol olmalıdır.')).toBeInTheDocument()
    expect(screen.getByLabelText(/^Adınız/)).toHaveAttribute('aria-invalid', 'true')
    expect(api.send).not.toHaveBeenCalled()
  })

  it('sends the message from the site and says it went', async () => {
    api.send.mockResolvedValue(undefined)
    show(<ContactPage />)
    fill()
    send()
    expect(await screen.findByRole('heading', { name: 'Mesajınız göndərildi' })).toBeInTheDocument()
    expect(api.send).toHaveBeenCalledWith({
      name: 'Aysel Məmmədova',
      email: 'aysel@example.com',
      message: 'Salam! Növbəti müsabiqə haqqında sualım var.',
    })
    // Another message starts from an empty form.
    fireEvent.click(screen.getByRole('button', { name: 'Yeni mesaj yaz' }))
    expect(screen.getByLabelText(/^Mesaj/)).toHaveValue('')
  })

  it('keeps the text when the message did not go, and says why', async () => {
    api.send.mockRejectedValue(new ApiError(503, 'Mesajınız göndərilmədi. Bir az sonra yenidən cəhd edin.'))
    show(<ContactPage />)
    fill()
    send()
    expect(await screen.findByRole('alert')).toHaveTextContent('Mesajınız göndərilmədi. Bir az sonra yenidən cəhd edin.')
    expect(screen.getByLabelText(/^Mesaj/)).toHaveValue('  Salam! Növbəti müsabiqə haqqında sualım var.  ')
    expect(screen.queryByRole('heading', { name: 'Mesajınız göndərildi' })).not.toBeInTheDocument()
  })

  it('shows the server’s word on a field', async () => {
    api.send.mockRejectedValue(new ApiError(400, 'Formada düzəliş tələb olunur.', { email: ['E-poçt ünvanı düzgün deyil.'] }))
    show(<ContactPage />)
    fill()
    send()
    expect(await screen.findByText('E-poçt ünvanı düzgün deyil.')).toBeInTheDocument()
    expect(screen.getByLabelText(/^E-poçt/)).toHaveAttribute('aria-invalid', 'true')
  })

  it('asks for the robot check of its own when the server names a site key, afresh after a refusal', async () => {
    config.value.captchaSiteKey = '0x4AAAAAAAtestsitekey'
    show(<ContactPage />)
    await waitFor(() => expect(widget.render).toHaveBeenCalledTimes(1))
    expect(widget.render.mock.calls[0][1]).toMatchObject({ action: 'contact' })
    fill()
    send()
    expect(await screen.findByText(/Robot olmadığınızı təsdiqləyin/)).toBeInTheDocument()
    expect(api.send).not.toHaveBeenCalled()

    act(() => widget.callbacks[0]('token-1'))
    api.send.mockRejectedValue(new ApiError(503, 'Mesajınız göndərilmədi. Bir az sonra yenidən cəhd edin.'))
    send()
    await waitFor(() => expect(api.send).toHaveBeenCalledWith(expect.objectContaining({ captchaToken: 'token-1' })))
    // A token works once: the check is shown again for a new one.
    await waitFor(() => expect(widget.render).toHaveBeenCalledTimes(2))
  })
})
