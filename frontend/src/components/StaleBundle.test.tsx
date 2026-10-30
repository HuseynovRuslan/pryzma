import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { isStaleBundleError } from '../lib/staleBundle'
import { StaleBundleBoundary } from './StaleBundle'

/** Every engine words a failed dynamic import differently, and this is all we get to recognise it by. */
const chunkFailures = [
  'Failed to fetch dynamically imported module: https://meydan.qrlog.az/assets/CompetitionDetailPage-CBdacg2W.js',
  'error loading dynamically imported module',
  'Importing a module script failed.',
  'Unable to preload CSS for /assets/index-VP-fK6_2.css',
]

describe('stale bundle', () => {
  const reload = vi.fn()
  let consoleError: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    reload.mockClear()
    Object.defineProperty(window, 'location', { value: { ...window.location, reload }, writable: true })
    // React prints the caught error; the test is about what the person sees, not about that noise.
    consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})
  })

  afterEach(() => consoleError.mockRestore())

  it.each(chunkFailures)('recognises "%s" as a bundle that went stale', (message) => {
    expect(isStaleBundleError(new Error(message))).toBe(true)
  })

  it('does not mistake an ordinary bug for a deploy', () => {
    expect(isStaleBundleError(new TypeError("Cannot read properties of undefined (reading 'map')"))).toBe(false)
    expect(isStaleBundleError(undefined)).toBe(false)
  })

  it('offers a refresh instead of a blank page when a chunk will not load', async () => {
    function Boom(): never {
      throw new Error('Failed to fetch dynamically imported module: /assets/Page-abc123.js')
    }
    render(
      <StaleBundleBoundary>
        <Boom />
      </StaleBundleBoundary>,
    )

    expect(screen.getByRole('alert')).toHaveTextContent('Yeni versiya mövcuddur')
    await userEvent.click(screen.getByRole('button', { name: 'Səhifəni yenilə' }))
    expect(reload).toHaveBeenCalledOnce()
  })

  it('catches the preload failure Vite reports before React sees it', async () => {
    render(
      <StaleBundleBoundary>
        <p>Səhifə</p>
      </StaleBundleBoundary>,
    )
    expect(screen.getByText('Səhifə')).toBeInTheDocument()

    const event = new Event('vite:preloadError', { cancelable: true })
    Object.assign(event, { payload: new Error('Unable to preload CSS for /assets/index-abc.css') })
    window.dispatchEvent(event)

    expect(await screen.findByRole('alert')).toHaveTextContent('Yeni versiya mövcuddur')
    expect(event.defaultPrevented).toBe(true) // Vite's own reload is left to the person
  })

  it('lets a real error through instead of blaming the deploy', () => {
    function Broken(): never {
      throw new TypeError('x.map is not a function')
    }
    expect(() =>
      render(
        <StaleBundleBoundary>
          <Broken />
        </StaleBundleBoundary>,
      ),
    ).toThrow('x.map is not a function')
  })
})
