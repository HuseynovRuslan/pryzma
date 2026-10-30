/**
 * Keeps the participant's choice (competition + category) alive across the login/registration detour.
 * The destination also travels in the `next` query parameter; sessionStorage adds the human-readable
 * names so the auth screens can say what the person is signing in for.
 */

export interface ParticipationIntent {
  competitionSlug: string
  competitionTitle: string
  projectName: string
  categorySlug: string | null
  categoryName: string | null
  categoryLabel: string | null
  savedAt: number
}

const KEY = 'meydan.participation'
const MAX_AGE_MS = 1000 * 60 * 60 * 3

export function participatePath(competitionSlug: string, categorySlug?: string | null): string {
  const base = `/competitions/${encodeURIComponent(competitionSlug)}/submit`
  return categorySlug ? `${base}?category=${encodeURIComponent(categorySlug)}` : base
}

export function saveIntent(intent: Omit<ParticipationIntent, 'savedAt'>) {
  try {
    sessionStorage.setItem(KEY, JSON.stringify({ ...intent, savedAt: Date.now() }))
  } catch {
    /* storage unavailable (private mode) — the `next` parameter still carries the route */
  }
}

export function readIntent(): ParticipationIntent | null {
  try {
    const raw = sessionStorage.getItem(KEY)
    if (!raw) return null
    const intent = JSON.parse(raw) as ParticipationIntent
    if (!intent.competitionSlug || Date.now() - intent.savedAt > MAX_AGE_MS) {
      sessionStorage.removeItem(KEY)
      return null
    }
    return intent
  } catch {
    return null
  }
}

export function clearIntent() {
  try {
    sessionStorage.removeItem(KEY)
  } catch {
    /* ignore */
  }
}

/** Only same-site relative paths are allowed as post-login destinations (no open redirects). */
export function safeNext(next: string | null | undefined, fallback = '/'): string {
  if (!next) return fallback
  if (!next.startsWith('/') || next.startsWith('//') || next.startsWith('/\\')) return fallback
  return next
}

/** Build /login?next=... or /register?next=... preserving the destination. */
export function authPath(kind: 'login' | 'register', next?: string | null): string {
  const path = kind === 'login' ? '/login' : '/register'
  return next ? `${path}?next=${encodeURIComponent(next)}` : path
}
