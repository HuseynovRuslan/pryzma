import type { ImportItem } from './api'

const BAKU_OFFSET_MIN = 4 * 60

/** ISO instant → value for <input type="datetime-local"> in Baku time ("2026-12-31T23:59"). */
export function toBakuInput(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  const baku = new Date(d.getTime() + BAKU_OFFSET_MIN * 60_000)
  return baku.toISOString().slice(0, 16)
}

/** <input type="datetime-local"> value (Baku wall time) → ISO with explicit +04:00, or null when empty. */
export function fromBakuInput(value: string): string | null {
  if (!value) return null
  return `${value.length === 16 ? `${value}:00` : value}+04:00`
}

/** Baku calendar day ("2026-09-18") → start/end instants for date-range filters. */
export function bakuDayStart(day: string): string {
  return `${day}T00:00:00+04:00`
}
export function bakuDayEnd(day: string): string {
  return `${day}T23:59:59+04:00`
}

export interface ParsedImport {
  items: ImportItem[]
  /** Problems found before sending, keyed by 0-based line index of `items`. */
  problems: Record<number, string>
}

/**
 * One category per line: "Ad", "Ad; slug" or "Ad; slug; açıqlama". A tab works as the separator too, so a
 * column copied from a spreadsheet can be pasted directly. Blank lines are ignored. Duplicates inside the
 * pasted list are flagged here; clashes with existing categories are reported by the server.
 */
export function parseCategoryImport(text: string): ParsedImport {
  const items: ImportItem[] = []
  const problems: Record<number, string> = {}
  const seen = new Map<string, number>()
  for (const raw of text.split(/\r?\n/)) {
    if (!raw.trim()) continue
    const [name = '', slug = '', ...rest] = raw.split(/\t|;/).map((p) => p.trim())
    const item: ImportItem = { name }
    if (slug) item.slug = slug
    const description = rest.join('; ').trim()
    if (description) item.description = description
    const index = items.push(item) - 1
    if (!name) {
      problems[index] = 'Ad boşdur.'
      continue
    }
    const key = name.toLocaleLowerCase('az')
    if (seen.has(key)) problems[index] = `“${name}” siyahıda iki dəfə var (${seen.get(key)! + 1}-ci sətir).`
    else seen.set(key, index)
  }
  return { items, problems }
}

/** Moves an element one step up (-1) or down (+1); returns a new array. */
export function move<T>(list: T[], index: number, delta: -1 | 1): T[] {
  const target = index + delta
  if (target < 0 || target >= list.length) return list
  const copy = [...list]
  ;[copy[index], copy[target]] = [copy[target], copy[index]]
  return copy
}
