import { useEffect } from 'react'
import { useLocation } from 'react-router-dom'

/**
 * The site's real address, and the one a search engine should record. Written out rather than read from the
 * browser because that is the point of a canonical link: a page reached at any other host — an old domain whose
 * redirect was missed, an IP, a preview — should still name this one. index.html carries the same address for
 * the scrapers that never run this code.
 */
export const SITE_URL = 'https://pryzma.az'

/**
 * The canonical address of a path: the site, the path, and nothing else.
 *
 * The query string is dropped on purpose. Advertising sends people to /emircan?utm_source=instagram&…, and every
 * campaign would otherwise look like a separate page of the same competition. Nothing this site does is a
 * different page by virtue of its query — the entry form's `category` chooses inside one page, and it is behind
 * a sign-in no crawler reaches.
 */
export function canonicalUrl(pathname: string): string {
  // One trailing slash on the home page, none anywhere else: "/competitions" and "/competitions/" are one page.
  const path = pathname.length > 1 ? pathname.replace(/\/+$/, '') : '/'
  return SITE_URL + path
}

/**
 * Keeps the canonical link and og:url pointing at the page actually being read. Called once, in the public
 * layout: the administration is behind a sign-in and is nobody's search result.
 */
export function useCanonical() {
  const { pathname } = useLocation()

  useEffect(() => {
    const url = canonicalUrl(pathname)
    document.querySelector<HTMLLinkElement>('link[rel="canonical"]')?.setAttribute('href', url)
    document.querySelector<HTMLMetaElement>('meta[property="og:url"]')?.setAttribute('content', url)
  }, [pathname])
}
