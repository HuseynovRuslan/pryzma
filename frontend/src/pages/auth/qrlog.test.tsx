import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../api/client'
import type { PublicConfig } from '../../api/types'
import ProfilePage from '../account/ProfilePage'
import LoginPage from './LoginPage'
import { ForgotPasswordPage } from './PasswordPages'
import { FAILED_MESSAGE } from '../../lib/qrlog'
import QrlogReturnPage from './QrlogReturnPage'

const api = vi.hoisted(() => ({
  qrlogStart: vi.fn(),
  qrlogClaim: vi.fn(),
  qrlogCancel: vi.fn(),
  logins: vi.fn(),
  unlinkQrlog: vi.fn(),
  login: vi.fn(),
  forgotPassword: vi.fn(),
}))
const auth = vi.hoisted(() => ({ applySession: vi.fn(), login: vi.fn(), logout: vi.fn(), setUser: vi.fn(), retry: vi.fn() }))
const config = vi.hoisted(() => ({ value: { qrlogLogin: true, passwordReset: false, ready: true } as PublicConfig & { ready: boolean } }))
const who = vi.hoisted(() => ({ roles: ['Participant'] as string[], status: 'anonymous' as 'loading' | 'authenticated' | 'anonymous' | 'unavailable' }))

vi.mock('../../api/endpoints', () => ({
  authApi: {
    qrlogStart: api.qrlogStart,
    qrlogClaim: api.qrlogClaim,
    qrlogCancel: api.qrlogCancel,
    login: api.login,
    forgotPassword: api.forgotPassword,
  },
  meApi: { logins: api.logins, unlinkQrlog: api.unlinkQrlog },
}))
vi.mock('../../auth/AuthContext', () => ({
  useAuth: () => ({
    status: who.status,
    user: { id: 'u1', firstName: 'Günel', lastName: 'Şükürova', email: 'gunel@example.com', phone: null, roles: who.roles },
    ...auth,
  }),
}))
vi.mock('../../lib/publicConfig', () => ({ usePublicConfig: () => config.value }))
// The QR itself: its value is what matters here (the library's drawing is its own business).
vi.mock('qrcode.react', () => ({
  QRCodeSVG: (props: { value: string; 'aria-label'?: string }) => (
    <svg data-testid="qr" data-value={props.value} role="img" aria-label={props['aria-label']} />
  ),
}))

/** A phone (narrow screen, coarse pointer) — or, when false, a computer. jsdom has no matchMedia of its own. */
function setPhone(phone: boolean) {
  window.matchMedia = ((query: string) => ({
    matches: phone && query.includes('pointer: coarse'),
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia
}

const assign = vi.fn()
beforeAll(() => {
  Object.defineProperty(window, 'location', { value: { ...window.location, assign }, writable: true })
})
beforeEach(() => {
  vi.clearAllMocks()
  sessionStorage.clear()
  config.value = { qrlogLogin: true, passwordReset: false, ready: true }
  who.roles = ['Participant']
  who.status = 'anonymous'
  setPhone(false)
})

afterEach(() => {
  vi.useRealTimers()
})

function Where() {
  const l = useLocation()
  return <output data-testid="where">{l.pathname + l.search}</output>
}

function tree(path: string) {
  return (
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/login/qrlog" element={<QrlogReturnPage />} />
        <Route path="/forgot-password" element={<ForgotPasswordPage />} />
        <Route path="/my/profile" element={<ProfilePage />} />
        <Route path="*" element={<Where />} />
      </Routes>
    </MemoryRouter>
  )
}

function renderAt(path: string) {
  return render(tree(path))
}

describe('the sign-in page for someone with no account yet', () => {
  it('starts by saying that a first visit begins with registering, keeping the destination', () => {
    renderAt('/login?next=%2Fcompetitions%2Fmastaganin-simvolu%2Fsubmit')
    expect(screen.getByText(/Saytda ilk dəfəsinizsə/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Qeydiyyatdan keçin' })).toHaveAttribute(
      'href',
      '/register?next=%2Fcompetitions%2Fmastaganin-simvolu%2Fsubmit',
    )
  })
})

describe('a refused sign-in', () => {
  const signIn = async () => {
    renderAt('/login?next=%2Fcompetitions%2Fmastaganin-simvolu%2Fsubmit')
    await userEvent.type(screen.getByLabelText('E-mail'), 'aysel@example.com')
    await userEvent.type(screen.getByLabelText('Şifrə'), 'secret123')
    await userEvent.click(screen.getByRole('button', { name: 'Daxil ol' }))
  }

  it('says what usually lies behind it: no account yet, a forgotten password, or QRLog', async () => {
    auth.login.mockRejectedValue(new ApiError(401, 'Bu e-mail ilə hesab yoxdur. Əvvəlcə qeydiyyatdan keçin.'))
    await signIn()
    expect(await screen.findByRole('alert')).toHaveTextContent('Bu e-mail ilə hesab yoxdur.')
    expect(screen.getByText('Daxil ola bilmirsiniz?')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'qeydiyyatdan keçin' })).toHaveAttribute(
      'href',
      '/register?next=%2Fcompetitions%2Fmastaganin-simvolu%2Fsubmit',
    )
    expect(screen.getByRole('link', { name: 'şifrəni bərpa edin' })).toHaveAttribute('href', '/forgot-password')
    expect(screen.getByText(/«QRLog ilə daxil ol» düyməsi ilə girmisinizsə/)).toBeInTheDocument()
  })

  it('says nothing of the kind when the trouble is the connection', async () => {
    auth.login.mockRejectedValue(new ApiError(0, 'Serverlə əlaqə qurulmadı.'))
    await signIn()
    expect(await screen.findByRole('alert')).toHaveTextContent('Serverlə əlaqə qurulmadı.')
    expect(screen.queryByText('Daxil ola bilmirsiniz?')).not.toBeInTheDocument()
  })
})

describe('QRLog ilə daxil ol', () => {
  it('on a phone: is the first option on the login page and goes straight to QRLog with the destination kept', async () => {
    setPhone(true)
    api.qrlogStart.mockResolvedValue({ authorizeUrl: 'https://app.qrlog.az/signin/meydan?code=abc&return=x', scanUrl: '', expiresAt: '' })
    renderAt('/login?next=%2Fcompetitions%2Fsevron%2Fsubmit%3Fcategory%3Dmastaga')
    const button = screen.getByRole('button', { name: 'QRLog ilə daxil ol' })
    // The word "QRLog" on the button is QRLog's own logo, not a redrawn mark.
    expect(within(button).getByRole('img', { name: 'QRLog' })).toHaveAttribute('src', '/brand/qrlog-logo.png')
    const form = screen.getByLabelText('E-mail').closest('form')!
    expect(button.compareDocumentPosition(form) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(screen.getByRole('separator', { name: 'və ya' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'iştirak şərtləri' })).toHaveAttribute('href', '/terms')
    expect(screen.getByText('QRLog tətbiqində davam edin')).toBeInTheDocument()

    await userEvent.click(button)
    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://app.qrlog.az/signin/meydan?code=abc&return=x'))
    expect(api.qrlogStart).toHaveBeenCalledWith({ returnTo: '/competitions/sevron/submit?category=mastaga', link: false })
    expect(sessionStorage.getItem('meydan.qrlog.next')).toBe('/competitions/sevron/submit?category=mastaga')
    // The e-mail form is still there.
    expect(screen.getByRole('button', { name: 'Daxil ol' })).toBeInTheDocument()
  })

  it('is not offered where the deployment has no QRLog door', () => {
    config.value = { qrlogLogin: false, passwordReset: true, ready: true }
    renderAt('/login')
    expect(screen.queryByRole('button', { name: /QRLog/ })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Daxil ol' })).toBeInTheDocument()
  })

  it('finishes the sign-in when QRLog has vouched, applying the session and continuing where the person was going', async () => {
    const session = { accessToken: 't', accessTokenExpiresAt: '', user: { id: 'u', firstName: 'A', lastName: 'B', email: '', phone: null, roles: ['Participant'] } }
    api.qrlogClaim.mockResolvedValue({ status: 'signed-in', returnTo: '/competitions/sevron/submit?category=mastaga', session })
    renderAt('/login/qrlog')
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/competitions/sevron/submit?category=mastaga'))
    expect(auth.applySession).toHaveBeenCalledWith(session)
  })

  it('waits for the session restore before claiming, so a link claim carries the restored account', async () => {
    // Coming back from QRLog is a full page load: the refresh cookie is still being exchanged for a token.
    who.status = 'loading'
    api.qrlogClaim.mockResolvedValue({ status: 'linked', returnTo: '/my/profile' })
    const view = renderAt('/login/qrlog')
    await new Promise((r) => setTimeout(r, 50))
    expect(api.qrlogClaim).not.toHaveBeenCalled()
    expect(screen.getByRole('heading', { name: 'QRLog təsdiqi gözlənilir' })).toBeInTheDocument()

    who.status = 'authenticated'
    api.logins.mockResolvedValue({ hasPassword: true, qrlog: true })
    view.rerender(tree('/login/qrlog'))
    expect(await screen.findByText(/QRLog hesabı bağlandı/)).toBeInTheDocument()
    expect(api.qrlogClaim).toHaveBeenCalledTimes(1)
  })

  it('claims an anonymous sign-in only once the restore has found no session, so nothing late can undo it', async () => {
    who.status = 'loading'
    const session = { accessToken: 't', accessTokenExpiresAt: '', user: { id: 'u', firstName: 'A', lastName: 'B', email: '', phone: null, roles: ['Participant'] } }
    api.qrlogClaim.mockResolvedValue({ status: 'signed-in', returnTo: '/competitions/sevron', session })
    const view = renderAt('/login/qrlog')
    await new Promise((r) => setTimeout(r, 50))
    expect(api.qrlogClaim).not.toHaveBeenCalled()

    who.status = 'anonymous'
    view.rerender(tree('/login/qrlog'))
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/competitions/sevron'))
    expect(auth.applySession).toHaveBeenCalledWith(session)
    expect(api.qrlogClaim).toHaveBeenCalledTimes(1)
  })

  it('does not spend the ticket while the session check is throttled, and claims once it has an answer', async () => {
    // The restore was answered 429 (or the server was out of reach): not signed in, not signed out — unknown.
    who.status = 'unavailable'
    api.qrlogClaim.mockResolvedValue({ status: 'linked', returnTo: '/my/profile' })
    const view = renderAt('/login/qrlog')
    await new Promise((r) => setTimeout(r, 50))
    expect(api.qrlogClaim).not.toHaveBeenCalled()
    expect(screen.getByRole('status')).toHaveTextContent('Giriş vəziyyətiniz bu an yoxlanıla bilmədi')
    await userEvent.click(screen.getByRole('button', { name: 'İndi yenidən yoxla' }))
    expect(auth.retry).toHaveBeenCalledTimes(1)

    who.status = 'authenticated'
    api.logins.mockResolvedValue({ hasPassword: true, qrlog: true })
    view.rerender(tree('/login/qrlog'))
    expect(await screen.findByText(/QRLog hesabı bağlandı/)).toBeInTheDocument()
    expect(api.qrlogClaim).toHaveBeenCalledTimes(1)
  })

  it('shows a safe message, not a protocol error, when the ticket is gone, and offers the e-mail way in', async () => {
    api.qrlogClaim.mockResolvedValue({ status: 'gone', returnTo: '/' })
    sessionStorage.setItem('meydan.qrlog.next', '/my/submissions')
    renderAt('/login/qrlog')
    expect(await screen.findByRole('alert')).toHaveTextContent(FAILED_MESSAGE)
    expect(screen.getByRole('link', { name: 'E-mail ilə daxil ol' })).toHaveAttribute('href', '/login?next=%2Fmy%2Fsubmissions')
    api.qrlogStart.mockResolvedValue({ authorizeUrl: 'https://app.qrlog.az/signin/meydan?code=again', expiresAt: '' })
    await userEvent.click(screen.getByRole('button', { name: 'Yenidən cəhd et' }))
    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://app.qrlog.az/signin/meydan?code=again'))
    expect(api.qrlogStart).toHaveBeenCalledWith({ returnTo: '/my/submissions', link: false })
  })

  it('refuses a blind account takeover and explains explicit linking when the e-mail already has an account', async () => {
    api.qrlogClaim.mockResolvedValue({ status: 'email-taken', returnTo: '/', email: 'aysel@example.com' })
    renderAt('/login/qrlog')
    expect(await screen.findByRole('alert')).toHaveTextContent('aysel@example.com')
    expect(screen.getByText(/QRLog hesabını bağla/)).toBeInTheDocument()
    expect(auth.applySession).not.toHaveBeenCalled()
  })

  it('tells a browser that did not start the sign-in where it will finish', async () => {
    api.qrlogClaim.mockRejectedValue(new ApiError(404, 'Bu brauzerdə başlanmış QRLog girişi yoxdur.'))
    renderAt('/login/qrlog')
    expect(await screen.findByRole('heading', { name: 'Giriş başqa brauzerdə başlayıb' })).toBeInTheDocument()
  })

  it('ends the ticket when the person cancelled at QRLog', async () => {
    api.qrlogCancel.mockResolvedValue(undefined)
    renderAt('/login/qrlog?cancelled=1')
    expect(await screen.findByRole('alert')).toHaveTextContent('ləğv edildi')
    expect(api.qrlogCancel).toHaveBeenCalled()
    expect(api.qrlogClaim).not.toHaveBeenCalled()
  })
})

describe('Şifrəni bərpa et without a mail provider', () => {
  it('says so instead of promising a message, and offers no address field', () => {
    renderAt('/forgot-password')
    expect(screen.getByRole('status')).toHaveTextContent('hələ aktiv deyil')
    expect(screen.queryByLabelText('E-mail')).not.toBeInTheDocument()
    expect(api.forgotPassword).not.toHaveBeenCalled()
  })

  it('offers the form when mail can be sent', () => {
    config.value = { qrlogLogin: false, passwordReset: true, ready: true }
    renderAt('/forgot-password')
    expect(screen.getByLabelText('E-mail')).toBeInTheDocument()
  })
})

describe('Bağlı hesablar', () => {
  it('offers to link QRLog to a password account (on a phone: straight to QRLog)', async () => {
    setPhone(true)
    api.logins.mockResolvedValue({ hasPassword: true, qrlog: false })
    api.qrlogStart.mockResolvedValue({ authorizeUrl: 'https://app.qrlog.az/signin/meydan?code=link', scanUrl: '', expiresAt: '' })
    renderAt('/my/profile')
    const button = await screen.findByRole('button', { name: /QRLog hesabını bağla/ })
    await waitFor(() => expect(button).toBeEnabled())
    await userEvent.click(button)
    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://app.qrlog.az/signin/meydan?code=link'))
    expect(api.qrlogStart).toHaveBeenCalledWith({ returnTo: '/my/profile', link: true })
  })

  it('shows the linked state, unlinks, and keeps a password-less account from losing its only way in', async () => {
    api.logins.mockResolvedValue({ hasPassword: true, qrlog: true })
    api.unlinkQrlog.mockResolvedValue(undefined)
    renderAt('/my/profile?linked=1')
    // "QRLog" here is QRLog's own logo (the wordmark image), followed by the words.
    const linked = (await screen.findByText(/hesabı bağlıdır/)).closest('p')!
    expect(within(linked).getByRole('img', { name: 'QRLog' })).toHaveAttribute('src', '/brand/qrlog-logo.png')
    expect(screen.getByRole('status')).toHaveTextContent('QRLog hesabı bağlandı')
    await userEvent.click(screen.getByRole('button', { name: 'Bağlantını ayır' }))
    await waitFor(() => expect(api.unlinkQrlog).toHaveBeenCalled())
    expect(await screen.findByRole('button', { name: /QRLog hesabını bağla/ })).toBeInTheDocument()

    api.logins.mockResolvedValue({ hasPassword: false, qrlog: true })
    renderAt('/my/profile')
    const buttons = await screen.findAllByRole('button', { name: 'Bağlantını ayır' })
    const unlink = buttons[buttons.length - 1]
    expect(unlink).toBeDisabled()
    expect(screen.getByText(/əvvəlcə “Şifrəni unutmuşam” ilə şifrə təyin edin/)).toBeInTheDocument()
  })

  it('offers no QRLog link on an administrator account and says why', async () => {
    who.roles = ['Admin', 'Participant']
    api.logins.mockResolvedValue({ hasPassword: true, qrlog: false })
    renderAt('/my/profile')
    expect(await screen.findByText(/İdarəetmə hesabı QRLog ilə bağlanmır/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /QRLog hesabını bağla/ })).not.toBeInTheDocument()
  })
})

describe('QRLog ilə daxil ol — on a computer (QR for the phone)', () => {
  const SCAN = 'https://app.qrlog.az/signin/meydan?code=0123456789abcdef0123456789abcdef'
  const inFiveMinutes = () => new Date(Date.now() + 5 * 60_000).toISOString()
  const session = { accessToken: 't', accessTokenExpiresAt: '', user: { id: 'u', firstName: 'A', lastName: 'B', email: '', phone: null, roles: ['Participant'] } }

  async function openQr() {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    renderAt('/login?next=%2Fcompetitions%2Fsevron%2Fsubmit%3Fcategory%3Dbileceri')
    const button = screen.getByRole('button', { name: 'QRLog ilə daxil ol' })
    expect(button).toHaveAttribute('aria-haspopup', 'dialog')
    await userEvent.click(button)
    return { button, dialog: await screen.findByRole('dialog', { name: 'QRLog ilə daxil ol' }) }
  }

  it('opens a QR that holds only the approval address with the ticket code, with the steps beside it', async () => {
    api.qrlogStart.mockResolvedValue({ authorizeUrl: 'https://app.qrlog.az/x?code=c&return=r', scanUrl: SCAN, expiresAt: inFiveMinutes() })
    api.qrlogClaim.mockResolvedValue({ status: 'pending', returnTo: '/' })
    const { dialog } = await openQr()

    const qr = await within(dialog).findByTestId('qr')
    expect(qr).toHaveAttribute('data-value', SCAN)
    expect(qr).toHaveAccessibleName(/QRLog tətbiqi ilə skan edin/)
    expect(assign).not.toHaveBeenCalled() // the computer does not go to QRLog itself
    expect(api.qrlogStart).toHaveBeenCalledWith({ returnTo: '/competitions/sevron/submit?category=bileceri', link: false })
    expect(within(dialog).getByText('QRLog tətbiqində skan edin')).toBeInTheDocument()
    expect(within(dialog).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'QRLog tətbiqini açın',
      'Xidmətlər → MEYDAN v1',
      'QR kodu skan edin',
      'Girişi təsdiqləyin',
    ])
    expect(within(dialog).getByRole('status')).toHaveTextContent('Telefonda təsdiq gözlənilir')
  })

  it('never counts down from more than a ticket lives, even when this computer\'s clock is behind the server', async () => {
    // A slow start (1.5 s) from a server whose clock is 4 s ahead.
    api.qrlogStart.mockImplementation(
      () => new Promise((ok) => setTimeout(() => ok({ authorizeUrl: 'x', scanUrl: SCAN, expiresAt: new Date(Date.now() + 5 * 60_000 + 4_000).toISOString() }), 1_500)),
    )
    api.qrlogClaim.mockResolvedValue({ status: 'pending', returnTo: '/' })
    const { dialog } = await openQr()
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1_600)
    })
    await within(dialog).findByTestId('qr')
    expect(within(dialog).getByRole('status')).toHaveTextContent(/[45]:\d\d/)
    expect(within(dialog).getByRole('status')).not.toHaveTextContent(/5:0[1-9]/)
  })

  it('notices the approval by itself, says "Təsdiqləndi" and continues where the person was going', async () => {
    api.qrlogStart.mockResolvedValue({ authorizeUrl: 'x', scanUrl: SCAN, expiresAt: inFiveMinutes() })
    api.qrlogClaim
      .mockResolvedValueOnce({ status: 'pending', returnTo: '/' })
      .mockResolvedValueOnce({ status: 'signed-in', returnTo: '/competitions/sevron/submit?category=bileceri', session })
    const { dialog } = await openQr()
    await within(dialog).findByTestId('qr')

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2600) // first poll: pending
    })
    expect(api.qrlogClaim).toHaveBeenCalledTimes(1)
    await act(async () => {
      await vi.advanceTimersByTimeAsync(2600) // second poll: approved on the phone
    })
    // "Təsdiqləndi" is on screen before the session is applied (so the page cannot move on under it)…
    expect(within(dialog).getAllByText(/Təsdiqləndi/).length).toBeGreaterThan(0)
    expect(auth.applySession).not.toHaveBeenCalled()
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000)
    })
    expect(auth.applySession).toHaveBeenCalledWith(session) // …then the session, then the destination
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/competitions/sevron/submit?category=bileceri'))
    expect(api.qrlogClaim).toHaveBeenCalledTimes(2) // and no more polling after the answer
  })

  it('an expired QR says so and makes a new one on request — a new ticket, never the old code', async () => {
    api.qrlogStart
      .mockResolvedValueOnce({ authorizeUrl: 'x', scanUrl: SCAN, expiresAt: inFiveMinutes() })
      .mockResolvedValueOnce({ authorizeUrl: 'x', scanUrl: SCAN.replace('0123', '9999'), expiresAt: inFiveMinutes() })
    api.qrlogClaim.mockResolvedValue({ status: 'gone', returnTo: '/' })
    const { dialog } = await openQr()
    await within(dialog).findByTestId('qr')
    await act(async () => {
      await vi.advanceTimersByTimeAsync(2600)
    })
    expect(within(dialog).getByText('QR kodun vaxtı bitdi')).toBeInTheDocument()
    expect(within(dialog).queryByTestId('qr')).not.toBeInTheDocument()
    expect(within(dialog).getByRole('status')).toHaveTextContent('QR kodun vaxtı bitdi')

    await userEvent.click(within(dialog).getByRole('button', { name: 'Yeni QR kod yarat' }))
    expect(await within(dialog).findByTestId('qr')).toHaveAttribute('data-value', SCAN.replace('0123', '9999'))
    expect(api.qrlogStart).toHaveBeenCalledTimes(2)
  })

  it('closes with Escape, ends the ticket, stops asking and gives focus back to the button', async () => {
    api.qrlogStart.mockResolvedValue({ authorizeUrl: 'x', scanUrl: SCAN, expiresAt: inFiveMinutes() })
    api.qrlogClaim.mockResolvedValue({ status: 'pending', returnTo: '/' })
    api.qrlogCancel.mockResolvedValue(undefined)
    const { button, dialog } = await openQr()
    await within(dialog).findByTestId('qr')
    expect(within(dialog).getByRole('button', { name: 'Bağla' })).toHaveFocus()

    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(api.qrlogCancel).toHaveBeenCalledTimes(1)
    expect(button).toHaveFocus()
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000)
    })
    expect(api.qrlogClaim).not.toHaveBeenCalled()
  })

  it('links QRLog from the profile with a QR too, and says so when the phone has approved', async () => {
    api.logins.mockResolvedValueOnce({ hasPassword: true, qrlog: false }).mockResolvedValue({ hasPassword: true, qrlog: true })
    api.qrlogStart.mockResolvedValue({ authorizeUrl: 'x', scanUrl: SCAN, expiresAt: inFiveMinutes() })
    api.qrlogClaim.mockResolvedValue({ status: 'linked', returnTo: '/my/profile' })
    vi.useFakeTimers({ shouldAdvanceTime: true })
    renderAt('/my/profile')
    const button = await screen.findByRole('button', { name: /QRLog hesabını bağla/ })
    await waitFor(() => expect(button).toBeEnabled())
    await userEvent.click(button)
    const dialog = await screen.findByRole('dialog', { name: 'QRLog hesabını bağla' })
    await within(dialog).findByTestId('qr')
    expect(api.qrlogStart).toHaveBeenCalledWith({ returnTo: '/my/profile', link: true })
    expect(assign).not.toHaveBeenCalled()

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2600 + 1000)
    })
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(await screen.findByText(/QRLog hesabı bağlandı/)).toBeInTheDocument()
  })
})
