import { resultsApi } from '../api/endpoints'
import { useApi } from '../lib/useApi'

/** "#039": the short number an announcement uses for a work. The link keeps the full reference. */
export function shortReference(reference: string): string {
  const digits = reference.match(/(\d+)$/)?.[1]
  return digits ? `#${String(Number(digits)).padStart(3, '0')}` : reference
}

/**
 * The published result the home page shows its winners from. Nothing is asked on an ordinary day: the competition
 * list the page already reads (`published`) says whether any result is public, and only then is the result read —
 * that one result when there is one, otherwise the newest by the published list. `settled` is false while that is
 * still being decided, so the page can wait and show everything below its lead at once, without a jump.
 *
 * Apart from the components (HomeWinners.tsx) so that file exports components only, which keeps fast refresh working.
 */
export function useHomeWinners(ready: boolean, published: readonly string[]) {
  const wanted = ready && published.length > 0
  const read = useApi(
    async (signal) => {
      if (!wanted) return null
      const slug = published.length === 1 ? published[0] : (await resultsApi.list(signal))[0]?.competition.slug
      return slug ? resultsApi.result(slug, signal) : null
    },
    [wanted, published.join('|')],
  )
  // An extra on the home page: a result that cannot be read leaves the page as it is.
  return { settled: ready && read.status !== 'loading', result: read.status === 'success' ? read.data : null }
}
