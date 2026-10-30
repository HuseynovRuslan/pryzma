/**
 * The juror's own API (Meydan.Api.Features.Jury.JurorController). It runs on the juror's session cookie — HttpOnly,
 * scoped to /api/jury — never on a user's token, so pictures load with a plain <img src>. The juror evaluates informed:
 * each work carries its public vote as counts (place, valid, set aside). Nothing these shapes can hold carries a voter,
 * an audit rule, another juror's score, an average or the jury's internal ranking.
 */
import { api } from '../api/client'

export interface JurorSession {
  jurorName: string
  competitionId: number
  competitionTitle: string
  /** The juror has confirmed the name shown is theirs; nothing else opens until they do. */
  confirmed: boolean
  /** Submitted: the scores are locked. */
  locked: boolean
  evaluationStatus: 'Draft' | 'Open'
}

/** The work's picture, as the server picked it: for a PDF the page that stands for it. */
export interface JurorImage {
  fileId: number
  large: boolean
  width: number | null
  height: number | null
  page?: number
  pages?: number
}

/**
 * A work's public vote: its public place by valid votes (level works share it — `tieSize` of them), its valid votes
 * and the votes approved audit decisions set aside. Counts only.
 */
export interface JurorPublicVote {
  position: number
  tieSize: number
  validVotes: number
  invalidatedVotes: number
}

/** One work in the juror's list — most valid public votes first. */
export interface JurorWorkItem {
  submissionId: number
  reference: string
  title: string | null
  image: JurorImage | null
  vote: JurorPublicVote
  score: number | null
  hasComment: boolean
}

export interface JurorDashboard {
  jurorName: string
  competitionId: number
  competitionTitle: string
  total: number
  scored: number
  remaining: number
  locked: boolean
  submittedAt: string | null
  /** Scores can be given (the evaluation is open). */
  open: boolean
  works: JurorWorkItem[]
}

export interface JurorMaterial {
  fileId: number
  kind: 'image' | 'pdf'
  page: number
  pages: number
}

export interface JurorWork {
  submissionId: number
  reference: string
  title: string | null
  category: string | null
  facts: { label: string; value: string }[]
  conceptLabel: string | null
  concept: string | null
  image: JurorImage | null
  pdfPages: number
  material: JurorMaterial[]
  vote: JurorPublicVote
  /** The work's place in the juror's list (most valid public votes first), of `total`. */
  index: number
  total: number
  previousId: number | null
  nextId: number | null
  score: number | null
  comment: string | null
  /** The stored score's version: a save on an older one is refused (409), never written over. */
  version: number | null
  locked: boolean
}

export interface JurorScoreResult {
  submissionId: number
  score: number
  comment: string | null
  version: number
  scored: number
  total: number
}

const base = '/api/jury'

/** A picture of a work on the juror's roster: the file's safe copy, or a later PDF page rendered on request. */
export function juryImageUrl(submissionId: number, fileId: number, page = 1, thumb = false) {
  const root = `${base}/works/${submissionId}/files/${fileId}`
  if (page > 1) return `${root}/pages/${page}${thumb ? '?thumb=true' : ''}`
  return thumb ? `${root}/thumb` : root
}

export const juryApi = {
  signIn: (token: string, code: string) => api<JurorSession>(`${base}/sign-in`, { method: 'POST', body: { token, code } }),
  signOut: () => api<void>(`${base}/sign-out`, { method: 'POST' }),
  session: (signal?: AbortSignal) => api<JurorSession>(`${base}/session`, { signal }),
  confirm: () => api<JurorSession>(`${base}/confirm`, { method: 'POST' }),
  dashboard: (signal?: AbortSignal) => api<JurorDashboard>(`${base}/dashboard`, { signal }),
  work: (submissionId: number, signal?: AbortSignal) => api<JurorWork>(`${base}/works/${submissionId}`, { signal }),
  score: (submissionId: number, score: number, comment: string | null, version: number | null) =>
    api<JurorScoreResult>(`${base}/works/${submissionId}/score`, { method: 'PUT', body: { score, comment, version } }),
  submit: () => api<JurorDashboard>(`${base}/submit`, { method: 'POST' }),
}

/** The secret from the invitation link's #fragment ("#t=…"); browsers never send a fragment to any server. */
export function tokenFromHash(hash: string): string | null {
  const params = new URLSearchParams(hash.replace(/^#/, ''))
  const t = params.get('t')?.trim()
  return t ? t : null
}

/** Where the invitation's secret waits between the link and the code, for this tab only. */
export const TOKEN_KEY = 'meydan.jury.invitation'
