import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import { securityHeaders } from './security-headers'

// The API runs on :5080 in development; proxying keeps the SPA and API same-origin,
// so the HttpOnly refresh cookie (SameSite=Strict, Path=/api/auth) just works.
declare const process: { env: Record<string, string | undefined> }

// API_TARGET points the proxy elsewhere, e.g. at a throwaway QA instance.
const apiTarget = process.env.API_TARGET ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [react()],
  server: {
    port: Number(process.env.PORT ?? 5173),
    proxy: {
      '/api': { target: apiTarget, changeOrigin: false },
    },
  },
  build: {
    // No source maps in the public bundle: they would publish the full source; errors are reported by traceId instead.
    sourcemap: false,
  },
  preview: {
    port: Number(process.env.PORT ?? 4173),
    headers: securityHeaders,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: false },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
})
