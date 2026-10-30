import type { PlatformSettings } from '../api/types'

/**
 * What the site says about itself before the settings request has answered — and what it keeps saying if that
 * request fails. Bundled on purpose: a temporary backend problem must leave a usable site with the right name on
 * it, not a blank header. Kept in step with PlatformDefaults on the server.
 */
export const platformDefaults: PlatformSettings = {
  platformName: 'PRIZMA',
  homeHeroTitle: 'İdeyanı meydana çıxar',
  homeHeroSubtitle: 'Yaradıcı layihələr və açıq müsabiqələr üçün vahid platforma',
  authPromoTitle: 'Bir hesab, bütün müsabiqələr',
  authPromoImage: null,
}
