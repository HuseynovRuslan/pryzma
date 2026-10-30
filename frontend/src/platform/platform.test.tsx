import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { PlatformSettings } from '../api/types'

const catalog = vi.hoisted(() => ({ settings: vi.fn(), home: vi.fn(), competitions: vi.fn() }))
vi.mock('../api/endpoints', () => ({ catalog, submissionsApi: {} }))
const auth = vi.hoisted(() => ({ value: { status: 'anonymous', user: null } as Record<string, unknown> }))
vi.mock('../auth/AuthContext', () => ({ useAuth: () => auth.value }))

const { Footer } = await import('../components/Footer')
const { Header } = await import('../components/Header')
const { PlatformProvider } = await import('./PlatformProvider')
const { AuthLayout } = await import('../pages/auth/AuthLayout')
const HomePage = (await import('../pages/HomePage')).default

const settings = (over: Partial<PlatformSettings> = {}): PlatformSettings => ({
  platformName: 'PRIZMA',
  homeHeroTitle: 'İdeyanı meydana çıxar',
  homeHeroSubtitle: 'Yaradıcı layihələr və açıq müsabiqələr üçün vahid platforma',
  authPromoTitle: 'Bir hesab, bütün müsabiqələr',
  authPromoImage: null,
  ...over,
})

const show = (ui: React.ReactNode) =>
  render(
    <MemoryRouter>
      <PlatformProvider>{ui}</PlatformProvider>
    </MemoryRouter>,
  )

describe('platform settings', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    catalog.settings.mockResolvedValue(settings())
    catalog.home.mockResolvedValue({ projects: [], openCompetitions: [] })
  })

  it('takes the brand in the header and the footer from the settings, not from the code', async () => {
    catalog.settings.mockResolvedValue(settings({ platformName: 'SƏNƏT MEYDANI' }))
    show(
      <>
        <Header />
        <Footer />
      </>,
    )

    // The header's brand is a picture, so the name reaches it as the link's label and the picture's alt text.
    await waitFor(() => expect(screen.getByLabelText('SƏNƏT MEYDANI — ana səhifə')).toBeInTheDocument())
    expect(screen.getByAltText('SƏNƏT MEYDANI')).toBeInTheDocument()
    expect(screen.getByText('SƏNƏT MEYDANI')).toBeInTheDocument()
    expect(screen.getByText(`© ${new Date().getFullYear()} SƏNƏT MEYDANI`)).toBeInTheDocument()
  })

  it('keeps the bundled PRIZMA defaults when the settings request fails', async () => {
    catalog.settings.mockRejectedValue(new Error('offline'))
    show(
      <>
        <Header />
        <Footer />
      </>,
    )

    // Not a blank header and not a crash: the site is still the right site.
    await waitFor(() => expect(catalog.settings).toHaveBeenCalled())
    expect(screen.getByAltText('PRIZMA')).toBeInTheDocument()
    expect(screen.getByText('PRIZMA')).toBeInTheDocument()
    expect(screen.getByText('Yaradıcı layihələr və açıq müsabiqələr üçün vahid platforma')).toBeInTheDocument()
  })

  it('shows any configured headline, not three fixed words', async () => {
    catalog.settings.mockResolvedValue(
      settings({ homeHeroTitle: 'Şəhəri birlikdə yenidən çəkək', homeHeroSubtitle: 'Yeni alt mətn.' }),
    )
    const { container } = show(<HomePage />)

    await waitFor(() => expect(container.querySelector('.hero__title')).toHaveTextContent('Şəhəri birlikdə yenidən çəkək'))
    expect(screen.getByText('Yeni alt mətn.')).toBeInTheDocument()
    // Every word is its own element, so the entrance still runs whatever the text is.
    expect(container.querySelectorAll('.hero__word')).toHaveLength(4)
    expect(container.textContent).not.toContain('İdeyanı')
  })

  it('uses the configured picture beside the sign-in form', async () => {
    catalog.settings.mockResolvedValue(settings({ authPromoImage: '/api/media/covers/2026/09/auth.webp' }))
    const { container } = show(
      <AuthLayout title="Daxil ol">
        <p>forma</p>
      </AuthLayout>,
    )

    await waitFor(() =>
      expect(container.querySelector('.auth__image')).toHaveAttribute('src', '/api/media/covers/2026/09/auth.webp'),
    )
  })

  it('falls back to the picture the app ships with when none is configured', async () => {
    const { container } = show(
      <AuthLayout title="Daxil ol">
        <p>forma</p>
      </AuthLayout>,
    )

    await waitFor(() =>
      expect(container.querySelector('.auth__image')).toHaveAttribute('src', '/images/projects/prizma.jpeg'),
    )
  })
})
