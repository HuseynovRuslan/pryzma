import { createContext, useContext } from 'react'
import type { PlatformSettings } from '../api/types'
import { platformDefaults } from './defaults'

/** Holds the platform's own words. Separate from the provider so the hook is not a component export. */
export const PlatformContext = createContext<PlatformSettings>(platformDefaults)

/** The platform's name and the few lines of copy that are about the site rather than about a competition. */
export function usePlatform(): PlatformSettings {
  return useContext(PlatformContext)
}
