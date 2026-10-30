/**
 * The administration's vote analytics over the catalog of every work (Meydan.Api.Features.Admin.VoteCatalogAnalytics).
 * Admins only. The jury's catalog, papers and API never carry any of this.
 */
import { api } from '../api/client'
import type { AdminPrintImage } from './api'

/** Votes set aside by approved audit decisions, per rule: D12 phone cluster, R2 coordinated identity, D14 e-mail. */
export interface VoteAuditBreakdown {
  duplicatePhone: number
  coordinatedAccounts: number
  disposableEmail: number
}

export interface VoteCatalogWork {
  /** Public position by valid votes among every work on the ballot; shared by `tieSize` works. */
  position: number
  tieSize: number
  submissionId: number
  reference: string
  title: string | null
  image: AdminPrintImage | null
  pdfPages: number
  validVotes: number
  invalidatedVotes: number
  invalidated: VoteAuditBreakdown
}

export interface VoteCatalog {
  competitionTitle: string
  validVotes: number
  invalidatedVotes: number
  invalidated: VoteAuditBreakdown
  worksWithInvalidations: number
  /** Null: no pending-review state is recorded anywhere, so nothing is shown as pending. */
  pendingReview: number | null
  /** Votes of the competition for works no longer on the ballot (counted, not shown). */
  outsideCatalog: number
  /** Most valid votes first. */
  works: VoteCatalogWork[]
  generatedAt: string
}

export const voteCatalogApi = {
  catalog: (competitionId: number, signal?: AbortSignal) =>
    api<VoteCatalog>(`/api/admin/competitions/${competitionId}/voting/catalog`, { signal }),
}

/** The rules' short names, in the order the public audit lists them. */
export const RULES: { key: keyof VoteAuditBreakdown; label: string }[] = [
  { key: 'duplicatePhone', label: 'telefon (D12)' },
  { key: 'coordinatedAccounts', label: 'R2' },
  { key: 'disposableEmail', label: 'D14' },
]

/** "D14 2 · telefon (D12) 1" — only the rules that apply, in the audit's order. */
export const breakdownText = (b: VoteAuditBreakdown) =>
  RULES.filter((r) => b[r.key] > 0)
    .map((r) => `${r.label} ${b[r.key]}`)
    .join(' · ')

/** Works ordered by valid votes (as the server sends them) or back in submission-number order. */
export const orderWorks = (works: VoteCatalogWork[], order: 'votes' | 'reference', onlyInvalidated: boolean) => {
  const shown = onlyInvalidated ? works.filter((w) => w.invalidatedVotes > 0) : [...works]
  return order === 'reference' ? shown.sort((a, b) => (a.reference < b.reference ? -1 : a.reference > b.reference ? 1 : 0)) : shown
}
