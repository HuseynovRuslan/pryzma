import { api, apiBlob, apiUpload } from '../api/client'
import { submissionsApi } from '../api/endpoints'
import type {
  CompetitionPhase,
  CompetitionStatus,
  FieldType,
  PlatformSettings,
  ProjectStatus,
  SubmissionStatus,
  VotingState,
} from '../api/types'

// Mirrors Meydan.Api.Features.Admin DTOs.

export interface Paged<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

/**
 * What a permanent deletion would take with it, counted by the server. The confirmation dialog shows exactly
 * these numbers — it never works them out from whatever the browser happens to have loaded.
 */
/** What an admin may change about the platform's own words (the image has its own endpoints). */
export interface PlatformSettingsInput {
  platformName: string
  homeHeroTitle: string
  homeHeroSubtitle: string
  authPromoTitle: string
}

export interface DeleteImpact {
  title: string
  context: string | null
  competitions: number
  categories: number
  fields: number
  submissions: number
  answers: number
  files: number
  results: number
  winners: number
  notes: number
  /** Something else would go with it, so the dialog asks for the word SİL before it enables the button. */
  requiresTypedConfirmation: boolean
}

export interface DeleteReport {
  removed: DeleteImpact
  storedFilesDeleted: number
  storedFilesFailed: number
}

export interface AdminProject {
  id: number
  name: string
  slug: string
  shortDescription: string
  description: string | null
  coverImage: string | null
  accentColor: string | null
  status: ProjectStatus
  sortOrder: number
  competitionCount: number
  submissionCount: number
  createdAt: string
  updatedAt: string
  canDelete: boolean
  /** Demo content created by `seed-demo` (removed by `remove-demo`). */
  isDemo: boolean
}

export interface ProjectInput {
  name: string
  slug: string
  shortDescription: string
  description: string
  accentColor: string
  sortOrder: number
}

export interface AdminCompetition {
  id: number
  projectId: number
  projectName: string
  projectStatus: ProjectStatus
  title: string
  slug: string
  shortDescription: string
  description: string | null
  rules: string | null
  requirements: string | null
  /** One block per nomination: first line the name, the rest its explanation. */
  nominations: string | null
  judgingCriteria: string | null
  usageTerms: string | null
  contactNote: string | null
  coverImage: string | null
  startDate: string | null
  endDate: string | null
  resultsAnnouncedOn: string | null
  exhibitionOn: string | null
  prize: string | null
  status: CompetitionStatus
  phase: CompetitionPhase
  isAcceptingSubmissions: boolean
  hasCategories: boolean
  categoryLabel: string | null
  maxSubmissionsPerUser: number | null
  sortOrder: number
  categoryCount: number
  activeCategoryCount: number
  fieldCount: number
  submissionCount: number
  draftCount: number
  createdAt: string
  updatedAt: string
  canDelete: boolean
  resultState: ResultState
  /** Demo content created by `seed-demo` (removed by `remove-demo`). */
  isDemo: boolean
  /** The public vote's window, and where it stands right now. Both null: this competition has no vote. */
  votingStartsAt: string | null
  votingEndsAt: string | null
  votingState: VotingState
}

export interface CompetitionInput {
  projectId: number
  title: string
  slug: string
  shortDescription: string
  description: string
  rules: string
  requirements: string
  nominations: string
  judgingCriteria: string
  usageTerms: string
  contactNote: string
  startDate: string | null
  endDate: string | null
  resultsAnnouncedOn: string | null
  exhibitionOn: string | null
  prize: string
  hasCategories: boolean
  categoryLabel: string
  maxSubmissionsPerUser: number | null
  sortOrder: number
  /** Both or neither: a half-set window is refused by the server rather than half-believed. */
  votingStartsAt: string | null
  votingEndsAt: string | null
}

/** One work in a nomination's tally. Named with its participant — an admin choosing a winner must know whose it is. */
export interface AdminVoteWork {
  submissionId: number
  reference: string
  title: string
  participantName: string
  status: SubmissionStatus
  voteCount: number
  /** 1 for the most-voted work; works level on votes share a place. The vote's rank — never the official place. */
  place: number
  isWinner: boolean
  /** Of its votes, how many came from accounts opened after voting began (they may vote; this is for a second look). */
  lateVoteCount: number
  /** Votes an admin set aside as fraudulent (D12): kept, and not in `voteCount`. */
  invalidatedVoteCount: number
  /** The work's picture as every admin screen shows it — for a PDF, the page that stands for it. */
  image?: AdminPrintImage | null
  /** Pages of the work's public PDFs together (0: none). */
  pdfPages?: number
  /** The official place the work holds in the result (1–3; 1 as a category's winner), published or not; null otherwise. */
  officialPlace?: number | null
}

export interface AdminVotingNomination {
  categoryId: number
  slug: string
  name: string
  isActive: boolean
  workCount: number
  voteCount: number
  /** Two or more works level at the top. The tally names no winner then — an admin chooses. */
  isTie: boolean
  works: AdminVoteWork[]
}

export interface AdminVoting {
  state: VotingState
  startsAt: string | null
  endsAt: string | null
  /** How many people voted here at all — never who. */
  voterCount: number
  voteCount: number
  /** Of the voters, how many opened their account after voting began. */
  lateVoterCount: number
  nominations: AdminVotingNomination[]
  /** Final works in no nomination, so nobody can vote for them. Empty is the ordinary case. */
  unplaced: AdminVoteWork[]
  /** Votes set aside by an admin (D12); `voteCount` is the counted ones. */
  invalidatedVoteCount: number
}

/**
 * One work as the suspicion score sees it, over the votes that still count. `expectedFake` is the sum of their scores
 * over 100; `rawVotes` and `invalidatedVotes` are every vote received and those an admin set aside.
 */
export interface AdminSuspicionWork {
  submissionId: number
  reference: string
  title: string
  votes: number
  high: number
  medium: number
  low: number
  expectedFake: number
  cleanEstimate: number
  rawVotes: number
  invalidatedVotes: number
  /** Counted votes of a phone cluster (3+ voting accounts on one phone) — the only ones that may be set aside (D12). */
  phoneClusterVotes: number
  /** Counted votes that meet R2, coordinated identity. */
  coordinatedIdentityVotes?: number
}

/**
 * D12, as the owner narrowed it. `PhoneCluster`: the voter's phone is held by 3+ voting accounts of the competition — the
 * only ground a vote may be set aside on. `CoordinatedIdentity`: R2 (a new account whose name many voting accounts share,
 * enough of them opened from one address) — a ground only while the server has R2 switched on; otherwise those votes
 * come as `Weak` with `coordinatedIdentity` set. `Weak`: suspicion only (a shared connection, timing, a look-alike
 * e-mail). `None`: no sign.
 */
export type VoteEvidence = 'PhoneCluster' | 'CoordinatedIdentity' | 'DisposableEmailDuringVoting' | 'Weak' | 'None'

/** D14 for one competition (VoteReview.DisposableEmailAsync): the frozen rule, what it meets, masked evidence. */
export interface AdminDisposableEmail {
  policyId: string
  version: number
  evidenceType: string
  enabled: boolean
  reason: string
  source: string
  domainCount: number
  votingStartsAt: string | null
  votingEndsAt: string | null
  votingClosed: boolean
  /** Valid votes meeting the rule now — what applying it sets aside. */
  eligibleVotes: number
  setAsideUnderRule: number
  /** Meeting the rule but set aside earlier under another rule — never counted twice. */
  setAsideOtherwise: number
  /** Listed domain, account opened before the vote: reported, never eligible. */
  openedBeforeVoting: number
  works: { submissionId: number; reference: string; validNow: number; eligible: number; validAfter: number; setAsideUnderRule: number }[]
  domains: { domain: string; provider: string; votes: number; eligible: number; setAsideUnderRule: number }[]
  evidence: {
    voteId: number
    reference: string
    maskedEmail: string
    domain: string
    provider: string
    accountOpenedAt: string
    votedAt: string
    valid: boolean
    setAsideUnderRule: boolean
  }[]
}

/** A vote scoring 30% or more, the account behind it and every reason in its score. */
export interface AdminSuspiciousVote {
  score: number
  submissionId: number
  reference: string
  voterName: string
  signedUpAt: string
  votedAt: string
  reasons: string[]
  voteId: number
  evidence: VoteEvidence
}

/** The suspicion score of the vote: a weighted list of signs, not a measured probability. */
export interface AdminVoteSuspicion {
  voteCount: number
  high: number
  medium: number
  low: number
  works: AdminSuspicionWork[]
  votes: AdminSuspiciousVote[]
  /** Votes set aside by an admin, across the competition. */
  invalidatedCount: number
}

/** One work's evidence, in numbers only: no phone, e-mail or address ever reaches the page. */
export interface AdminWorkEvidence {
  submissionId: number
  reference: string
  title: string
  rawVotes: number
  validVotes: number
  invalidatedVotes: number
  phoneClusterVotes: number
  repeatedPhoneVotes: number
  largestPhoneGroup: number
  repeatedNameVotes: number
  largestNameGroup: number
  largestIpGroup: number
  authorIpVotes: number
  authorPhoneVotes: number
  newAccountVotes: number
  medianSecondsToVote: number | null
  largestBurst15Min: number
  firstSignUp: string | null
  lastSignUp: string | null
  firstVote: string | null
  lastVote: string | null
  /** Counted votes that meet R2, coordinated identity, whether or not it is switched on. */
  coordinatedIdentityVotes?: number
  /** Whether R2 is ground to set a vote aside, or only a review sign. */
  coordinatedIdentityEnabled?: boolean
}

/** One vote under review. Groups are labels with sizes ("T1", 77) — never the phone or the address itself. */
export interface AdminReviewVote {
  voteId: number
  voterName: string
  signedUpAt: string
  votedAt: string
  score: number
  evidence: VoteEvidence
  reasons: string[]
  phoneGroup: string | null
  phoneGroupSize: number
  nameGroup: string | null
  nameGroupSize: number
  ipGroup: string | null
  ipGroupSize: number
  invalidatedAt: string | null
  invalidatedBy: string | null
  invalidationReason: string | null
  /** Meets R2, coordinated identity, whether or not it is switched on. */
  coordinatedIdentity?: boolean
}

export interface AdminWorkReview {
  evidence: AdminWorkEvidence
  votes: AdminReviewVote[]
}

export interface AdminVoteDecision {
  changed: number
  references: string[]
}

export interface AdminCategory {
  id: number
  competitionId: number
  name: string
  slug: string
  description: string | null
  sortOrder: number
  isActive: boolean
  submissionCount: number
}

export interface CategoryInput {
  name: string
  slug?: string
  description?: string
  isActive: boolean
}

export interface ImportItem {
  name: string
  slug?: string
  description?: string
}

export interface AdminField {
  id: number
  competitionId: number
  key: string
  label: string
  fieldType: FieldType
  placeholder: string | null
  helpText: string | null
  isRequired: boolean
  isSubmissionTitle: boolean
  isActive: boolean
  isPublicAfterResults: boolean
  optionsJson: string | null
  sortOrder: number
  answerCount: number
  isLocked: boolean
}

export interface FieldInput {
  key?: string
  label: string
  fieldType: FieldType
  placeholder?: string
  helpText?: string
  isRequired: boolean
  isSubmissionTitle: boolean
  isActive: boolean
  isPublicAfterResults: boolean
  optionsJson?: string | null
}

export interface UploadPolicy {
  maxFileSizeMb: number
  maxFilesPerSubmission: number
  allowedTypes: { key: string; label: string; extensions: string[] }[]
}

export interface Ref {
  id: number
  name: string
}

export interface AdminSubmissionRow {
  id: number
  referenceNumber: string | null
  title: string
  status: SubmissionStatus
  submittedAt: string | null
  participant: { id: string; name: string; email: string | null }
  project: Ref
  competition: Ref
  category: Ref | null
  fileCount: number
  isWinner: boolean
  /** The file a list shows for this work and whether its small copy exists (lists never download originals). */
  preview: AdminPreview | null
  /** Demo content created by `seed-demo`. */
  isDemo: boolean
}

export interface AdminSubmissionDetail {
  id: number
  referenceNumber: string | null
  title: string
  status: SubmissionStatus
  createdAt: string
  updatedAt: string
  submittedAt: string | null
  participant: { id: string; firstName: string; lastName: string; email: string | null; phone: string | null; registeredAt: string }
  project: Ref
  competition: Ref
  competitionSlug: string
  categoryLabel: string | null
  category: Ref | null
  answers: { fieldId: number; key: string; label: string; currentLabel: string; fieldType: FieldType; fieldActive: boolean; value: string | null }[]
  files: AdminFile[]
  notes: { id: number; text: string; authorName: string; createdAt: string }[]
  history: { from: SubmissionStatus; to: SubmissionStatus; changedBy: string | null; changedAt: string; comment: string | null }[]
  allowedStatuses: SubmissionStatus[]
  result: {
    /** The category (settlement) the work was entered in — the place it can win. Null: the competition as a whole. */
    category: Ref | null
    isWinner: boolean
    canBeWinner: boolean
    ineligibleReason: string | null
    /** That category's winner when it is another work. */
    currentWinner: { submissionId: number; reference: string | null; title: string; participantName: string } | null
    resultPublished: boolean
    /** Published and visible right now (false while the competition accepts entries again). */
    resultPublicNow: boolean
    /** The place this work holds in a competition without categories (1–3), if any. */
    place: number | null
  }
  /** Admin's explicit gallery image (null = automatic). */
  previewFileId: number | null
  /** What the gallery will show: the choice if still valid, else the automatic fallback. */
  effectivePreviewFileId: number | null
  /** Demo content created by `seed-demo`. */
  isDemo: boolean
}

/** State of the metadata-free public copy of a file. */
export type PublicAssetStatus = 'Pending' | 'Ready' | 'Failed'

/**
 * The picture an admin list shows for a work: which file, whether its card-size copy is ready, and — for a PDF — the
 * page that stands for it (the server decides: the admin's choice, else 2 of two or more pages, else 1).
 */
export interface AdminPreview {
  fileId: number
  thumbnailReady: boolean
  page?: number
}

export interface AdminFile {
  id: number
  fieldKey: string | null
  fieldLabel: string | null
  originalFileName: string
  contentType: string
  sizeBytes: number
  uploadedAt: string
  /** The file's field is shown in the gallery after results. */
  isPublicField: boolean
  publicAsset: PublicAssetStatus
  publicAssetError: string | null
  /** The small copy exists: lists show it instead of downloading the original. */
  thumbnailReady: boolean
  /** 'NotScanned' unless an antivirus is configured. */
  scanStatus: 'NotScanned' | 'Clean' | 'Infected'
  canBePreview: boolean
  /** An admin took the file off every public page (ballot, work page, gallery); the file itself is untouched. */
  hiddenFromPublic: boolean
  /** A PDF's page count once its safe first page is ready; 0 otherwise and for anything else. */
  pdfPages?: number
  /** The page an admin chose to stand for this PDF; null: automatic. */
  previewPage?: number | null
  /** The page that stands for it on every admin screen and print: the choice, else page 2 of two or more, else 1. */
  representativePage?: number
  /** The page the automatic choice gives (the server's rule), for the picker's "avtomatik" mark. */
  automaticPage?: number
}

// ---- Print / PDF ---------------------------------------------------------------------------------------------

/** The picture a printed page shows: the gallery's own choice, as its metadata-free copy where ready. */
export interface AdminPrintImage {
  fileId: number
  /** The 1600 px copy is ready (adminApi.publicAssetBlob); otherwise only the card-size copy (thumbBlob). */
  large: boolean
  width: number | null
  height: number | null
  /**
   * For a PDF: the page that stands for the work on every admin screen and print — the admin's choice, else 2 when it
   * has two or more pages (a first page is often only a cover), else 1. The server decides; a page after the first
   * comes from adminApi.documentPageBlob.
   */
  page?: number
  /** For a PDF: its page count ("Önizləmə: 2/4"); 0 or absent for a picture. */
  pages?: number
}

/** One work as management reads it on paper. No contact or registration data exists in this shape at all. */
export interface AdminPrintItem {
  id: number
  reference: string | null
  title: string
  /** The category, for a competition that has categories. */
  category: string | null
  /** Public choice answers that sort the works, e.g. { label: 'Nominasiya', value: '…' }. */
  facts: { label: string; value: string }[]
  conceptLabel: string | null
  /** The public description, in full; null when that field is not public or was left empty. */
  concept: string | null
  image: AdminPrintImage | null
  /** "First Last" — only when the page asked for it (the admin's print pages do, by default: internal documents). */
  author: string | null
  /** Pages of the work's public PDFs together (0: none), for the "PDF · N səhifə" badge. */
  pdfPages: number
  /** The work's whole public material in reading order, for the printed appendix. */
  material: AdminPrintMaterial[]
}

/**
 * One page of a work's material: a picture (its safe copy, adminApi.publicAssetBlob) or a PDF page (page 1 is the
 * file's safe copy; later pages adminApi.documentPageBlob), with the PDF's page count.
 */
export interface AdminPrintMaterial {
  fileId: number
  kind: 'image' | 'pdf'
  page: number
  pages: number
}

export interface AdminPrint {
  competitionTitle: string | null
  /** Works matching the filters; at most `limit` of them are in `items`. */
  total: number
  limit: number
  authorIncluded: boolean
  items: AdminPrintItem[]
}

export interface SubmissionFilters {
  projectId?: string
  competitionId?: string
  categoryId?: string
  status?: string
  from?: string
  to?: string
  q?: string
  /** "true": winners only. */
  winner?: string
  /** "true": demo content only (`seed-demo`); "false": real submissions only. */
  demo?: string
  page?: string
  pageSize?: string
}

/** A competition the overview can be narrowed to; `isOpen` while it takes entries. */
export interface DashboardCompetition {
  id: number
  title: string
  status: CompetitionStatus
  phase: CompetitionPhase
  isOpen: boolean
}

/**
 * The overview. With `competitionId` set the submission figures and the recent list are that competition's only;
 * the project and open-competition counts are always the platform's.
 */
export interface Dashboard {
  publishedProjects: number
  openCompetitions: number
  totalSubmissions: number
  draftSubmissions: number
  submittedSubmissions: number
  underReviewSubmissions: number
  approvedSubmissions: number
  rejectedSubmissions: number
  recentSubmissions: AdminSubmissionRow[]
  competitionId: number | null
  competitions: DashboardCompetition[]
}

export interface AdminParticipant {
  id: string
  name: string
  email: string | null
  phone: string | null
  registeredAt: string
  submissionCount: number
  isAdmin: boolean
  /** A demo participant created by `seed-demo`: cannot sign in. */
  isDemo: boolean
  /** The account has cast a vote, counted or set aside: it is never removed (the server refuses with 409). */
  hasVotes: boolean
  /** Nothing of their own left, no vote, and not an admin's: the only kind of account that may be removed. */
  canDelete: boolean
}

/** Which podium of a two-choice result. */
export type AwardCategory = 'Jury' | 'Public'

/** One place of a podium as the admin sees it: the work, the announced name beside the account's, the number. */
export interface AdminAwardPlace {
  place: number
  submissionId: number
  reference: string
  title: string
  participantName: string
  accountName: string
  points: number
  status: SubmissionStatus
  preview: AdminPreview | null
  /** The work's valid votes now — the public podium's number must equal it. */
  validVotes: number
}

export interface AdminAwardPodium {
  category: AwardCategory
  places: AdminAwardPlace[]
  /** Where the numbers come from (never public). */
  source: string | null
  recordedAt: string | null
  recordedBy: string | null
}

/** The two podiums of a result, with every work that can stand on one (most valid votes first). */
export interface AdminAwards {
  competitionId: number
  resultPublished: boolean
  voting: VotingState
  jury: AdminAwardPodium
  public: AdminAwardPodium
  candidates: AdminPlaceCandidate[]
}

export interface AwardPlaceInput {
  submissionId: number
  participantName: string
  points: number
}

export interface AuditEntry {
  id: number
  action: string
  entityType: string
  entityId: string
  summary: string | null
  userName: string | null
  createdAt: string
}

const qs = (params: Record<string, string | number | undefined | null>) => {
  const search = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') search.set(k, String(v))
  const s = search.toString()
  return s ? `?${s}` : ''
}

/** A competition's result: NoWinner → Prepared (at least one winner, private) → Published. */
export type ResultState = 'NoWinner' | 'Prepared' | 'Published'

export interface AdminWinner {
  submissionId: number
  reference: string
  title: string
  participantName: string
  status: SubmissionStatus
  /** Which file to show and whether its small copy is ready (never the original in a list). */
  preview: AdminPreview | null
  selectedAt: string
  selectedBy: string | null
}

/** A place a winner is chosen for: a category (settlement), or the whole competition when it has none (category null). */
export interface AdminWinnerSlot {
  category: Ref | null
  categoryActive: boolean
  eligibleCount: number
  winner: AdminWinner | null
}

/** One place of a competition without categories (1 is the overall winner) and the work holding it. */
export interface AdminPlace {
  place: number
  winner: AdminWinner | null
}

/** A work that can take a place, with its public vote. Works level on votes share a rank (D3: a person decides). */
export interface AdminPlaceCandidate {
  submissionId: number
  reference: string
  title: string
  participantName: string
  preview: AdminPreview | null
  voteCount: number
  voteRank: number
  /** Of its votes, how many came from accounts opened after voting began. */
  lateVoteCount: number
  /** Set aside by an admin (D12): not in `voteCount`, not in the rank. */
  invalidatedVoteCount: number
}

export interface AdminResult {
  competitionId: number
  competitionTitle: string
  competitionSlug: string
  hasCategories: boolean
  categoryLabel: string | null
  state: ResultState
  categoryCount: number
  winnerCount: number
  slots: AdminWinnerSlot[]
  publishedAt: string | null
  publishedBy: string | null
  announcement: string | null
  /** The results banner (an admin upload), shown on the public results pages. */
  coverImage: string | null
  isPublicNow: boolean
  publishProblems: string[]
  eligibleCount: number
  publicFields: string[]
  hasPublicFileField: boolean
  assetsPending: number
  assetFailures: { submissionId: number; reference: string; fileId: number; code: string }[]
  /** First, second and third place of a competition without categories; empty for one with categories. */
  places: AdminPlace[]
  /** Works that can take a place, most votes first; empty for a competition with categories. */
  placeCandidates: AdminPlaceCandidate[]
  voting: VotingState
  /** Places recorded on the two podiums of a two-choice result. */
  juryAwardCount?: number
  publicAwardCount?: number
}

export const adminApi = {
  dashboard: (signal?: AbortSignal, competitionId?: number) =>
    api<Dashboard>(`/api/admin/dashboard${competitionId ? `?competitionId=${competitionId}` : ''}`, { signal }),
  uploadPolicy: (signal?: AbortSignal) => api<UploadPolicy>('/api/admin/upload-policy', { signal }),
  audit: (entityType: string, entityId: number | string, signal?: AbortSignal) =>
    api<AuditEntry[]>(`/api/admin/audit${qs({ entityType, entityId })}`, { signal }),
  /** demo: "true" demo participants only, "false" real ones only, undefined everyone. */
  participants: (q: string, page: number, demo: string | undefined, signal?: AbortSignal) =>
    api<Paged<AdminParticipant>>(`/api/admin/participants${qs({ q, page, demo })}`, { signal }),
  participantDeleteImpact: (id: string, signal?: AbortSignal) =>
    api<DeleteImpact>(`/api/admin/participants/${id}/delete-impact`, { signal }),
  deleteParticipantPermanently: (id: string) =>
    api<DeleteReport>(`/api/admin/participants/${id}/permanent`, { method: 'DELETE' }),

  /** The platform's own public words; the same shape the public /api/settings returns. */
  settings: (signal?: AbortSignal) => api<PlatformSettings>('/api/admin/settings', { signal }),
  updateSettings: (body: PlatformSettingsInput) =>
    api<PlatformSettings>('/api/admin/settings', { method: 'PUT', body }),
  uploadAuthImage: (file: File, onProgress: (f: number) => void) => {
    const form = new FormData()
    form.append('image', file, file.name)
    return apiUpload<PlatformSettings>('/api/admin/settings/auth-image', form, onProgress)
  },
  removeAuthImage: () => api<PlatformSettings>('/api/admin/settings/auth-image', { method: 'DELETE' }),

  projects: (signal?: AbortSignal) => api<AdminProject[]>('/api/admin/projects', { signal }),
  project: (id: number, signal?: AbortSignal) => api<AdminProject>(`/api/admin/projects/${id}`, { signal }),
  createProject: (body: ProjectInput) => api<AdminProject>('/api/admin/projects', { method: 'POST', body }),
  updateProject: (id: number, body: ProjectInput) => api<AdminProject>(`/api/admin/projects/${id}`, { method: 'PUT', body }),
  projectStatus: (id: number, status: ProjectStatus) =>
    api<AdminProject>(`/api/admin/projects/${id}/status`, { method: 'PUT', body: { status } }),
  deleteProject: (id: number) => api<void>(`/api/admin/projects/${id}`, { method: 'DELETE' }),
  projectDeleteImpact: (id: number, signal?: AbortSignal) =>
    api<DeleteImpact>(`/api/admin/projects/${id}/delete-impact`, { signal }),
  deleteProjectPermanently: (id: number) =>
    api<DeleteReport>(`/api/admin/projects/${id}/permanent`, { method: 'DELETE' }),
  uploadCover: (id: number, file: File, onProgress: (f: number) => void) => {
    const form = new FormData()
    form.append('file', file, file.name)
    return apiUpload<AdminProject>(`/api/admin/projects/${id}/cover`, form, onProgress)
  },
  removeCover: (id: number) => api<AdminProject>(`/api/admin/projects/${id}/cover`, { method: 'DELETE' }),

  competitions: (projectId?: number, signal?: AbortSignal) =>
    api<AdminCompetition[]>(`/api/admin/competitions${qs({ projectId })}`, { signal }),
  competition: (id: number, signal?: AbortSignal) => api<AdminCompetition>(`/api/admin/competitions/${id}`, { signal }),
  /** What the public vote produced. Read-only: the window is a competition setting, the winner a results decision. */
  voting: (id: number, signal?: AbortSignal) => api<AdminVoting>(`/api/admin/competitions/${id}/voting`, { signal }),
  voteSuspicion: (id: number, signal?: AbortSignal) =>
    api<AdminVoteSuspicion>(`/api/admin/competitions/${id}/voting/suspicion`, { signal }),
  /** One work's evidence and every vote it received, for review. */
  voteReview: (id: number, submissionId: number, signal?: AbortSignal) =>
    api<AdminWorkReview>(`/api/admin/competitions/${id}/voting/suspicion/works/${submissionId}`, { signal }),
  /** Sets the chosen votes aside as fraudulent — all or none, with a reason, audited (D12). */
  /** D14: the frozen disposable-e-mail rule for this competition. Read-only. */
  disposableEmailRule: (id: number, signal?: AbortSignal) =>
    api<AdminDisposableEmail>(`/api/admin/competitions/${id}/voting/rules/disposable-email`, { signal }),
  /** D14 applied competition-wide; refused unless the server counts exactly `expectedVotes`. Never deletes a vote. */
  applyDisposableEmailRule: (id: number, expectedVotes: number, reason?: string) =>
    api<AdminDisposableEmail>(`/api/admin/competitions/${id}/voting/rules/disposable-email/apply`, {
      method: 'POST',
      body: { expectedVotes, reason },
    }),
  invalidateVotes: (id: number, voteIds: number[], reason: string) =>
    api<AdminVoteDecision>(`/api/admin/competitions/${id}/voting/votes/invalidate`, { method: 'POST', body: { voteIds, reason } }),
  /** Restores votes set aside earlier — with a reason, audited. */
  restoreVotes: (id: number, voteIds: number[], reason: string) =>
    api<AdminVoteDecision>(`/api/admin/competitions/${id}/voting/votes/restore`, { method: 'POST', body: { voteIds, reason } }),
  createCompetition: (body: CompetitionInput) =>
    api<AdminCompetition>('/api/admin/competitions', { method: 'POST', body }),
  updateCompetition: (id: number, body: CompetitionInput) =>
    api<AdminCompetition>(`/api/admin/competitions/${id}`, { method: 'PUT', body }),
  competitionStatus: (id: number, status: CompetitionStatus) =>
    api<AdminCompetition>(`/api/admin/competitions/${id}/status`, { method: 'PUT', body: { status } }),
  deleteCompetition: (id: number) => api<void>(`/api/admin/competitions/${id}`, { method: 'DELETE' }),
  competitionDeleteImpact: (id: number, signal?: AbortSignal) =>
    api<DeleteImpact>(`/api/admin/competitions/${id}/delete-impact`, { signal }),
  deleteCompetitionPermanently: (id: number) =>
    api<DeleteReport>(`/api/admin/competitions/${id}/permanent`, { method: 'DELETE' }),
  /** The competition's own poster (lists and its page); re-encoded server-side like project covers. */
  uploadCompetitionCover: (id: number, file: File, onProgress: (f: number) => void) => {
    const form = new FormData()
    form.append('cover', file, file.name)
    return apiUpload<AdminCompetition>(`/api/admin/competitions/${id}/cover`, form, onProgress)
  },
  removeCompetitionCover: (id: number) => api<AdminCompetition>(`/api/admin/competitions/${id}/cover`, { method: 'DELETE' }),

  categories: (competitionId: number, signal?: AbortSignal) =>
    api<AdminCategory[]>(`/api/admin/competitions/${competitionId}/categories`, { signal }),
  createCategory: (competitionId: number, body: CategoryInput) =>
    api<AdminCategory>(`/api/admin/competitions/${competitionId}/categories`, { method: 'POST', body }),
  updateCategory: (competitionId: number, id: number, body: CategoryInput) =>
    api<AdminCategory>(`/api/admin/competitions/${competitionId}/categories/${id}`, { method: 'PUT', body }),
  deleteCategory: (competitionId: number, id: number) =>
    api<void>(`/api/admin/competitions/${competitionId}/categories/${id}`, { method: 'DELETE' }),
  reorderCategories: (competitionId: number, ids: number[]) =>
    api<AdminCategory[]>(`/api/admin/competitions/${competitionId}/categories/order`, { method: 'PUT', body: { ids } }),
  importCategories: (competitionId: number, items: ImportItem[]) =>
    api<AdminCategory[]>(`/api/admin/competitions/${competitionId}/categories/import`, { method: 'POST', body: { items } }),

  fields: (competitionId: number, signal?: AbortSignal) =>
    api<AdminField[]>(`/api/admin/competitions/${competitionId}/fields`, { signal }),
  createField: (competitionId: number, body: FieldInput) =>
    api<AdminField>(`/api/admin/competitions/${competitionId}/fields`, { method: 'POST', body }),
  updateField: (competitionId: number, id: number, body: FieldInput) =>
    api<AdminField>(`/api/admin/competitions/${competitionId}/fields/${id}`, { method: 'PUT', body }),
  deleteField: (competitionId: number, id: number) =>
    api<void>(`/api/admin/competitions/${competitionId}/fields/${id}`, { method: 'DELETE' }),
  reorderFields: (competitionId: number, ids: number[]) =>
    api<AdminField[]>(`/api/admin/competitions/${competitionId}/fields/order`, { method: 'PUT', body: { ids } }),

  submissions: (filters: SubmissionFilters, signal?: AbortSignal) =>
    api<Paged<AdminSubmissionRow>>(`/api/admin/submissions${qs({ ...filters })}`, { signal }),
  submissionDeleteImpact: (id: number, signal?: AbortSignal) =>
    api<DeleteImpact>(`/api/admin/submissions/${id}/delete-impact`, { signal }),
  deleteSubmissionPermanently: (id: number) =>
    api<DeleteReport>(`/api/admin/submissions/${id}/permanent`, { method: 'DELETE' }),
  submission: (id: number, signal?: AbortSignal) => api<AdminSubmissionDetail>(`/api/admin/submissions/${id}`, { signal }),
  /**
   * The list's selection for the print view. Without a status filter, only works that can still win. The author's
   * name travels only with `includeAuthor`; contact and registration data never do.
   */
  printSubmissions: (filters: SubmissionFilters, includeAuthor: boolean, signal?: AbortSignal, finalists = false) =>
    api<AdminPrint>(
      `/api/admin/submissions/print${qs({
        ...filters,
        page: undefined,
        pageSize: undefined,
        includeAuthor: includeAuthor ? 'true' : undefined,
        finalists: finalists ? 'true' : undefined,
      })}`,
      { signal },
    ),
  submissionStatus: (id: number, status: SubmissionStatus, comment: string) =>
    api<AdminSubmissionDetail>(`/api/admin/submissions/${id}/status`, { method: 'PUT', body: { status, comment } }),
  setPreview: (id: number, fileId: number | null) =>
    api<AdminSubmissionDetail>(`/api/admin/submissions/${id}/preview`, { method: 'PUT', body: { fileId } }),
  /** Takes one file off every public page, or puts it back. Nothing is deleted. */
  setFileHidden: (id: number, fileId: number, hidden: boolean) =>
    api<AdminSubmissionDetail>(`/api/admin/submissions/${id}/files/${fileId}/visibility`, { method: 'PUT', body: { hidden } }),
  /**
   * The page of a PDF that stands for the work everywhere (1…its page count), or null for the automatic choice. Only
   * the choice is stored — the PDF is never changed. Audited.
   */
  setPdfPreviewPage: (id: number, fileId: number, page: number | null) =>
    api<AdminSubmissionDetail>(`/api/admin/submissions/${id}/files/${fileId}/preview-page`, { method: 'PUT', body: { page } }),
  /** One work for the work viewer: reference, title, author, nomination, concept and every picture and PDF page. */
  workMaterial: (id: number, signal?: AbortSignal) => api<AdminPrintItem>(`/api/admin/submissions/${id}/material`, { signal }),
  /** A later page (2, 3, …) of a work's PDF as a metadata-free image; `thumb` for the card-size copy. */
  documentPageBlob: (id: number, fileId: number, page: number, signal?: AbortSignal, thumb = false) =>
    submissionsApi.pageBlob(id, fileId, page, signal, thumb),
  /** The metadata-free derivative exactly as the public will see it. */
  publicAssetBlob: (id: number, fileId: number, signal?: AbortSignal) =>
    apiBlob(`/api/admin/submissions/${id}/files/${fileId}/public`, signal),
  addNote: (id: number, text: string) =>
    api<AdminSubmissionDetail>(`/api/admin/submissions/${id}/notes`, { method: 'POST', body: { text } }),

  result: (competitionId: number, signal?: AbortSignal) =>
    api<AdminResult>(`/api/admin/competitions/${competitionId}/result`, { signal }),
  /** Winner of the submission's own category. confirmPublished: the admin saw that a published result changes at once. */
  selectWinner: (competitionId: number, submissionId: number, confirmPublished = false) =>
    api<AdminResult>(`/api/admin/competitions/${competitionId}/result/winners`, {
      method: 'POST',
      body: { submissionId, confirmPublished },
    }),
  changeWinner: (competitionId: number, submissionId: number, currentWinnerSubmissionId: number, confirmPublished = false) =>
    api<AdminResult>(`/api/admin/competitions/${competitionId}/result/winners`, {
      method: 'PUT',
      body: { submissionId, currentWinnerSubmissionId, confirmPublished },
    }),
  /** All three places at once; `current` is what the screen showed, so a stale screen is refused. */
  setPlaces: (competitionId: number, places: (number | null)[], current: (number | null)[], confirmPublished = false) =>
    api<AdminResult>(`/api/admin/competitions/${competitionId}/result/places`, {
      method: 'PUT',
      body: { places, current, confirmPublished },
    }),
  removeWinner: (competitionId: number, submissionId: number, confirmPublished = false) =>
    api<AdminResult>(`/api/admin/competitions/${competitionId}/result/winners/${submissionId}${qs({ confirmPublished: String(confirmPublished) })}`, {
      method: 'DELETE',
    }),
  uploadResultCover: (competitionId: number, file: File, onProgress: (f: number) => void) => {
    const form = new FormData()
    form.append('cover', file, file.name)
    return apiUpload<AdminResult>(`/api/admin/competitions/${competitionId}/result/cover`, form, onProgress)
  },
  removeResultCover: (competitionId: number) =>
    api<AdminResult>(`/api/admin/competitions/${competitionId}/result/cover`, { method: 'DELETE' }),
  resultAnnouncement: (competitionId: number, announcement: string) =>
    api<AdminResult>(`/api/admin/competitions/${competitionId}/result/announcement`, { method: 'PUT', body: { announcement } }),
  awards: (competitionId: number, signal?: AbortSignal) =>
    api<AdminAwards>(`/api/admin/competitions/${competitionId}/result/awards`, { signal }),
  /** One podium, all three places; `current` is what the screen showed, so a stale screen is refused. */
  setAwards: (
    competitionId: number,
    category: 'jury' | 'public',
    places: AwardPlaceInput[],
    current: (number | null)[],
    source: string | null,
    confirmPublished: boolean,
  ) =>
    api<AdminAwards>(`/api/admin/competitions/${competitionId}/result/awards/${category}`, {
      method: 'PUT',
      body: { places, current, source, confirmPublished },
    }),
  clearAwards: (competitionId: number, category: 'jury' | 'public') =>
    api<AdminAwards>(`/api/admin/competitions/${competitionId}/result/awards/${category}`, { method: 'DELETE' }),
  publishResult: (competitionId: number) =>
    api<AdminResult>(`/api/admin/competitions/${competitionId}/result/publish`, { method: 'POST' }),
  unpublishResult: (competitionId: number) =>
    api<AdminResult>(`/api/admin/competitions/${competitionId}/result/unpublish`, { method: 'POST' }),

  // ---- the jury final (D13) ----
  final: (competitionId: number, signal?: AbortSignal) =>
    api<AdminFinal>(`/api/admin/competitions/${competitionId}/final`, { signal }),
  /** `supersedeReason` is required when a frozen ten exists: the old snapshot goes to the audit with it. */
  freezeFinal: (competitionId: number, prizes: number[], supersedeReason?: string) =>
    api<AdminFinal>(`/api/admin/competitions/${competitionId}/final/freeze`, {
      method: 'POST',
      body: { auditCompleted: true, prizes, supersedeReason },
    }),
  addJuror: (competitionId: number) =>
    api<AdminFinal>(`/api/admin/competitions/${competitionId}/final/jurors`, { method: 'POST' }),
  /** A juror's name on the admin's screens and sheets (never public). Audited. */
  renameJuror: (competitionId: number, jurorId: number, label: string) =>
    api<AdminFinal>(`/api/admin/competitions/${competitionId}/final/jurors/${jurorId}`, { method: 'PUT', body: { label } }),
  removeJuror: (competitionId: number, jurorId: number) =>
    api<AdminFinal>(`/api/admin/competitions/${competitionId}/final/jurors/${jurorId}`, { method: 'DELETE' }),
  saveJuryMarks: (competitionId: number, jurorId: number, marks: JuryMarkInput[]) =>
    api<AdminFinal>(`/api/admin/competitions/${competitionId}/final/marks`, { method: 'PUT', body: { jurorId, marks } }),
  lockJury: (competitionId: number) =>
    api<AdminFinal>(`/api/admin/competitions/${competitionId}/final/lock`, { method: 'POST' }),
  unlockJury: (competitionId: number, reason: string) =>
    api<AdminFinal>(`/api/admin/competitions/${competitionId}/final/unlock`, { method: 'POST', body: { reason } }),
  recordTieBreak: (competitionId: number, submissionIds: number[], reason: string) =>
    api<AdminFinal>(`/api/admin/competitions/${competitionId}/final/tie-breaks`, {
      method: 'POST',
      body: { submissionIds, reason },
    }),
  /** The public vote on paper: the ten from the frozen snapshot, or every work by valid votes. Read-only. */
  leaderboardPrint: (competitionId: number, mode: LeaderboardPrintMode, includeAuthor: boolean, signal?: AbortSignal) =>
    api<LeaderboardPrint>(
      `/api/admin/competitions/${competitionId}/final/print/${mode === 'Top10' ? 'top10' : 'full'}${includeAuthor ? '?includeAuthor=true' : ''}`,
      { signal },
    ),
  finalShow: (competitionId: number, signal?: AbortSignal) =>
    api<FinalShow>(`/api/admin/competitions/${competitionId}/final/show`, { signal }),
  publishFinal: (competitionId: number) =>
    api<AdminFinal>(`/api/admin/competitions/${competitionId}/final/publish`, { method: 'POST', body: { confirm: true } }),
}

// ---- the jury final (D13): mirrors Meydan.Api.Features.Final DTOs ----

export type FinalStage = 'Frozen' | 'JuryLocked' | 'Published'

/** How a finalist's place was reached when it was level with others on every rule. */
export type FinalTie = 'None' | 'SharedPlace' | 'DecidedByJury' | 'NeedsJuryDecision'

export interface FinalCandidate {
  submissionId: number
  reference: string
  title: string | null
  validVotes: number
  rank: number
}

export interface FinalFreezePreview {
  votingClosed: boolean
  resultPublished: boolean
  validVotes: number
  invalidatedVotes: number
  votesFingerprint: string
  top: FinalCandidate[]
  /** The first work outside the ten, for the margin at the line. */
  next: FinalCandidate | null
  boundaryTie: boolean
  problems: string[]
}

export interface FinalJuror {
  id: number
  label: string
  hasMarks: boolean
}

export interface JuryMark {
  jurorId: number
  idea: number | null
  execution: number | null
  relevance: number | null
  subtotal: number
  complete: boolean
}

/** A finalist on the jury's screens: never its votes or its public rank. */
export interface JuryFinalist {
  submissionId: number
  reference: string
  title: string | null
  image: AdminPrintImage | null
  /** The author's name: the admin's screens are internal. Never a vote. */
  author?: string | null
  marks: JuryMark[]
  juryTotal: number
  complete: boolean
}

export interface JuryMarkInput {
  submissionId: number
  idea: number | null
  execution: number | null
  relevance: number | null
}

export interface FinalRow {
  submissionId: number
  reference: string
  title: string | null
  validVotes: number
  publicRank: number
  publicPoints: number
  juryTotal: number
  juryRank: number
  juryPoints: number
  finalScore: number
  /** Competition rank by final score, raw jury score, valid votes: level works share it. */
  rank: number
  /** Null while a tie at a prize waits for the jury. */
  place: number | null
  tie: FinalTie
  prizeAzn: number | null
}

export interface FinalTieGroup {
  rank: number
  works: { submissionId: number; reference: string; title: string | null }[]
}

export interface FinalTieBreak {
  references: string[]
  reason: string
  decidedAt: string
  decidedBy: string | null
}

export interface FinalCalculation {
  jurorCount: number
  juryMax: number
  rows: FinalRow[]
  unresolvedTies: FinalTieGroup[]
  tieBreaks: FinalTieBreak[]
  publishProblems: string[]
}

export interface AdminFinal {
  competitionId: number
  competitionTitle: string
  /** Null while nothing is frozen; `preview` then says what would be. */
  stage: FinalStage | null
  preview: FinalFreezePreview | null
  frozenAt: string | null
  frozenBy: string | null
  frozenValidVotes: number | null
  frozenInvalidatedVotes: number | null
  frozenVotesFingerprint: string | null
  votesChangedSinceFreeze: boolean
  prizes: number[]
  jurors: FinalJuror[]
  /** Reference order, blind to the vote. */
  finalists: JuryFinalist[]
  missingMarks: number
  canRefreeze: boolean
  juryLockedAt: string | null
  juryLockedBy: string | null
  /** Only from the jury's lock on. */
  calculation: FinalCalculation | null
  publishedAt: string | null
  publishedBy: string | null
}

export type LeaderboardPrintMode = 'Top10' | 'Full'

export interface LeaderboardPrintRow {
  /** Competition ranking by valid votes (1, 2, 2, 4): the public rank, not a prize place. */
  rank: number
  validVotes: number
  /** The final's points for the rank (Top 10 only). */
  publicPoints: number | null
  /** The work as the printed cards show it: picture, reference, title, nomination, author when asked for. */
  item: AdminPrintItem
}

/** The public vote on paper. No voter exists in this shape at all; the author only when asked for. */
export interface LeaderboardPrint {
  mode: LeaderboardPrintMode
  competitionTitle: string
  /** When the snapshot was frozen (Top 10); null for the full ranking, read as it stands. */
  frozenAt: string | null
  validVotes: number
  invalidatedVotes: number
  totalVotes: number
  votesFingerprint: string
  rows: LeaderboardPrintRow[]
  generatedAt: string
}

export interface FinalShowWork {
  submissionId: number
  reference: string
  title: string | null
  image: AdminPrintImage | null
  validVotes: number
  publicRank: number
  publicPoints: number
  juryTotal: number
  juryRank: number
  juryPoints: number
  finalScore: number
  rank: number
  place: number | null
  tie: FinalTie
  prizeAzn: number | null
}

export interface FinalShow {
  competitionTitle: string
  jurorCount: number
  juryMax: number
  /** Reference order; the show builds each reveal's order from the numbers. */
  works: FinalShowWork[]
  /** False while a tie at a prize waits for the jury: the top three stay closed. */
  prizesDecided: boolean
  prizes: number[]
}
