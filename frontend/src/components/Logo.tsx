import { usePlatform } from '../platform/context'
import './Logo.css'

/**
 * The wordmark: PRIZMA's drawn logo — the name in extended capitals beside the prism it is named after. The
 * platform's name is still a setting; it names the picture for anyone who cannot see it, and the header's link
 * carries it as the label.
 */
export function Wordmark() {
  const { platformName } = usePlatform()
  return (
    <span className="wordmark">
      <img
        className="wordmark__image"
        src="/brand/prizma-wordmark.png"
        alt={platformName}
        width={1200}
        height={304}
        decoding="async"
      />
    </span>
  )
}
