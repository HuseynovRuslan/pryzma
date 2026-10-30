import type { CompetitionPhase, ProjectStatus, SubmissionStatus } from '../api/types'

// Written out rather than relying on Intl 'az' data, which is incomplete in some mobile browsers.
const MONTHS = [
  'yanvar',
  'fevral',
  'mart',
  'aprel',
  'may',
  'iyun',
  'iyul',
  'avqust',
  'sentyabr',
  'oktyabr',
  'noyabr',
  'dekabr',
]

/** "18 sentyabr 2026" in Baku time. */
export function formatDate(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  const baku = new Date(d.getTime() + (4 * 60 + d.getTimezoneOffset()) * 60_000)
  return `${baku.getDate()} ${MONTHS[baku.getMonth()]} ${baku.getFullYear()}`
}

export const phaseLabel: Record<CompetitionPhase, string> = {
  Open: 'Qəbul açıqdır',
  Upcoming: 'Tezliklə',
  Closed: 'Qəbul bağlanıb',
}

export const projectStatusLabel: Record<ProjectStatus, string> = {
  Draft: 'Qaralama',
  Published: 'Aktiv',
  Archived: 'Arxivdə',
}

export const submissionStatusLabel: Record<SubmissionStatus, string> = {
  Draft: 'Qaralama',
  Submitted: 'Göndərilib',
  UnderReview: 'Qiymətləndirilir',
  Approved: 'Qəbul edilib',
  Rejected: 'Qəbul edilməyib',
  Winner: 'Qalib',
  Withdrawn: 'Geri çəkilib',
}

/** "2,4 MB", "830 KB" — decimal comma as written in Azerbaijani; a no-break space keeps number and unit together. */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes}\u00a0B`
  const kb = bytes / 1024
  if (kb < 1024) return `${Math.round(kb)}\u00a0KB`
  const mb = kb / 1024
  return Number.isInteger(mb) ? `${mb}\u00a0MB` : `${mb.toFixed(1).replace('.', ',')}\u00a0MB`
}

/** The calendar day in Baku, as a Date whose local fields hold it — the same shift formatDate makes. */
function bakuDay(d: Date): Date {
  return new Date(d.getTime() + (4 * 60 + d.getTimezoneOffset()) * 60_000)
}

/** "3 oktyabr" in Baku time — the date without its year, for a line where the year goes without saying. */
export function formatDayMonth(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  const baku = bakuDay(d)
  return `${baku.getDate()} ${MONTHS[baku.getMonth()]}`
}

/** "23:59" in Baku time — the minute a deadline actually falls on. */
export function formatTime(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  const baku = bakuDay(d)
  return `${String(baku.getHours()).padStart(2, '0')}:${String(baku.getMinutes()).padStart(2, '0')}`
}

/**
 * Whole days left until a deadline, counted in Baku calendar days: 0 on the last day itself, 1 the day before.
 * Null once it has passed, so a caller can never print "−2 gün".
 */
export function daysLeft(iso: string | null | undefined, now: Date = new Date()): number | null {
  if (!iso) return null
  const end = new Date(iso)
  if (Number.isNaN(end.getTime()) || end.getTime() < now.getTime()) return null
  const a = bakuDay(now)
  const b = bakuDay(end)
  const dayA = Date.UTC(a.getFullYear(), a.getMonth(), a.getDate())
  const dayB = Date.UTC(b.getFullYear(), b.getMonth(), b.getDate())
  return Math.round((dayB - dayA) / 86_400_000)
}

/** "18 sentyabr 2026, 14:05" in Baku time. */
export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  const baku = new Date(d.getTime() + (4 * 60 + d.getTimezoneOffset()) * 60_000)
  const hh = String(baku.getHours()).padStart(2, '0')
  const mm = String(baku.getMinutes()).padStart(2, '0')
  return `${formatDate(iso)}, ${hh}:${mm}`
}

/**
 * A competition's name as a label rather than a sentence: “Novxanının simvolu” yaradıcılıq müsabiqəsi becomes
 * Novxanının simvolu. Titles are written with the name in quotation marks and the kind of contest after it, so
 * the quoted part is the name; a title written any other way is returned whole and the label wraps instead.
 */
export function shortCompetitionTitle(title: string): string {
  const quoted = title.match(/[“"«]\s*([^”"»]+?)\s*[”"»]/)
  return quoted?.[1] || title.trim()
}

/** Two-digit project index, e.g. 1 → "01". */
export function ordinal(n: number): string {
  return String(n).padStart(2, '0')
}
