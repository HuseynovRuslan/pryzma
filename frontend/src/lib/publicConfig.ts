import { useEffect, useState } from 'react'
import { configApi } from '../api/endpoints'
import type { PublicConfig } from '../api/types'

/** Until the API answers, assume the safest shape: no QRLog door, and no promise that a reset mail can be sent. */
export const defaultConfig: PublicConfig = { qrlogLogin: false, passwordReset: false }

let cached: PublicConfig | null = null
let inFlight: Promise<PublicConfig> | null = null

export function loadPublicConfig(): Promise<PublicConfig> {
  if (cached) return Promise.resolve(cached)
  inFlight ??= configApi
    .get()
    .then((c) => {
      cached = c
      return c
    })
    .catch(() => defaultConfig)
    .finally(() => {
      inFlight = null
    })
  return inFlight
}

/** For tests and for a session change that may open or close a door. */
export function resetPublicConfig() {
  cached = null
}

/**
 * Which sign-in doors this deployment has open. Fetched once per page load and shared; `ready` is false only until
 * the first answer, so screens can avoid flashing a form they are about to replace.
 */
export function usePublicConfig(): PublicConfig & { ready: boolean } {
  const [state, setState] = useState<{ config: PublicConfig; ready: boolean }>(() => ({
    config: cached ?? defaultConfig,
    ready: cached !== null,
  }))
  useEffect(() => {
    let cancelled = false
    loadPublicConfig().then((config) => {
      if (!cancelled) setState({ config, ready: true })
    })
    return () => {
      cancelled = true
    }
  }, [])
  return { ...state.config, ready: state.ready }
}
