/**
 * The submissions print view's plain helpers, apart from the page itself so the list can link to it without
 * loading it (admin/pages/SubmissionsPrintPage).
 */

/** The list's filters the print view carries over. Not the page number: a print takes the whole selection. */
export const PRINT_FILTER_KEYS = ['projectId', 'competitionId', 'categoryId', 'status', 'winner', 'demo', 'from', 'to', 'q'] as const

export type PrintLayout = 1 | 4

/** The jury's three criteria, each marked of 10, and summed of 30 (D13). */
export const CRITERIA = ['İdeya', 'Vizual / icra', 'Mövzuya uyğunluq'] as const

/**
 * What the jury is asked, at the top of the scoring screen and of every jury sheet (owner, 7 October 2026): the
 * provisional public ranking stays online, so the jury is told in so many words to judge the work, not the crowd.
 */
export const BLIND_INSTRUCTION = 'Qiymətləndirmə zamanı xalq səsverməsinin nəticələrinə baxmadan işin özünü dəyərləndirin.'

/** Every work of the competition that can still win, four to a page, for management's review. */
export const allWorksPrintHref = (competitionId: number) => `/admin/submissions/print?competitionId=${competitionId}`

/** The jury's package: the competition's frozen finalists, four to a page, then their whole material. */
export const finalistsPrintHref = (competitionId: number) =>
  `/admin/submissions/print?competitionId=${competitionId}&finalists=1`

/** The print view of the list's current selection. */
export function printHref(params: URLSearchParams): string {
  const next = new URLSearchParams()
  for (const k of PRINT_FILTER_KEYS) {
    const v = params.get(k)
    if (v) next.set(k, v)
  }
  const s = next.toString()
  return `/admin/submissions/print${s ? `?${s}` : ''}`
}

/** A card's description: enough to recall the work, never so much that four stop fitting on a page. */
export const EXCERPT = 240

/** The description in one paragraph, cut at a word near `max` characters, with an ellipsis when cut. */
export function excerpt(text: string, max = EXCERPT): string {
  const flat = text.replace(/\s+/g, ' ').trim()
  if (flat.length <= max) return flat
  const cut = flat.slice(0, max)
  const space = cut.lastIndexOf(' ')
  return `${(space > max * 0.6 ? cut.slice(0, space) : cut).replace(/[\s,.;:–—-]+$/u, '')}…`
}

/** Splits a list into pages of `size`. */
export const chunk = <T>(list: T[], size: number): T[][] =>
  Array.from({ length: Math.ceil(list.length / size) }, (_, i) => list.slice(i * size, i * size + size))

/** The public vote on paper — a document apart from the jury's sheets: the frozen Top 10, or the full ranking. */
export const leaderboardPrintHref = (competitionId: number, mode: 'top10' | 'full') =>
  `/admin/final-print/${competitionId}?mode=${mode}`

/**
 * Rows of the full ranking on one portrait A4 sheet, under its head and above its foot — with room left for a few
 * titles that run to a second line (titles wrap rather than being cut).
 */
export const FULL_ROWS_PER_PAGE = 32
