/**
 * A random id for this browser, sent with every API call as X-Device-Id. The server keeps it with sign-ins and votes
 * as fraud evidence only (docs/voting/OWNER-DECISIONS.md, D20): several accounts voting from one browser share it.
 * It identifies a browser, never a person, carries nothing about the user and is never shown anywhere.
 *
 * Kept in localStorage so it survives reloads and sign-outs. Where storage is blocked (private mode, disabled site
 * data) the id lives for this page load only, and the site works the same.
 */
const KEY = 'pryzma:device'
const SHAPE = /^[A-Za-z0-9-]{8,64}$/

let memory: string | null = null

function fresh(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  const bytes = new Uint8Array(16)
  if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') crypto.getRandomValues(bytes)
  else for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256)
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')
}

export function deviceId(): string {
  if (memory) return memory
  try {
    const stored = localStorage.getItem(KEY)
    if (stored && SHAPE.test(stored)) return (memory = stored)
  } catch {
    // Storage blocked: fall through to a per-page-load id.
  }
  memory = fresh()
  try {
    localStorage.setItem(KEY, memory)
  } catch {
    // Storage blocked or full: the in-memory id still serves this page load.
  }
  return memory
}

/** The header name the API reads. */
export const DEVICE_HEADER = 'X-Device-Id'

/** Test hook: forget the cached id so the next call reads storage again. */
export function resetDeviceIdForTests(): void {
  memory = null
}
