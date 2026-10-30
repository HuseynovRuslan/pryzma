import { Component, type ErrorInfo, type ReactNode } from 'react'
import { isStaleBundleError } from '../lib/staleBundle'

/*
  A tab that was open across a deploy is still running the previous bundle. Most of it keeps working — but the
  moment it needs a chunk it has not loaded yet, it asks for a file name from a build that may no longer be on the
  server. The server keeps old builds around for a while (frontend/nginx-assets.sh), so this should be rare; when
  it does happen the person should be told what actually went wrong, not shown a blank page.
*/

export function StaleBundleNotice() {
  return (
    <div className="container section" role="alert">
      <div className="empty">
        <h2>Yeni versiya mövcuddur</h2>
        <p>
          Sayt siz bu səhifəni açandan sonra yeniləndi, ona görə bu hissə yüklənə bilmədi. Səhifəni yeniləyin —
          yazdıqlarınız saxlanılıbsa itməyəcək.
        </p>
        <button type="button" className="btn btn--primary btn--lg" onClick={() => window.location.reload()}>
          Səhifəni yenilə
        </button>
      </div>
    </div>
  )
}

interface State {
  error: unknown
}

/**
 * Catches the two ways a stale bundle shows up: a lazy route that throws while rendering, and Vite's own
 * `vite:preloadError`, which fires when a module preload fails before React ever sees it. Anything else is
 * re-thrown from render, so a genuine bug still surfaces instead of being dressed up as a deploy.
 */
export class StaleBundleBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { error: null }

  private readonly onPreloadError = (event: Event) => {
    // Vite's event carries the original error and can be cancelled to stop the default reload/throw.
    event.preventDefault()
    this.setState({ error: (event as Event & { payload?: unknown }).payload ?? event })
  }

  static getDerivedStateFromError(error: unknown): State {
    return { error }
  }

  componentDidMount() {
    window.addEventListener('vite:preloadError', this.onPreloadError)
  }

  componentWillUnmount() {
    window.removeEventListener('vite:preloadError', this.onPreloadError)
  }

  componentDidCatch(error: unknown, info: ErrorInfo) {
    if (!isStaleBundleError(error)) console.error('Unhandled error', error, info.componentStack)
  }

  render() {
    const { error } = this.state
    if (error === null) return this.props.children
    if (isStaleBundleError(error)) return <StaleBundleNotice />
    throw error
  }
}
