// Mirrors the backend DTOs (Meydan.Api.Features.*). Enums arrive as strings.

/** What an admin decided. */
export type ProjectStatus = 'Draft' | 'Published' | 'Archived'
export type CompetitionStatus = 'Draft' | 'Published' | 'Closed' | 'Archived'
/** Computed by the server from status + dates: whether work is accepted right now. */
export type CompetitionPhase = 'Upcoming' | 'Open' | 'Closed'
export type SubmissionStatus = 'Draft' | 'Submitted' | 'UnderReview' | 'Approved' | 'Rejected' | 'Winner' | 'Withdrawn'

export interface ProjectRef {
  slug: string
  name: string
  accentColor: string | null
}

export interface ProjectCard {
  id: number
  slug: string
  name: string
  shortDescription: string
  coverImage: string | null
  accentColor: string | null
  status: ProjectStatus
  sortOrder: number
  openCompetitionCount: number
  competitionCount: number
}

/** The platform's own public words, edited from the admin (see PlatformProvider for the fallbacks). */
export interface PlatformSettings {
  platformName: string
  homeHeroTitle: string
  homeHeroSubtitle: string
  authPromoTitle: string
  authPromoImage: string | null
}

export interface CompetitionSummary {
  id: number
  slug: string
  title: string
  shortDescription: string
  coverImage: string | null
  status: CompetitionStatus
  phase: CompetitionPhase
  startDate: string | null
  endDate: string | null
  hasPrize: boolean
  hasCategories: boolean
  categoryLabel: string | null
  categoryCount: number
  project: ProjectRef
  /** Results are public (published and the competition no longer accepts work). */
  resultsPublished: boolean
  /** Where the public vote stands; `NotConfigured` — the usual case — means this competition has no vote. */
  voting: VotingState
}

export interface ProjectDetail {
  id: number
  slug: string
  name: string
  shortDescription: string
  description: string | null
  coverImage: string | null
  accentColor: string | null
  status: ProjectStatus
  sortOrder: number
  competitions: CompetitionSummary[]
}

export interface Category {
  id: number
  slug: string
  name: string
  description: string | null
  image: string | null
}

export interface CompetitionDetail {
  id: number
  slug: string
  title: string
  shortDescription: string
  description: string | null
  rules: string | null
  requirements: string | null
  /** One block per nomination: its first line is the name, the rest explains it; blocks split by a blank line. */
  nominations: string | null
  /** What the entries are judged on, and what happens to an author's work; null while unwritten. */
  judgingCriteria: string | null
  usageTerms: string | null
  coverImage: string | null
  status: CompetitionStatus
  phase: CompetitionPhase
  isAcceptingSubmissions: boolean
  startDate: string | null
  endDate: string | null
  /** Announcement and exhibition days; null while undecided, and then not shown at all. */
  resultsAnnouncedOn: string | null
  exhibitionOn: string | null
  /** Optional line for questions; entries always come through the site itself. */
  contactNote: string | null
  prize: string | null
  hasCategories: boolean
  categoryLabel: string | null
  maxSubmissionsPerUser: number | null
  categories: Category[]
  project: ProjectRef
  /** Set only once results are public; the winner itself comes from the results endpoint. */
  resultsPublishedAt: string | null
  /** Where the public vote stands; `NotConfigured` — the usual case — means this competition has no vote. */
  voting: VotingState
}

export interface HomeData {
  projects: ProjectCard[]
  openCompetitions: CompetitionSummary[]
  /** Competitions whose public vote is open — usually after their entries have closed. */
  votingCompetitions: CompetitionSummary[]
}

export interface User {
  id: string
  firstName: string
  lastName: string
  email: string
  phone: string | null
  roles: string[]
  emailConfirmed?: boolean
}

export interface AuthResponse {
  accessToken: string
  accessTokenExpiresAt: string
  user: User
}

export interface MySubmission {
  id: number
  referenceNumber: string | null
  title: string
  status: SubmissionStatus
  isEditable: boolean
  submittedAt: string | null
  createdAt: string
  updatedAt: string
  projectName: string
  projectSlug: string
  competitionTitle: string
  competitionSlug: string
  categoryLabel: string | null
  categoryName: string | null
  /** The competition's results are public; only then is isWinner meaningful. */
  resultsPublished: boolean
  isWinner: boolean
  /** 1, 2 or 3 when the work holds a place of a competition without categories. */
  place: number | null
}

// ---- Results & gallery (public after publication only) --------------------------------------------

export interface GalleryCategory {
  slug: string
  name: string
}

/**
 * A public-safe picture of a work: `url` is the server-made copy (longest side 1600 px), `thumbUrl` the card-size copy
 * for grids. Width/height are the copy's pixel size.
 */
export interface PublicImage {
  url: string
  thumbUrl: string
  width: number | null
  height: number | null
}

/** Ready: an image exists; Processing: still being made; Unavailable: could not be made; None: no public file. */
export type PreviewState = 'Ready' | 'Processing' | 'Unavailable' | 'None'

export interface GalleryItem {
  reference: string
  /** Null when the title field is not public. */
  title: string | null
  category: GalleryCategory | null
  isWinner: boolean
  /** Set only for winners of a published result — everybody else stays anonymous. */
  winnerName: string | null
  preview: PublicImage | null
  previewState: PreviewState
  /** 1, 2 or 3 for a placed work of a competition without categories; null everywhere else. */
  place: number | null
  /** The podium places this work holds in a two-choice result; absent or null when none. */
  awards?: ItemAward[] | null
}

/** Which of the two independent choices of a result. */
export type AwardCategory = 'Jury' | 'Public'

/** A podium place a work holds: the choice, the place and its number (the jury's points or the valid votes). */
export interface ItemAward {
  category: AwardCategory
  place: number
  points: number
}

/** One place of a podium: its number, the name the result announced, and the work. */
export interface PublicAward {
  place: number
  points: number
  participantName: string
  work: GalleryItem
}

/** The jury's choice and the public's choice, each in place order — never combined, no overall winner. */
export interface PublicAwards {
  jury: PublicAward[]
  public: PublicAward[]
}

export interface GalleryFile {
  id: number
  /**
   * 'image' = re-encoded copy of an uploaded image, 'document-preview' = first page of a PDF, 'document-page' = any
   * later page of it. Every one is a server-made picture; the PDF itself is never public.
   */
  kind: 'image' | 'document-preview' | 'document-page'
  url: string
  thumbUrl: string
  width: number | null
  height: number | null
  name: string
  /** Which page of the document, for 'document-page' (2, 3, …). */
  page?: number | null
}

export interface GalleryWork extends GalleryItem {
  answers: { label: string; fieldType: FieldType; value: string }[]
  files: GalleryFile[]
}

export interface ResultCompetition {
  slug: string
  title: string
  shortDescription: string
  categoryLabel: string | null
  hasCategories: boolean
  project: ProjectRef
}

/** A category (settlement) on the results page: how many works it received and its winner, if chosen. */
export interface ResultCategory {
  slug: string
  name: string
  entryCount: number
  winner: GalleryItem | null
}

export interface CompetitionResult {
  competition: ResultCompetition
  publishedAt: string
  announcement: string | null
  /** The admin's results banner; never a submission's image. */
  coverImage: string | null
  entryCount: number
  winnerCount: number
  /** Every category in order, with or without a winner; empty for competitions without categories. */
  categories: ResultCategory[]
  /** The single winner of a competition without categories — its first place. */
  overallWinner: GalleryItem | null
  /** First, second and third place of a competition without categories, in order; only those awarded. */
  places: GalleryItem[]
  /** How each place was reached, when the places came from a jury final (D13); absent otherwise. */
  final?: PublicFinal | null
  /** The two independent choices of a two-choice result; absent or null otherwise. */
  awards?: PublicAwards | null
}

/** The jury final behind a result: jurors are counted, never named. */
export interface PublicFinal {
  jurorCount: number
  /** The most the jury could give together (jurors × 30). */
  juryMax: number
  places: PublicFinalPlace[]
}

/** One placed work's numbers, as announced: the public side, the jury side, and the sum. */
export interface PublicFinalPlace {
  place: number
  reference: string
  /** Valid (audited) votes. */
  validVotes: number
  publicRank: number
  publicPoints: number
  juryTotal: number
  juryRank: number
  juryPoints: number
  finalScore: number
}

export type ResultCoverKind = 'Result' | 'Competition' | 'Project'

export interface ResultListItem {
  competition: ResultCompetition
  publishedAt: string
  coverImage: string | null
  coverKind: ResultCoverKind | null
  categoryCount: number
  winnerCount: number
  entryCount: number
}

export interface Gallery {
  competition: ResultCompetition
  publishedAt: string
  categories: (GalleryCategory & { count: number })[]
  items: GalleryItem[]
  total: number
  winnerCount: number
}

export type FieldType = 'ShortText' | 'LongText' | 'Number' | 'Url' | 'Select' | 'Checkbox' | 'Date' | 'File'

// ---- The public vote -------------------------------------------------------------------------
//
// Shapes of /api/competitions/{slug}/voting. The server decides every one of these; the browser never works out
// from dates whether voting is open, nor from counts whether a button should be pressable.

/** Where the vote stands. `NotConfigured` never reaches the browser: the API answers 404 instead. */
export type VotingState = 'NotConfigured' | 'Upcoming' | 'Open' | 'Closed'

/** Why a work's button says what it says — one value per piece of copy, never a rule the browser re-derives. */
export type VoteEligibility =
  | 'Allowed'
  | 'NotSignedIn'
  | 'VotingNotOpen'
  | 'AlreadyVotedForThisWork'
  | 'AlreadyVotedInNomination'
  | 'OwnWork'
  /** An account opened after e-mail confirmation became required, not confirmed yet. */
  | 'EmailNotConfirmed'

export interface VotingNomination {
  slug: string
  name: string
  workCount: number
  voteCount: number
}

/** What this viewer may do, for the page as a whole. Null when nobody is signed in. */
export interface VotingViewer {
  mayVote: boolean
  reason: VoteEligibility
  /** Nominations this viewer has already voted in. */
  votedNominations: string[]
  /** The works those votes went to. */
  votedWorks: string[]
}

export interface VotingWork {
  reference: string
  /** Null when the title field is not public. */
  title: string | null
  nomination: GalleryCategory
  preview: PublicImage | null
  previewState: PreviewState
  voteCount: number
  eligibility: VoteEligibility
  /**
   * A few lines of the work's own description, for the card — one public field, shortened by the server.
   * Null when that field is not public or was left empty. The full text is on the work's own page.
   */
  publicConceptDescription: string | null
}

export interface Voting {
  competition: ResultCompetition
  state: VotingState
  startsAt: string | null
  endsAt: string | null
  nominations: VotingNomination[]
  workCount: number
  voteCount: number
  viewer: VotingViewer | null
  /** How many people have voted — a number, never a name. */
  voterCount: number
  /** When the latest vote was cast; null before the first. */
  lastVoteAt: string | null
  /**
   * The official result is public (published by an admin, by the results pages' own rule). A closed vote without
   * it is in review. Optional only so that an answer from a server older than the field reads as "not yet".
   */
  resultsPublished?: boolean
  /** The server's clock when it answered — see lib/serverClock. */
  serverNow?: string
}

/** Why votes were set aside, in public words — the rule, never anybody's own text. */
export type VotingAuditReason = 'DuplicatePhone' | 'CoordinatedAccounts' | 'DisposableEmail'

/** One admin decision as the public sees it. */
export interface VotingAuditEntry {
  at: string
  votes: number
  reason: VotingAuditReason
}

/** The public audit of a vote: real totals from the votes themselves, and one line per decision. */
export interface VotingAudit {
  state: VotingState
  validVotes: number
  invalidatedVotes: number
  totalVotes: number
  /** Newest first. */
  entries: VotingAuditEntry[]
  updatedAt: string
  /** Per rule, the votes set aside under it, all decisions together (rules with none left out). */
  rules?: { reason: VotingAuditReason; votes: number }[]
}

export interface VotingWorks {
  state: VotingState
  items: VotingWork[]
  total: number
  page: number
  pageSize: number
  viewer: VotingViewer | null
}

export interface VotingWorkDetail {
  work: VotingWork
  /** Answers of fields an admin marked public — the same switch the gallery uses. */
  answers: { label: string; fieldType: FieldType; value: string }[]
  files: GalleryFile[]
  state: VotingState
  endsAt: string | null
  viewer: VotingViewer | null
}

/** What a cast vote answers with. A vote is final: there is nothing that undoes one. */
export interface VoteReceipt {
  reference: string
  nominationSlug: string
  voteCount: number
  castAt: string
}

/** A work's place in the public vote. Works with the same count share a rank (1, 2, 2, 4). */
export interface VotingLeaderboardEntry {
  rank: number
  reference: string
  /** Null when the title field is not public. */
  title: string | null
  voteCount: number
  preview: PublicImage | null
}

/** The live ranking of the public vote — votes only, never the places an admin awards. */
export interface VotingLeaderboard {
  state: VotingState
  updatedAt: string
  totalVotes: number
  entries: VotingLeaderboardEntry[]
  /** The work this viewer voted for; null when signed out or not voted yet. */
  myVoteReference: string | null
}

export interface AcceptedFileType {
  key: string
  label: string
  mimeType: string
  extensions: string[]
}

export interface FileRules {
  maxFiles: number
  maxFileSizeBytes: number
  accept: AcceptedFileType[]
}

export interface FormField {
  id: number
  key: string
  label: string
  fieldType: FieldType
  placeholder: string | null
  helpText: string | null
  isRequired: boolean
  sortOrder: number
  options: string[] | null
  maxLength: number | null
  files: FileRules | null
}

export interface SubmissionFile {
  id: number
  fieldKey: string
  originalFileName: string
  contentType: string
  sizeBytes: number
  uploadedAt: string
}

export interface Submission {
  id: number
  status: SubmissionStatus
  referenceNumber: string | null
  title: string
  isEditable: boolean
  createdAt: string
  updatedAt: string
  submittedAt: string | null
  project: ProjectRef
  competition: {
    slug: string
    title: string
    phase: CompetitionPhase
    isAcceptingSubmissions: boolean
    hasCategories: boolean
    categoryLabel: string | null
  }
  category: { slug: string; name: string } | null
  fields: FormField[]
  answers: Record<string, string | null>
  files: SubmissionFile[]
}

/** The contact form: one message to the organiser, by e-mail. */
export interface ContactPayload {
  name: string
  email: string
  message: string
  captchaToken?: string
}

export interface RegisterPayload {
  firstName: string
  lastName: string
  phone: string
  email: string
  password: string
  confirmPassword: string
  acceptTerms: boolean
  /** The robot check's token, when the server asks for one (PublicConfig.captchaSiteKey). */
  captchaToken?: string
}

export interface LoginPayload {
  email: string
  password: string
  rememberMe: boolean
}

export interface ProfilePayload {
  firstName: string
  lastName: string
  phone: string
}

/** Which doors this deployment has open; nothing secret (GET /api/config). */
export interface PublicConfig {
  qrlogLogin: boolean
  passwordReset: boolean
  /** Registration asks for the robot check (Cloudflare Turnstile) with this public key; null or absent when it does not. */
  captchaSiteKey?: string | null
  /** "email-first": registration starts with the address and a link; "classic" (or absent): one form. */
  registrationFlow?: 'email-first' | 'classic'
}

/** Email-first registration: the link was good; the completion form may be sent with `proof` until `expiresAt`. */
export interface RegistrationVerified {
  proof: string
  email: string
  expiresAt: string
}

export interface RegistrationCompletePayload {
  proof: string
  firstName: string
  lastName: string
  phone: string
  password: string
  confirmPassword: string
  acceptTerms: boolean
}

export interface QrlogStart {
  /** This device goes to QRLog and comes back (a phone with QRLog on it). */
  authorizeUrl: string
  /** What a desktop's QR shows: QRLog's approval page with the ticket code only — no way back, no secret. */
  scanUrl: string
  expiresAt: string
}

export type QrlogClaimStatus = 'pending' | 'signed-in' | 'linked' | 'email-taken' | 'gone'

export interface QrlogClaim {
  status: QrlogClaimStatus
  returnTo: string
  session?: AuthResponse | null
  email?: string | null
}

/** The sign-in methods attached to the current account. */
export interface LoginsInfo {
  hasPassword: boolean
  qrlog: boolean
}
