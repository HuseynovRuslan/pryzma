import type { CompetitionInput, ProjectInput } from './api'
import type { FieldErrors } from './errors'

const HEX = /^#[0-9a-f]{6}$/i

// Client-side checks for quick feedback; the admin API validates everything again.

export function validateProject(v: ProjectInput): FieldErrors {
  return {
    name: v.name.trim() ? undefined : 'Layihənin adını yazın.',
    shortDescription: v.shortDescription.trim() ? undefined : 'Qısa təsvir yazın.',
    accentColor: !v.accentColor || HEX.test(v.accentColor) ? undefined : 'Rəngi #RRGGBB formatında yazın.',
  }
}

export function validateCompetition(v: CompetitionInput): FieldErrors {
  const errors: FieldErrors = {
    projectId: v.projectId > 0 ? undefined : 'Layihəni seçin.',
    title: v.title.trim() ? undefined : 'Müsabiqənin adını yazın.',
    shortDescription: v.shortDescription.trim() ? undefined : 'Qısa təsvir yazın.',
    maxSubmissionsPerUser:
      v.maxSubmissionsPerUser === null || (v.maxSubmissionsPerUser >= 1 && v.maxSubmissionsPerUser <= 1000)
        ? undefined
        : 'Limit 1 ilə 1000 arasında olmalıdır.',
  }
  if (v.startDate && v.endDate && new Date(v.endDate) < new Date(v.startDate))
    errors.endDate = 'Bitmə tarixi başlama tarixindən əvvəl ola bilməz.'
  // The voting window, as the server checks it: a half-set window is not a window, and it has to run forwards.
  if (!v.votingStartsAt !== !v.votingEndsAt)
    errors[v.votingStartsAt ? 'votingEndsAt' : 'votingStartsAt'] =
      'Səsvermənin həm başlama, həm də bitmə tarixini yazın (və ya hər ikisini boş buraxın).'
  else if (v.votingStartsAt && v.votingEndsAt && new Date(v.votingEndsAt) <= new Date(v.votingStartsAt))
    errors.votingEndsAt = 'Səsvermənin bitmə tarixi başlama tarixindən sonra olmalıdır.'
  return errors
}

export const hasErrors = (errors: FieldErrors) => Object.values(errors).some(Boolean)
