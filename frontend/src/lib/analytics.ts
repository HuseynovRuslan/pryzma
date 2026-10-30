/**
 * The four moments of the entry funnel, named once, so that measuring them later is one adapter and not a hunt
 * through the pages. Nothing is sent anywhere today: no tracking script is installed and none is added here.
 *
 * Wiring a provider later means one call at start-up, e.g. for Meta Pixel (after its own snippet has loaded):
 *
 *   registerAnalytics((e) => {
 *     if (e.name === 'landing_viewed') fbq('track', 'ViewContent', { content_name: e.slug })
 *     if (e.name === 'submission_completed') fbq('track', 'Lead', { content_name: e.slug })
 *   })
 *
 * `submission_completed` fires exactly once per entry, at the moment the server accepts it — the right place for a
 * Lead or CompleteRegistration event. A draft resumed in a second tab, or a submit recovered after a double click,
 * does not fire it again.
 */

export type AnalyticsEvent =
  /** A competition page was opened; `via` is the address it was opened at ("/emircan" or "/competitions/<slug>"). */
  | { name: 'landing_viewed'; slug: string; via: string }
  /** One of the page's "take part" buttons was pressed; `placement` says which. */
  | { name: 'cta_clicked'; slug: string; placement: 'hero' | 'sticky' | 'final' | 'panel' }
  /** A signed-in person reached the entry form: their draft was opened (or resumed). */
  | { name: 'submission_started'; slug: string; submissionId: number }
  /** The server accepted an entry. */
  | { name: 'submission_completed'; slug: string; submissionId: number; reference: string | null }

/** What a provider receives: the event, plus where the visit came from if it arrived with campaign parameters. */
export type TrackedEvent = AnalyticsEvent & { attribution: Attribution | null }

type Adapter = (event: TrackedEvent) => void

const adapters = new Set<Adapter>()

/** Registers a provider; returns the function that removes it again. */
export function registerAnalytics(adapter: Adapter): () => void {
  adapters.add(adapter)
  return () => adapters.delete(adapter)
}

/** Hands the event to every registered provider. A provider that throws never breaks the page it was called from. */
export function track(event: AnalyticsEvent) {
  const tracked: TrackedEvent = { ...event, attribution: readAttribution() }
  for (const adapter of adapters) {
    try {
      adapter(tracked)
    } catch {
      /* a broken tracker is not the visitor's problem */
    }
  }
  if (import.meta.env.DEV) console.debug('[analytics]', tracked)
}

// ---- Attribution ----------------------------------------------------------------------------------------------

/** The parameters an ad or a newsletter link carries. Nothing personal: these name a campaign, not a person. */
export const CAMPAIGN_PARAMS = [
  'utm_source',
  'utm_medium',
  'utm_campaign',
  'utm_content',
  'utm_term',
  'fbclid',
  'gclid',
] as const

export type CampaignParam = (typeof CAMPAIGN_PARAMS)[number]

export interface Attribution {
  params: Partial<Record<CampaignParam, string>>
  /** The path the visit landed on, e.g. "/emircan". */
  landingPath: string
  landedAt: number
}

const KEY = 'prizma.attribution'
/** Long enough to cover somebody who clicks an ad today and comes back to enter at the weekend. */
const MAX_AGE_MS = 1000 * 60 * 60 * 24 * 30

/**
 * Remembers the campaign a visit arrived from. The entry form is several pages and a sign-in away from the page the
 * ad opened — the login detour, a registration, a return from QRLog — and none of those carry the ad's parameters
 * in their address. They are kept here instead, so the moment an entry is sent can still say which ad it came from.
 *
 * The most recent campaign wins; a later visit without parameters leaves the remembered one alone. Kept in
 * localStorage so a second tab of the same browser still knows it.
 */
export function captureAttribution(search: string, pathname: string, now = Date.now()) {
  const query = new URLSearchParams(search)
  const params: Attribution['params'] = {}
  for (const name of CAMPAIGN_PARAMS) {
    const value = query.get(name)?.trim()
    // A generous cap: a real parameter is short, and an absurd one should not fill somebody's storage.
    if (value) params[name] = value.slice(0, 200)
  }
  if (Object.keys(params).length === 0) return
  try {
    localStorage.setItem(KEY, JSON.stringify({ params, landingPath: pathname, landedAt: now } satisfies Attribution))
  } catch {
    /* storage unavailable (private mode): the event simply goes out without a campaign */
  }
}

export function readAttribution(now = Date.now()): Attribution | null {
  try {
    const raw = localStorage.getItem(KEY)
    if (!raw) return null
    const stored = JSON.parse(raw) as Attribution
    if (!stored?.params || typeof stored.landedAt !== 'number' || now - stored.landedAt > MAX_AGE_MS) {
      localStorage.removeItem(KEY)
      return null
    }
    return stored
  } catch {
    return null
  }
}
