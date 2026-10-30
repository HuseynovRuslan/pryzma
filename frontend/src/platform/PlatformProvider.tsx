import { useEffect, useState, type ReactNode } from 'react'
import { catalog } from '../api/endpoints'
import type { PlatformSettings } from '../api/types'
import { PlatformContext } from './context'
import { platformDefaults } from './defaults'

/**
 * Starts from the bundled defaults and replaces them once the server answers. Two consequences on purpose:
 * the header never renders empty and never flashes a different brand, and a settings request that fails leaves
 * the site exactly as it shipped instead of taking it down.
 */
export function PlatformProvider({ children }: { children: ReactNode }) {
  const [settings, setSettings] = useState<PlatformSettings>(platformDefaults)

  useEffect(() => {
    const controller = new AbortController()
    catalog
      .settings(controller.signal)
      .then((value) => setSettings({ ...platformDefaults, ...value }))
      .catch(() => {
        /* the defaults are already on screen and stay there */
      })
    return () => controller.abort()
  }, [])

  return <PlatformContext.Provider value={settings}>{children}</PlatformContext.Provider>
}
