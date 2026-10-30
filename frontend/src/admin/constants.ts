import type { SubmissionStatus } from '../api/types'

/** Statuses an admin works with in Phase 3 (Winner is reserved for the future jury phase). */
export const AdminModerationStatuses: SubmissionStatus[] = ['Submitted', 'UnderReview', 'Approved', 'Rejected']
