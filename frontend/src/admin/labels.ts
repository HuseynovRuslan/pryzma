import type { CompetitionPhase, CompetitionStatus, FieldType, ProjectStatus, SubmissionStatus } from '../api/types'

// Admin vocabulary. Participant-facing wording lives in lib/format.ts and is deliberately softer.

export const projectStatusAdmin: Record<ProjectStatus, string> = {
  Draft: 'Qaralama',
  Published: 'Dərc olunub',
  Archived: 'Arxivdə',
}

export const competitionStatusAdmin: Record<CompetitionStatus, string> = {
  Draft: 'Qaralama',
  Published: 'Dərc olunub',
  Closed: 'Bağlanıb',
  Archived: 'Arxivdə',
}

export const phaseAdmin: Record<CompetitionPhase, string> = {
  Open: 'Qəbul açıqdır',
  Upcoming: 'Qəbul hələ başlamayıb',
  Closed: 'Qəbul bağlıdır',
}

export const submissionStatusAdmin: Record<SubmissionStatus, string> = {
  Draft: 'Qaralama',
  Submitted: 'Göndərilib',
  UnderReview: 'Yoxlanılır',
  Approved: 'Təsdiqlənib',
  Rejected: 'Rədd edilib',
  Winner: 'Qalib',
  Withdrawn: 'Geri çəkilib',
}

export const fieldTypeLabel: Record<FieldType, string> = {
  ShortText: 'Qısa mətn',
  LongText: 'Uzun mətn',
  File: 'Fayl',
  Select: 'Seçim',
  Checkbox: 'Təsdiq qutusu',
  Date: 'Tarix',
  Number: 'Rəqəm',
  Url: 'Link',
}

/** Field types offered in the form builder, in the order they are most often needed. */
export const fieldTypes: FieldType[] = ['ShortText', 'LongText', 'File', 'Select', 'Checkbox', 'Date', 'Number', 'Url']

export const auditActionLabel: Record<string, string> = {
  Created: 'Yaradıldı',
  Updated: 'Yeniləndi',
  Deleted: 'Silindi',
  Published: 'Dərc olundu',
  Unpublished: 'Dərcdən çıxarıldı',
  Closed: 'Bağlandı',
  Archived: 'Arxivləşdirildi',
  CoverChanged: 'Örtük dəyişdi',
  CategoriesImported: 'Kateqoriyalar idxal olundu',
  CategoriesReordered: 'Kateqoriyaların sırası dəyişdi',
  FieldsReordered: 'Sahələrin sırası dəyişdi',
  StatusChanged: 'Status dəyişdi',
  NoteAdded: 'Qeyd əlavə olundu',
  WinnerSelected: 'Qalib seçildi',
  WinnerChanged: 'Qalib dəyişdirildi',
  AwardsSet: 'Nəticə podiumu qeyd olundu',
  AwardsCleared: 'Nəticə podiumu silindi',
  ResultsPublished: 'Nəticə dərc olundu',
  ResultsUnpublished: 'Nəticə dərcdən çıxarıldı',
  ResultUpdated: 'Nəticə mətni yeniləndi',
  FileHidden: 'Fayl saytda gizlədildi',
  FileShown: 'Fayl saytda yenidən göstərildi',
  // The jury's evaluation of every work (InternalJuryRankingV1)
  JuryEvaluationCreated: 'Münsif qiymətləndirməsi yaradıldı',
  JuryRosterFrozen: 'İşlərin siyahısı donduruldu',
  JuryJurorAssigned: 'Münsif əlavə olundu',
  JuryJurorUnassigned: 'Münsif çıxarıldı',
  JuryInvitationIssued: 'Dəvət verildi',
  JuryInvitationRevoked: 'Dəvət ləğv olundu',
  JurorSignedIn: 'Münsif daxil oldu',
  JurorSignInFailed: 'Səhv giriş kodu',
  JurorIdentityConfirmed: 'Münsif adını təsdiqlədi',
  JurorSignedOut: 'Münsif çıxdı',
  JuryWorkScored: 'Bal verildi',
  JuryPaperScoresEntered: 'Kağızdan ballar daxil edildi',
  JuryEvaluationSubmitted: 'Qiymətləndirmə yekunlaşdırıldı',
  JuryEvaluationReopened: 'Qiymətləndirmə yenidən açıldı',
  FinalJurorRenamed: 'Münsifin adı dəyişdi',
}
