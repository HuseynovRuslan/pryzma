/**
 * The jury's evaluation of every work and its internal ranking (InternalJuryRankingV1), from the administration's side —
 * mirrors Meydan.Api.Features.Jury DTOs. Nothing here is public.
 */
import { api, apiBlob } from '../api/client'
import type { AdminPrintImage, AdminPrintMaterial, AuditEntry } from './api'

export type JuryEvaluationStatus = 'Draft' | 'Open'
export type InvitationState = 'None' | 'Active' | 'Expired' | 'Revoked' | 'Locked'
export type JurorStatus = 'NotStarted' | 'InProgress' | 'Submitted'

export interface JurorInvitationState {
  state: InvitationState
  issuedAt: string | null
  expiresAt: string | null
  lastSignInAt: string | null
  identityConfirmedAt: string | null
  failedAttempts: number
}

export interface AdminJurorProgress {
  jurorId: number
  label: string
  scored: number
  missing: number
  status: JurorStatus
  submittedAt: string | null
  submittedByAdmin: boolean
  invitation: JurorInvitationState
}

export interface AdminJuryEvaluation {
  exists: boolean
  competitionId: number
  competitionTitle: string
  format: string | null
  status: JuryEvaluationStatus | null
  createdAt: string | null
  rosterFrozenAt: string | null
  /** Works the ballot's rule admits right now, to compare with the frozen roster. */
  eligibleNow: number
  rosterSize: number
  /** The ballot changed after the roster was frozen; the roster did not follow (by design). */
  rosterDiffers: boolean
  /** A score exists: the roster and the jurors no longer change. */
  rosterLocked: boolean
  jurorCount: number
  expectedScores: number
  completedScores: number
  missingScores: number
  jurors: AdminJurorProgress[]
  hasCombinedFinal: boolean
}

/** Shown once: the link (its secret in the #fragment) and the separate access code. */
export interface IssuedInvitation {
  jurorId: number
  label: string
  link: string
  code: string
  expiresAt: string
}

export interface JuryMatrixJuror {
  id: number
  label: string
  submitted: boolean
}

export interface JuryMatrixRow {
  submissionId: number
  reference: string
  title: string | null
  scores: (number | null)[]
  sources: (string | null)[]
  hasComment: boolean[]
  /** Only once every juror has submitted. */
  average: number | null
}

export interface JuryMatrix {
  jurors: JuryMatrixJuror[]
  rows: JuryMatrixRow[]
  complete: boolean
}

export interface PaperLine {
  reference?: string
  submissionId?: number
  score: string
}

export interface PaperResolution {
  submissionId: number
  keep: 'paper' | 'existing'
  /** The saved score the admin saw; a changed one saves nothing. */
  seen: number
}

export type PaperStatus = 'New' | 'Same' | 'Conflict' | 'Existing' | 'Missing'

export interface PaperPreviewRow {
  submissionId: number
  reference: string
  paper: number | null
  existing: number | null
  existingSource: string | null
  status: PaperStatus
}

export interface PaperPreview {
  jurorId: number
  label: string
  rows: PaperPreviewRow[]
  new: number
  same: number
  conflicts: number
  missing: number
  invalidLines: string[]
  jurorLocked: boolean
}

export interface PaperCommit {
  saved: number
  unchanged: number
  conflictsResolved: number
  scored: number
  total: number
}

/** One work in the internal ranking. Works with the same exact average share the position (`tied`). */
export interface JuryRankingRow {
  position: number
  submissionId: number
  reference: string
  title: string | null
  /** The exact average rounded to 4 places; `scoreSum / scoreCount` is the exact value compared. */
  average: number
  scoreSum: number
  scoreCount: number
  tied: boolean
  /** How many works share this position. */
  tieSize: number
}

export interface JuryRanking {
  competitionTitle: string
  /** Every juror has submitted every score; only then are there rows. */
  complete: boolean
  rosterSize: number
  jurorCount: number
  submittedJurors: number
  expectedScores: number
  completedScores: number
  rows: JuryRankingRow[]
  /** Positions shared by two or more works. */
  tieGroups: number
  generatedAt: string
}

/**
 * A work's public vote as the jury sees it: its public position by valid votes (level works share it — `tieSize` of
 * them), its valid votes and the votes approved audit decisions set aside. Counts only: no rule, no voter.
 */
export interface JuryPublicVote {
  position: number
  tieSize: number
  validVotes: number
  invalidatedVotes: number
}

export interface JuryPrintWork {
  submissionId: number
  reference: string
  title: string
  image: AdminPrintImage | null
  pdfPages: number
  material: AdminPrintMaterial[]
  vote: JuryPublicVote
}

/** The printed jury catalog — the evaluation form itself: every work, most valid public votes first. */
export interface JuryPrint {
  competitionTitle: string
  works: JuryPrintWork[]
  /** The jurors actually configured, in their order: one score field each (M1, M2, …). */
  jurors: JuryMatrixJuror[]
}

const url = (competitionId: number, path = '') => `/api/admin/competitions/${competitionId}/jury-evaluation${path}`

export const juryAdminApi = {
  get: (competitionId: number, signal?: AbortSignal) => api<AdminJuryEvaluation>(url(competitionId), { signal }),
  create: (competitionId: number) => api<AdminJuryEvaluation>(url(competitionId), { method: 'POST' }),
  freezeRoster: (competitionId: number) => api<AdminJuryEvaluation>(url(competitionId, '/roster/freeze'), { method: 'POST' }),
  addJuror: (competitionId: number, label: string) =>
    api<AdminJuryEvaluation>(url(competitionId, '/jurors'), { method: 'POST', body: { label } }),
  renameJuror: (competitionId: number, jurorId: number, label: string) =>
    api<AdminJuryEvaluation>(url(competitionId, `/jurors/${jurorId}`), { method: 'PUT', body: { label } }),
  removeJuror: (competitionId: number, jurorId: number) =>
    api<AdminJuryEvaluation>(url(competitionId, `/jurors/${jurorId}`), { method: 'DELETE' }),
  issueInvitation: (competitionId: number, jurorId: number, days: number) =>
    api<IssuedInvitation>(url(competitionId, `/jurors/${jurorId}/invitation`), { method: 'POST', body: { days } }),
  revokeInvitation: (competitionId: number, jurorId: number) =>
    api<AdminJuryEvaluation>(url(competitionId, `/jurors/${jurorId}/invitation`), { method: 'DELETE' }),
  lockOnBehalf: (competitionId: number, jurorId: number) =>
    api<AdminJuryEvaluation>(url(competitionId, `/jurors/${jurorId}/lock`), { method: 'POST' }),
  reopen: (competitionId: number, jurorId: number, reason: string) =>
    api<AdminJuryEvaluation>(url(competitionId, `/jurors/${jurorId}/reopen`), { method: 'POST', body: { reason } }),
  matrix: (competitionId: number, signal?: AbortSignal) => api<JuryMatrix>(url(competitionId, '/matrix'), { signal }),
  exportCsv: (competitionId: number) => apiBlob(url(competitionId, '/export.csv')),
  paperPreview: (competitionId: number, jurorId: number, lines: PaperLine[]) =>
    api<PaperPreview>(url(competitionId, '/paper/preview'), { method: 'POST', body: { jurorId, lines } }),
  paperCommit: (competitionId: number, jurorId: number, lines: PaperLine[], resolutions: PaperResolution[], reason: string | null) =>
    api<PaperCommit>(url(competitionId, '/paper/commit'), { method: 'POST', body: { jurorId, lines, resolutions, reason } }),
  ranking: (competitionId: number, signal?: AbortSignal) => api<JuryRanking>(url(competitionId, '/ranking'), { signal }),
  rankingCsv: (competitionId: number) => apiBlob(url(competitionId, '/ranking.csv')),
  print: (competitionId: number, signal?: AbortSignal) => api<JuryPrint>(url(competitionId, '/print'), { signal }),
  audit: (competitionId: number, signal?: AbortSignal) => api<AuditEntry[]>(url(competitionId, '/audit?take=200'), { signal }),
}

/** The jury's catalog (the printable evaluation form), the internal ranking on paper, or the admin's vote analytics. */
export const juryPrintHref = (competitionId: number, doc: 'catalog' | 'ranking' | 'analytics') =>
  `/admin/jury-print/${competitionId}?doc=${doc}`

/** The full invitation address for the juror, from the server's relative link. */
export const invitationUrl = (link: string) => `${window.location.origin}${link}`

/** Works on one portrait A4 page of the jury's catalog: 2×2. */
export const CATALOG_PER_PAGE = 4

/** The key of each juror's score field, in the jurors' order: M1, M2, … — one per juror actually configured. */
export const markKeys = (jurors: readonly unknown[]) => jurors.map((_, i) => `M${i + 1}`)

/** Ranking rows on one portrait A4 page. */
export const RANKING_ROWS = 33
