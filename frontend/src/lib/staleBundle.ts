/**
 * Was this failure a chunk that could not be fetched?
 *
 * A tab left open across a deploy still runs the previous bundle, and the moment it needs a chunk it has not
 * loaded yet it asks for a file name from a build that may no longer be on the server. Every engine words that
 * failure differently and none of them gives a code, so the message is all there is to go on. Anything
 * unrecognised is treated as a real error, not as a deploy.
 */
export function isStaleBundleError(error: unknown): boolean {
  const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error ?? '')
  return (
    /Failed to fetch dynamically imported module/i.test(message) || // Chrome, Edge
    /error loading dynamically imported module/i.test(message) || // Firefox
    /Importing a module script failed/i.test(message) || // Safari
    /Unable to preload CSS/i.test(message) || // Vite's own preload helper
    /ChunkLoadError/i.test(message)
  )
}
