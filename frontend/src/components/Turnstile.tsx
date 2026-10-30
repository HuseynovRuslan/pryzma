import { useEffect, useRef, useState } from 'react'

interface TurnstileApi {
  render: (el: HTMLElement, options: Record<string, unknown>) => string
  remove: (id: string) => void
}

declare global {
  interface Window {
    turnstile?: TurnstileApi
  }
}

/** Cloudflare's script, the one third-party script the CSP allows (security-headers.ts), loaded only where it is shown. */
export const TURNSTILE_SRC = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'

let loading: Promise<TurnstileApi> | null = null

function loadTurnstile(): Promise<TurnstileApi> {
  if (window.turnstile) return Promise.resolve(window.turnstile)
  loading ??= new Promise<TurnstileApi>((resolve, reject) => {
    const script = document.createElement('script')
    script.src = TURNSTILE_SRC
    script.async = true
    script.onload = () => (window.turnstile ? resolve(window.turnstile) : reject(new Error('turnstile')))
    script.onerror = () => {
      loading = null
      script.remove()
      reject(new Error('turnstile'))
    }
    document.head.appendChild(script)
  })
  return loading
}

/**
 * The robot check (Cloudflare Turnstile), shown only when the server names a site key. Usually it passes by itself
 * without a click. It hands the page a single-use token, or null when the token expires or the check fails; to ask
 * for a fresh one after a refused submit, give the component a new `key`.
 */
export function Turnstile({
  siteKey,
  action,
  onToken,
}: {
  siteKey: string
  action: string
  onToken: (token: string | null) => void
}) {
  const box = useRef<HTMLDivElement>(null)
  const latest = useRef(onToken)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    latest.current = onToken
  })

  useEffect(() => {
    let id: string | undefined
    let gone = false
    loadTurnstile()
      .then((api) => {
        if (gone || !box.current) return
        id = api.render(box.current, {
          sitekey: siteKey,
          action,
          theme: 'light',
          language: 'auto',
          callback: (token: string) => latest.current(token),
          'expired-callback': () => latest.current(null),
          'error-callback': () => latest.current(null),
        })
      })
      .catch(() => setFailed(true))
    return () => {
      gone = true
      if (id && window.turnstile) window.turnstile.remove(id)
    }
  }, [siteKey, action])

  return (
    <div className="turnstile">
      <div ref={box} />
      {failed && (
        <p className="field__hint" role="alert">
          Yoxlama yüklənmədi. İnterneti yoxlayıb səhifəni yeniləyin.
        </p>
      )}
    </div>
  )
}
