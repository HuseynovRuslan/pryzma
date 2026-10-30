import { ApiError } from '../api/client'

export type FieldErrors = Record<string, string | undefined>

/** Turns an API failure into a form-level message plus per-field messages. */
export function readErrors(
  err: unknown,
  fallback: string,
): { message: string; fields: FieldErrors; lists: Record<string, string[]> } {
  if (!(err instanceof ApiError)) return { message: fallback, fields: {}, lists: {} }
  const fields: FieldErrors = {}
  for (const [key, messages] of Object.entries(err.fieldErrors)) fields[key] = messages.join(' ')
  const hasFields = Object.keys(fields).length > 0
  return { message: hasFields ? 'Qeyd olunan sahələri düzəldin.' : err.message, fields, lists: err.fieldErrors }
}
