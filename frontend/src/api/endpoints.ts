import { noteServerTime } from '../lib/serverClock'
import { api, apiBlob, apiUpload } from './client'
import type {
  AuthResponse,
  CompetitionDetail,
  CompetitionResult,
  CompetitionSummary,
  ContactPayload,
  Gallery,
  GalleryWork,
  HomeData,
  LoginPayload,
  PlatformSettings,
  LoginsInfo,
  MySubmission,
  ProfilePayload,
  ProjectCard,
  ProjectDetail,
  PublicConfig,
  QrlogClaim,
  QrlogStart,
  RegisterPayload,
  RegistrationCompletePayload,
  RegistrationVerified,
  ResultListItem,
  Submission,
  SubmissionFile,
  User,
  VoteReceipt,
  Voting,
  VotingLeaderboard,
  VotingWorkDetail,
  VotingWorks,
  VotingAudit,
} from './types'

const enc = encodeURIComponent

export const catalog = {
  home: (signal?: AbortSignal) => api<HomeData>('/api/home', { signal }),
  projects: (signal?: AbortSignal) => api<ProjectCard[]>('/api/projects', { signal }),
  project: (slug: string, signal?: AbortSignal) => api<ProjectDetail>(`/api/projects/${enc(slug)}`, { signal }),
  competitions: (signal?: AbortSignal) => api<CompetitionSummary[]>('/api/competitions', { signal }),
  competition: (slug: string, signal?: AbortSignal) =>
    api<CompetitionDetail>(`/api/competitions/${enc(slug)}`, { signal }),
  /** The platform's own name and copy; anonymous, and safe to fail (the app carries defaults). */
  settings: (signal?: AbortSignal) => api<PlatformSettings>('/api/settings', { signal }),
}

/** Published results only; everything here is a 404 until an admin publishes the competition's result. */
export const resultsApi = {
  list: (signal?: AbortSignal) => api<ResultListItem[]>('/api/results', { signal }),
  result: (slug: string, signal?: AbortSignal) => api<CompetitionResult>(`/api/competitions/${enc(slug)}/results`, { signal }),
  gallery: (slug: string, category: string | null, signal?: AbortSignal) =>
    api<Gallery>(`/api/competitions/${enc(slug)}/gallery${category ? `?category=${enc(category)}` : ''}`, { signal }),
  work: (slug: string, reference: string, signal?: AbortSignal) =>
    api<GalleryWork>(`/api/competitions/${enc(slug)}/gallery/${enc(reference)}`, { signal }),
}

/**
 * The public vote. Reading is anonymous, but the answer is shaped to whoever asks: a signed-in visitor is told
 * which nomination they have already used. Casting needs a session and takes nothing but the address — the voter
 * comes from the token, the nomination from the work. There is no way to change or withdraw a vote.
 */
export const votingApi = {
  /** Also sets this page's idea of the server's clock (lib/serverClock) from the answer's `serverNow`. */
  summary: (slug: string, signal?: AbortSignal) =>
    api<Voting>(`/api/competitions/${enc(slug)}/voting`, { signal }).then((voting) => {
      noteServerTime(voting.serverNow)
      return voting
    }),
  /** Valid, set-aside and total votes, and one public line per admin decision. Aggregates only. */
  audit: (slug: string, signal?: AbortSignal) =>
    api<VotingAudit>(`/api/competitions/${enc(slug)}/voting/audit`, { signal }),
  /** `seed`: this visitor's shuffle (see `ballotSeed`), the same order on every page and reload. */
  works: (
    slug: string,
    q: { nomination?: string | null; q?: string | null; page?: number; pageSize?: number; seed?: number },
    signal?: AbortSignal,
  ) => {
    const p = new URLSearchParams()
    if (q.nomination) p.set('nomination', q.nomination)
    if (q.q) p.set('q', q.q)
    if (q.page && q.page > 1) p.set('page', String(q.page))
    if (q.pageSize) p.set('pageSize', String(q.pageSize))
    if (q.seed !== undefined) p.set('seed', String(q.seed))
    const query = p.toString()
    return api<VotingWorks>(`/api/competitions/${enc(slug)}/voting/works${query ? `?${query}` : ''}`, { signal })
  },
  work: (slug: string, reference: string, signal?: AbortSignal) =>
    api<VotingWorkDetail>(`/api/competitions/${enc(slug)}/voting/works/${enc(reference)}`, { signal }),
  /** `captchaToken`: the robot check's single-use token, when the server asks for one (PublicConfig.voteCaptcha). */
  vote: (slug: string, reference: string, captchaToken?: string) =>
    api<VoteReceipt>(`/api/competitions/${enc(slug)}/voting/works/${enc(reference)}/vote`, {
      method: 'POST',
      ...(captchaToken ? { body: { captchaToken } } : {}),
    }),
  /** The live ranking of every work on the ballot, with this viewer's own vote. */
  leaderboard: (slug: string, signal?: AbortSignal) =>
    api<VotingLeaderboard>(`/api/competitions/${enc(slug)}/voting/leaderboard`, { signal }),
}

export const authApi = {
  register: (body: RegisterPayload) => api<AuthResponse>('/api/auth/register', { method: 'POST', body }),
  login: (body: LoginPayload) => api<AuthResponse>('/api/auth/login', { method: 'POST', body }),
  logout: () => api<void>('/api/auth/logout', { method: 'POST' }),
  forgotPassword: (email: string) => api<void>('/api/auth/forgot-password', { method: 'POST', body: { email } }),
  resetPassword: (body: { email: string; token: string; password: string; confirmPassword: string }) =>
    api<void>('/api/auth/reset-password', { method: 'POST', body }),
  /** Email-first registration, step 1: the address and the robot check; the same answer whoever it belongs to. */
  registrationStart: (body: { email: string; captchaToken?: string }) =>
    api<void>('/api/auth/registration/start', { method: 'POST', body }),
  /** Step 2: the link's token; answers with the proof the completion form needs. */
  registrationVerify: (body: { token: string }) =>
    api<RegistrationVerified>('/api/auth/registration/verify', { method: 'POST', body }),
  /** Step 3: name, phone and password; creates the account and signs it in. */
  registrationComplete: (body: RegistrationCompletePayload) =>
    api<AuthResponse>('/api/auth/registration/complete', { method: 'POST', body }),
  /** The link from the confirmation e-mail. */
  confirmEmail: (body: { email: string; token: string }) => api<void>('/api/auth/confirm-email', { method: 'POST', body }),
  /** A new confirmation link for the signed-in account (at most one a minute, five a day). */
  resendConfirmation: () => api<void>('/api/auth/email-confirmation', { method: 'POST' }),
  /** "QRLog ilə daxil ol": opens a ticket (the claim secret lands in an HttpOnly cookie) and says where to send the person. */
  qrlogStart: (body: { returnTo?: string | null; link?: boolean }) =>
    api<QrlogStart>('/api/auth/qrlog/start', { method: 'POST', body }),
  /** Polled after coming back from QRLog; the cookie identifies the ticket. */
  qrlogClaim: () => api<QrlogClaim>('/api/auth/qrlog/claim', { method: 'POST' }),
  qrlogCancel: () => api<void>('/api/auth/qrlog/cancel', { method: 'POST' }),
}

export const contactApi = {
  /** Sent at once by e-mail to the organiser; nothing is stored. A 503 means it did not go. */
  send: (body: ContactPayload) => api<void>('/api/contact', { method: 'POST', body }),
}

export const configApi = {
  get: (signal?: AbortSignal) => api<PublicConfig>('/api/config', { signal }),
}

export const meApi = {
  submissions: (signal?: AbortSignal) => api<MySubmission[]>('/api/me/submissions', { signal }),
  profile: (signal?: AbortSignal) => api<User>('/api/me/profile', { signal }),
  updateProfile: (body: ProfilePayload) => api<User>('/api/me/profile', { method: 'PUT', body }),
  logins: (signal?: AbortSignal) => api<LoginsInfo>('/api/me/logins', { signal }),
  unlinkQrlog: () => api<void>('/api/me/logins/qrlog', { method: 'DELETE' }),
}

export const submissionsApi = {
  /** Opens or resumes the draft for this competition + category. */
  start: (competitionSlug: string, categorySlug: string | null) =>
    api<Submission>('/api/submissions', { method: 'POST', body: { competitionSlug, categorySlug } }),
  get: (id: number, signal?: AbortSignal) => api<Submission>(`/api/submissions/${id}`, { signal }),
  save: (id: number, answers: Record<string, string | null>) =>
    api<Submission>(`/api/submissions/${id}`, { method: 'PUT', body: { answers } }),
  check: (id: number) => api<Submission>(`/api/submissions/${id}/check`, { method: 'POST' }),
  submit: (id: number) => api<Submission>(`/api/submissions/${id}/submit`, { method: 'POST' }),
  upload: (id: number, fieldKey: string, file: File, onProgress: (fraction: number) => void) => {
    const form = new FormData()
    form.append('fieldKey', fieldKey)
    form.append('file', file, file.name)
    return apiUpload<SubmissionFile>(`/api/submissions/${id}/files`, form, onProgress)
  },
  removeFile: (id: number, fileId: number) =>
    api<void>(`/api/submissions/${id}/files/${fileId}`, { method: 'DELETE' }),
  fileBlob: (id: number, fileId: number, signal?: AbortSignal) =>
    apiBlob(`/api/submissions/${id}/files/${fileId}`, signal),
  /** The card-size copy (a few dozen KB) for lists; 404 until it is made. Never the original. */
  thumbBlob: (id: number, fileId: number, signal?: AbortSignal) =>
    apiBlob(`/api/submissions/${id}/files/${fileId}/thumb`, signal),
  /**
   * Admins only: a later page (2, 3, …) of a work's PDF as a metadata-free image, rendered on request — `thumb` for
   * the card-size copy. Page 1 is the file's own copy (thumbBlob). The PDF itself is never served.
   */
  pageBlob: (id: number, fileId: number, page: number, signal?: AbortSignal, thumb = false) =>
    apiBlob(`/api/admin/submissions/${id}/files/${fileId}/pages/${page}${thumb ? '?thumb=true' : ''}`, signal),
}
