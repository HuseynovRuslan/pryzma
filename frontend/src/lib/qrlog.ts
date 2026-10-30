import { useSyncExternalStore } from 'react'
import { authApi } from '../api/endpoints'

const NEXT_KEY = 'meydan.qrlog.next'

export function rememberQrlogNext(next: string | null | undefined) {
  try {
    if (next) sessionStorage.setItem(NEXT_KEY, next)
    else sessionStorage.removeItem(NEXT_KEY)
  } catch {
    /* private mode — the server keeps the destination on the ticket anyway */
  }
}

export function recallQrlogNext(): string | null {
  try {
    return sessionStorage.getItem(NEXT_KEY)
  } catch {
    return null
  }
}

/**
 * A phone, as far as signing in is concerned: a narrow screen held in the hand (coarse pointer, no hover). There
 * QRLog opens on the same device; everywhere else — laptops, desktops, tablets — the page shows a QR for the phone.
 * Capabilities, not the user agent; and a narrow desktop window (fine pointer) still gets the QR.
 */
const PHONE_QUERY = '(max-width: 767px) and (pointer: coarse)'

export function isPhoneLike(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return false
  return window.matchMedia(PHONE_QUERY).matches
}

/** {@link isPhoneLike}, kept current as the window is resized or the device rotated. */
export function usePhoneLike(): boolean {
  return useSyncExternalStore(
    (onChange) => {
      if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return () => {}
      const query = window.matchMedia(PHONE_QUERY)
      query.addEventListener('change', onChange)
      return () => query.removeEventListener('change', onChange)
    },
    isPhoneLike,
    () => false,
  )
}

/** Shown whenever a QRLog sign-in could not be finished; never a protocol detail. */
export const FAILED_MESSAGE = 'QRLog ilə giriş tamamlanmadı. Yenidən cəhd edin.'

/**
 * Starts a QRLog sign-in: the API opens a ticket (its claim secret goes into an HttpOnly cookie) and this tab
 * travels to QRLog, where the person approves with one tap and is sent back to /login/qrlog.
 */
export async function beginQrlogSignIn(next: string | null | undefined, link = false): Promise<void> {
  rememberQrlogNext(next)
  const started = await authApi.qrlogStart({ returnTo: next ?? null, link })
  window.location.assign(started.authorizeUrl)
}
