/**
 * Response headers for the built SPA (index.html and assets). One source of truth: `vite preview` applies them
 * (so e2e tests run the real bundle under the real policy) and the production web server copies them (README).
 *
 * CSP: everything is self-hosted (fonts bundled, API same-origin). 'unsafe-inline' is limited to style attributes,
 * which React's style={{…}} props need (project accent colours); no inline or third-party script is allowed.
 * blob: images are the authorised previews of a participant's own files. Add `upgrade-insecure-requests` on HTTPS.
 */
export const securityHeaders: Record<string, string> = {
  'Content-Security-Policy': [
    "default-src 'self'",
    // Cloudflare Turnstile, the registration robot check: its script and its frame, nothing else of theirs.
    "script-src 'self' https://challenges.cloudflare.com",
    'frame-src https://challenges.cloudflare.com',
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self'",
    "connect-src 'self'",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    "frame-ancestors 'none'",
  ].join('; '),
  'X-Content-Type-Options': 'nosniff',
  'X-Frame-Options': 'DENY',
  'Referrer-Policy': 'strict-origin-when-cross-origin',
  'Permissions-Policy': 'camera=(), microphone=(), geolocation=(), payment=(), usb=()',
  'Cross-Origin-Opener-Policy': 'same-origin',
}
