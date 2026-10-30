import { useCallback, useEffect, useState } from 'react'
import { votingApi } from '../../api/endpoints'
import type { VotingAudit } from '../../api/types'

/** How often a visible page in review asks for the audit: often enough that a decision shows up within the minute. */
export const AUDIT_POLL_MS = 30_000

/**
 * The public audit of a vote — real totals and one line per admin decision — while the page shows the review
 * stage. Read on mount, then every AUDIT_POLL_MS while the tab is visible, and at once when it is shown again.
 * A failed read keeps the last answer; nothing here ever claims more than the server said.
 */
export function useVotingAudit(slug: string, enabled: boolean) {
  const [audit, setAudit] = useState<VotingAudit | null>(null)

  const read = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const next = await votingApi.audit(slug, signal)
        if (!signal?.aborted) setAudit(next)
      } catch {
        // keep the last answer on screen
      }
    },
    [slug],
  )

  useEffect(() => {
    if (!enabled) return
    const controller = new AbortController()
    void read(controller.signal)
    const id = setInterval(() => {
      if (document.visibilityState === 'visible') void read(controller.signal)
    }, AUDIT_POLL_MS)
    const onVisibility = () => {
      if (document.visibilityState === 'visible') void read(controller.signal)
    }
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      controller.abort()
      clearInterval(id)
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [enabled, read])

  return audit
}
