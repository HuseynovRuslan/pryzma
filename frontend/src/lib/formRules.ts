import type { FileRules, FormField } from '../api/types'
import { formatBytes } from './format'

// Mirrors Meydan.Api.Features.Submissions.FieldSchema so most mistakes surface before a round trip.
// The server stays the source of truth and re-checks everything.

export type Answers = Record<string, string | null>
export type FieldErrorMap = Record<string, string | undefined>

export function isEmpty(value: string | null | undefined): boolean {
  return value == null || value.trim() === ''
}

/** Shape check for one value; empty values pass (drafts may be incomplete). */
export function shapeError(field: FormField, raw: string | null | undefined): string | undefined {
  const value = raw?.trim() ?? ''
  if (value === '') return undefined
  switch (field.fieldType) {
    case 'ShortText':
    case 'LongText':
      return field.maxLength && value.length > field.maxLength
        ? `“${field.label}” ən çox ${field.maxLength} simvol ola bilər.`
        : undefined
    case 'Number':
      return Number.isFinite(Number(value.replace(',', '.'))) ? undefined : `“${field.label}” üçün rəqəm yazın.`
    case 'Date':
      return /^\d{4}-\d{2}-\d{2}$/.test(value) ? undefined : `“${field.label}” üçün düzgün tarix seçin.`
    case 'Url':
      return /^https?:\/\/\S+$/i.test(value) ? undefined : `“${field.label}” üçün tam link yazın (https://…).`
    case 'Select':
      return field.options?.includes(value) ? undefined : `“${field.label}” üçün siyahıdan birini seçin.`
    default:
      return undefined
  }
}

export function requiredError(field: FormField, value: string | null | undefined, fileCount: number): string | undefined {
  if (!field.isRequired) return undefined
  if (field.fieldType === 'File') return fileCount === 0 ? `“${field.label}” üçün ən azı bir fayl əlavə edin.` : undefined
  if (field.fieldType === 'Checkbox') return value === 'true' ? undefined : `Davam etmək üçün “${field.label}” qeyd olunmalıdır.`
  return isEmpty(value) ? `“${field.label}” sahəsini doldurun.` : undefined
}

/** Full check of the given fields, as the server does at final submission. */
export function validateFields(fields: FormField[], answers: Answers, fileCounts: Record<string, number>): FieldErrorMap {
  const errors: FieldErrorMap = {}
  for (const f of fields) {
    const message = requiredError(f, answers[f.key], fileCounts[f.key] ?? 0) ?? shapeError(f, answers[f.key])
    if (message) errors[f.key] = message
  }
  return errors
}

export function hasAny(errors: FieldErrorMap): boolean {
  return Object.values(errors).some(Boolean)
}

/** "PNG, JPG, WEBP və ya PDF. Hər fayl ən çox 10 MB, cəmi 5 fayl." — built from the rules, never hardcoded. */
export function describeFileRules(rules: FileRules): string {
  const labels = rules.accept.map((t) => t.label)
  const formats = labels.length > 1 ? `${labels.slice(0, -1).join(', ')} və ya ${labels[labels.length - 1]}` : (labels[0] ?? '')
  return `${formats}. Hər fayl ən çox ${formatBytes(rules.maxFileSizeBytes)}, cəmi ${rules.maxFiles} fayl.`
}

export function acceptAttribute(rules: FileRules): string {
  return [...rules.accept.flatMap((t) => t.extensions), ...rules.accept.map((t) => t.mimeType)].join(',')
}

/** Client-side pre-check of one picked file; returns the reason it cannot be uploaded, if any. */
export function fileError(file: File, rules: FileRules, alreadyAttached: number): string | undefined {
  const dot = file.name.lastIndexOf('.')
  const ext = dot >= 0 ? file.name.slice(dot).toLowerCase() : ''
  const allowed = rules.accept.some((t) => t.extensions.includes(ext))
  if (!allowed) {
    const labels = rules.accept.map((t) => t.label).join(', ')
    return `Bu format qəbul olunmur. İcazə verilən formatlar: ${labels}.`
  }
  if (file.size === 0) return 'Fayl boşdur.'
  if (file.size > rules.maxFileSizeBytes)
    return `Fayl çox böyükdür (${formatBytes(file.size)}). Hər fayl ən çox ${formatBytes(rules.maxFileSizeBytes)} ola bilər.`
  if (alreadyAttached >= rules.maxFiles) return `Ən çox ${rules.maxFiles} fayl əlavə etmək olar.`
  return undefined
}
