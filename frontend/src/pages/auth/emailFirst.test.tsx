import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../api/client'
import { RegisterCompletePage, RegisterVerifyPage } from './EmailFirstRegistration'
import RegisterPage from './RegisterPage'

const api = vi.hoisted(() => ({ registrationStart: vi.fn(), registrationVerify: vi.fn(), registrationComplete: vi.fn() }))
const auth = vi.hoisted(() => ({ register: vi.fn(), applySession: vi.fn(), status: 'anonymous' as const }))
const config = vi.hoisted(() => ({
  value: { qrlogLogin: false, passwordReset: true, captchaSiteKey: 'site' as string | null, registrationFlow: 'email-first' as const, ready: true },
}))
vi.mock('../../api/endpoints', () => ({ authApi: api }))
vi.mock('../../auth/AuthContext', () => ({ useAuth: () => auth }))
vi.mock('../../lib/publicConfig', () => ({ usePublicConfig: () => config.value }))

const widget = { render: vi.fn(), remove: vi.fn(), callbacks: [] as ((t: string) => void)[] }

beforeEach(() => {
  vi.clearAllMocks()
  sessionStorage.clear()
  widget.callbacks = []
  widget.render.mockImplementation((_el: HTMLElement, o: { callback: (t: string) => void }) => {
    widget.callbacks.push(o.callback)
    return `w${widget.callbacks.length}`
  })
  window.turnstile = { render: widget.render, remove: widget.remove }
})
afterEach(() => {
  delete window.turnstile
  window.history.replaceState(null, '', '/')
})

const future = () => new Date(Date.now() + 20 * 60_000).toISOString()

describe('email-first registration, step 1', () => {
  it('offers no form to type into until the site says which one it uses', () => {
    config.value = { ...config.value, ready: false }
    try {
      render(
        <MemoryRouter>
          <RegisterPage />
        </MemoryRouter>,
      )
      expect(screen.getByRole('status')).toHaveTextContent('Yüklənir…')
      for (const label of ['E-mail', /^Ad/, /^Telefon/, /^Şifrə$/]) expect(screen.queryByLabelText(label)).not.toBeInTheDocument()
    } finally {
      config.value = { ...config.value, ready: true }
    }
  })

  it('asks only for the address and the robot check, and creates nothing itself', async () => {
    render(
      <MemoryRouter>
        <RegisterPage />
      </MemoryRouter>,
    )
    expect(screen.getByRole('heading', { name: 'E-mailinizi təsdiqləyin' })).toBeInTheDocument()
    expect(screen.getByText('Qeydiyyata davam etmək üçün e-mail ünvanınıza təsdiq linki göndərəcəyik.')).toBeInTheDocument()
    expect(screen.getByLabelText('E-mail')).toBeInTheDocument()
    for (const label of [/^Ad/, /^Soyad/, /^Telefon/, /^Şifrə$/]) expect(screen.queryByLabelText(label)).not.toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('E-mail'), { target: { value: 'aysel@example.com' } })
    fireEvent.click(screen.getByRole('button', { name: 'Təsdiq linki göndər' }))
    expect(await screen.findByText(/Robot olmadığınızı təsdiqləyin/)).toBeInTheDocument()
    expect(api.registrationStart).not.toHaveBeenCalled()

    await waitFor(() => expect(widget.callbacks.length).toBeGreaterThan(0))
    act(() => widget.callbacks[0]('tok-1'))
    api.registrationStart.mockResolvedValue(undefined)
    fireEvent.click(screen.getByRole('button', { name: 'Təsdiq linki göndər' }))
    expect(await screen.findByRole('heading', { name: 'Təsdiq linkini göndərdik' })).toBeInTheDocument()
    expect(api.registrationStart).toHaveBeenCalledWith({ email: 'aysel@example.com', captchaToken: 'tok-1' })
    expect(auth.register).not.toHaveBeenCalled()
  })

  it('sends a new link on request, with a fresh robot-check token, and says when asked too often', async () => {
    api.registrationStart.mockResolvedValue(undefined)
    render(
      <MemoryRouter>
        <RegisterPage />
      </MemoryRouter>,
    )
    fireEvent.change(screen.getByLabelText('E-mail'), { target: { value: 'aysel@example.com' } })
    await waitFor(() => expect(widget.callbacks.length).toBe(1))
    act(() => widget.callbacks[0]('tok-1'))
    fireEvent.click(screen.getByRole('button', { name: 'Təsdiq linki göndər' }))
    await screen.findByRole('heading', { name: 'Təsdiq linkini göndərdik' })
    // Asking again does not cancel the mails already sent, and there is a way to the organiser.
    expect(screen.getByText(/Əvvəlki məktublardakı linklər də 45 dəqiqə ərzində işləyir/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'bizə yazın' })).toHaveAttribute('href', '/contact')

    await waitFor(() => expect(widget.callbacks.length).toBe(2))
    act(() => widget.callbacks[1]('tok-2'))
    api.registrationStart.mockRejectedValueOnce(new ApiError(429, 'Link az əvvəl göndərildi. Bir dəqiqə sonra yenidən istəyin.'))
    fireEvent.click(screen.getByRole('button', { name: 'Linki yenidən göndər' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Bir dəqiqə sonra')
    expect(api.registrationStart).toHaveBeenLastCalledWith({ email: 'aysel@example.com', captchaToken: 'tok-2' })
  })
})

describe('email-first registration, an address that already has an account', () => {
  const submit = async (email: string) => {
    fireEvent.change(screen.getByLabelText('E-mail'), { target: { value: email } })
    await waitFor(() => expect(widget.callbacks.length).toBeGreaterThan(0))
    act(() => widget.callbacks[widget.callbacks.length - 1]('tok'))
    fireEvent.click(screen.getByRole('button', { name: 'Təsdiq linki göndər' }))
  }

  const page = () =>
    render(
      <MemoryRouter initialEntries={['/register']}>
        <Routes>
          <Route path="/register" element={<RegisterPage />} />
          <Route path="/login" element={<p>giriş səhifəsi</p>} />
          <Route path="/forgot-password" element={<p>bərpa səhifəsi</p>} />
        </Routes>
      </MemoryRouter>,
    )

  it('says so plainly, with the ways in, and never "Link göndərilmədi"', async () => {
    api.registrationStart.mockRejectedValue(new ApiError(409, 'Bu e-mail ilə artıq hesab mövcuddur'))
    page()
    await submit('aysel@example.com')
    expect(await screen.findByRole('heading', { name: 'Bu e-mail ilə artıq hesab mövcuddur' })).toBeInTheDocument()
    expect(screen.getByText('Daxil olun və ya şifrənizi unutmusunuzsa, bərpa edin.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Daxil ol' })).toHaveAttribute('href', expect.stringMatching(/^\/login/))
    expect(screen.getByRole('link', { name: 'Şifrəni unutmuşam' })).toHaveAttribute('href', '/forgot-password')
    expect(document.body.textContent).not.toMatch(/Link göndərilmədi/)
    expect(screen.queryByRole('heading', { name: 'Təsdiq linkini göndərdik' })).not.toBeInTheDocument()
  })

  it('takes the person to sign in, or to the password reset', async () => {
    api.registrationStart.mockRejectedValue(new ApiError(409, 'Bu e-mail ilə artıq hesab mövcuddur'))
    page()
    await submit('aysel@example.com')
    fireEvent.click(await screen.findByRole('link', { name: 'Daxil ol' }))
    expect(await screen.findByText('giriş səhifəsi')).toBeInTheDocument()
  })

  it('opens the password reset from the same panel', async () => {
    api.registrationStart.mockRejectedValue(new ApiError(409, 'Bu e-mail ilə artıq hesab mövcuddur'))
    page()
    await submit('aysel@example.com')
    fireEvent.click(await screen.findByRole('link', { name: 'Şifrəni unutmuşam' }))
    expect(await screen.findByText('bərpa səhifəsi')).toBeInTheDocument()
  })

  it('lets another address be tried, which then follows the normal flow', async () => {
    api.registrationStart.mockRejectedValueOnce(new ApiError(409, 'Bu e-mail ilə artıq hesab mövcuddur'))
    page()
    await submit('aysel@example.com')
    fireEvent.click(await screen.findByRole('button', { name: 'Başqa e-mail yaz' }))
    api.registrationStart.mockResolvedValueOnce(undefined)
    await submit('yeni@example.com')
    expect(await screen.findByRole('heading', { name: 'Təsdiq linkini göndərdik' })).toBeInTheDocument()
  })

  it('shows the limit message when there were too many tries', async () => {
    api.registrationStart.mockRejectedValue(new ApiError(429, 'Çox sayda cəhd edildi. Bir qədər sonra yenidən yoxlayın.'))
    page()
    await submit('aysel@example.com')
    expect(await screen.findByRole('alert')).toHaveTextContent('Çox sayda cəhd edildi. Bir qədər sonra yenidən yoxlayın.')
  })
})

describe('email-first registration, step 2', () => {
  it('spends the link at once and moves on to the form, without the token in the address bar', async () => {
    api.registrationVerify.mockResolvedValue({ proof: 'p-1', email: 'aysel@example.com', expiresAt: future() })
    window.history.replaceState(null, '', '/register/verify#token=abc')
    render(
      <MemoryRouter initialEntries={['/register/verify']}>
        <Routes>
          <Route path="/register/verify" element={<RegisterVerifyPage />} />
          <Route path="/register/complete" element={<p>tamamla</p>} />
        </Routes>
      </MemoryRouter>,
    )
    expect(await screen.findByText('tamamla')).toBeInTheDocument()
    expect(api.registrationVerify).toHaveBeenCalledWith({ token: 'abc' })
    expect(JSON.parse(sessionStorage.getItem('registration.proof')!).proof).toBe('p-1')
    expect(window.location.hash).toBe('')
  })

  it('says so when the link is used or old, and where to turn when it keeps failing', async () => {
    api.registrationVerify.mockRejectedValue(
      new ApiError(400, 'x', {
        token: ['Bu link artıq işləmir: ya istifadə olunub, ya da vaxtı bitib. Ən son gələn məktubdakı linki açın və ya yeni link istəyin.'],
      }),
    )
    window.history.replaceState(null, '', '/register/verify#token=old')
    render(
      <MemoryRouter>
        <RegisterVerifyPage />
      </MemoryRouter>,
    )
    expect(await screen.findByRole('alert')).toHaveTextContent('Ən son gələn məktubdakı linki açın')
    expect(screen.getByRole('link', { name: 'Yeni link istə' })).toHaveAttribute('href', '/register')
    expect(screen.getByRole('link', { name: 'Əlaqə' })).toHaveAttribute('href', '/contact')
  })
})

describe('email-first registration, step 3', () => {
  const fill = () => {
    const type = (label: RegExp, value: string) => fireEvent.change(screen.getByLabelText(label), { target: { value } })
    type(/^Ad/, 'Aysel')
    type(/^Soyad/, 'Məmmədova')
    type(/^Telefon/, '050 123 45 67')
    type(/^Şifrə$/, 'secret123')
    type(/^Şifrəni təkrar/, 'secret123')
    fireEvent.click(screen.getByRole('checkbox'))
  }

  it('without a proof sends the person back to the e-mail step', () => {
    render(
      <MemoryRouter>
        <RegisterCompletePage />
      </MemoryRouter>,
    )
    expect(screen.getByRole('heading', { name: 'Qeydiyyatın vaxtı bitib' })).toBeInTheDocument()
    expect(screen.queryByLabelText(/^Telefon/)).not.toBeInTheDocument()
  })

  it('creates the account with the proof, signs in and forgets the proof', async () => {
    sessionStorage.setItem('registration.proof', JSON.stringify({ proof: 'p-1', email: 'aysel@example.com', expiresAt: future() }))
    const session = { accessToken: 't', accessTokenExpiresAt: future(), user: { id: 'u' } }
    api.registrationComplete.mockResolvedValue(session)
    render(
      <MemoryRouter>
        <RegisterCompletePage />
      </MemoryRouter>,
    )
    expect(screen.getByText('aysel@example.com təsdiqləndi. Qeydiyyatı tamamlayın.')).toBeInTheDocument()
    fill()
    fireEvent.click(screen.getByRole('button', { name: 'Hesab yarat' }))
    await waitFor(() => expect(auth.applySession).toHaveBeenCalledWith(session))
    expect(api.registrationComplete).toHaveBeenCalledWith(expect.objectContaining({ proof: 'p-1', phone: '050 123 45 67', firstName: 'Aysel' }))
    expect(sessionStorage.getItem('registration.proof')).toBeNull()
  })

  it('keeps the form for a refused phone, and goes back to the e-mail step when the proof ran out', async () => {
    sessionStorage.setItem('registration.proof', JSON.stringify({ proof: 'p-1', email: 'aysel@example.com', expiresAt: future() }))
    render(
      <MemoryRouter>
        <RegisterCompletePage />
      </MemoryRouter>,
    )
    fill()
    api.registrationComplete.mockRejectedValueOnce(new ApiError(400, 'x', { phone: ['Bu telefon nömrəsi ilə artıq hesab açılıb.'] }))
    fireEvent.click(screen.getByRole('button', { name: 'Hesab yarat' }))
    expect(await screen.findByText('Bu telefon nömrəsi ilə artıq hesab açılıb.')).toBeInTheDocument()

    api.registrationComplete.mockRejectedValueOnce(new ApiError(400, 'x', { proof: ['Qeydiyyatın vaxtı bitib. E-mailinizi yenidən təsdiqləyin.'] }))
    fireEvent.click(screen.getByRole('button', { name: 'Hesab yarat' }))
    expect(await screen.findByRole('heading', { name: 'Qeydiyyatın vaxtı bitib' })).toBeInTheDocument()
    expect(sessionStorage.getItem('registration.proof')).toBeNull()
  })
})
