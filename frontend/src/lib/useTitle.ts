import { useEffect } from 'react'
import { usePlatform } from '../platform/context'

/** Sets document.title as "&lt;page&gt; — &lt;platform&gt;", the platform being whatever the admin called it. */
export function useTitle(title: string | undefined) {
  const { platformName } = usePlatform()
  useEffect(() => {
    document.title = title ? `${title} — ${platformName}` : platformName
  }, [title, platformName])
}
