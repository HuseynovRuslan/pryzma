const AZ_TO_LATIN: Record<string, string> = { ə: 'e', ı: 'i', ş: 's', ç: 'c', ğ: 'g', ö: 'o', ü: 'u' }

/** Case- and diacritic-insensitive form: "Maştağa" → "mastaga", "İÇƏRİŞƏHƏR" → "iceriseher". */
export function normaliseAz(text: string): string {
  return text
    .toLocaleLowerCase('az')
    .replace(/[əışçğöü]/g, (ch) => AZ_TO_LATIN[ch])
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
}

/**
 * Search key tolerant of how people actually type Azerbaijani on a Latin keyboard:
 * diacritics dropped (mastaga → Maştağa), English digraphs folded (pirshagi → Pirşağı, bakikhanov → Bakıxanov,
 * chilov → Çilov) and spaces/punctuation ignored (resulzade → M.Ə. Rəsulzadə, 28may → 28 May).
 */
export function searchKey(text: string): string {
  return normaliseAz(text)
    .replace(/sh/g, 's')
    .replace(/ch/g, 'c')
    .replace(/gh/g, 'g')
    .replace(/kh/g, 'x')
    .replace(/[^a-z0-9]/g, '')
}

export function matchesSearch(name: string, query: string): boolean {
  const q = searchKey(query)
  return q.length === 0 || searchKey(name).includes(q)
}

/**
 * Singular of an Azerbaijani plural label for "one of them" captions: Qəsəbələr → Qəsəbə, Kəndlər → Kənd,
 * Janrlar → Janr. The plural suffix -lar/-lər is regular; anything else is returned unchanged.
 */
export function singularAz(label: string): string {
  const m = /^(.{2,}?)(lar|lər)$/iu.exec(label.trim())
  return m ? m[1] : label
}

// ---- Case endings (vowel harmony) -------------------------------------------------------------------

const BACK = /[aıou]/
const VOWELS = /[aıoueəiöü]/

/** The last vowel of a word decides its suffixes (vowel harmony); words without vowels count as front. */
function lastVowel(word: string): string {
  const w = word.toLocaleLowerCase('az')
  for (let i = w.length - 1; i >= 0; i--) if (VOWELS.test(w[i])) return w[i]
  return 'e'
}

const endsInVowel = (word: string) => VOWELS.test(word.toLocaleLowerCase('az').slice(-1))

/** "from": qəsəbə → qəsəbədən, kateqoriya → kateqoriyadan, kənd → kənddən. */
export const azAblative = (word: string) => word + (BACK.test(lastVowel(word)) ? 'dan' : 'dən')

/** "in": qəsəbə → qəsəbədə, kateqoriya → kateqoriyada. */
export const azLocative = (word: string) => word + (BACK.test(lastVowel(word)) ? 'da' : 'də')

/** "of": qəsəbə → qəsəbənin, kateqoriya → kateqoriyanın, kənd → kəndin, nominasiya → nominasiyanın. */
export function azGenitive(word: string): string {
  const v = lastVowel(word)
  const harmony = 'aı'.includes(v) ? 'ın' : 'ou'.includes(v) ? 'un' : 'öü'.includes(v) ? 'ün' : 'in'
  return word + (endsInVowel(word) ? 'n' : '') + harmony
}

/** What a verb acts on: nominasiyalar → nominasiyaları, qəsəbələr → qəsəbələri, kateqoriya → kateqoriyanı. */
export function azAccusative(word: string): string {
  const v = lastVowel(word)
  const harmony = 'aı'.includes(v) ? 'ı' : 'ou'.includes(v) ? 'u' : 'öü'.includes(v) ? 'ü' : 'i'
  return word + (endsInVowel(word) ? 'n' : '') + harmony
}

// Possessive suffix of a numeral follows its last spoken word: 1 bir → 1-i, 3 üç → 3-ü, 17 on yeddi → 17-si.
const UNITS = ['ı', 'i', 'si', 'ü', 'ü', 'i', 'sı', 'si', 'i', 'u'] // sıfır, bir, iki, üç, dörd, beş, altı, yeddi, səkkiz, doqquz
const TENS = ['', 'u', 'si', 'u', 'ı', 'si', 'ı', 'i', 'i', 'ı'] // –, on, iyirmi, otuz, qırx, əlli, altmış, yetmiş, səksən, doxsan

/** "17 of them" as written in Azerbaijani: 17 → "17-si", 1 → "1-i", 40 → "40-ı", 100 → "100-ü", 0 → "0-ı". */
export function azCountOf(n: number): string {
  const x = Math.abs(Math.trunc(n))
  let suffix: string
  if (x === 0) suffix = 'ı'
  else if (x % 10 !== 0) suffix = UNITS[x % 10]
  else if (x % 100 !== 0) suffix = TENS[(x / 10) % 10]
  else if (x % 1000 !== 0) suffix = 'ü' // yüz
  else if (x % 1_000_000 !== 0) suffix = 'i' // min
  else suffix = 'u' // milyon
  return `${n}-${suffix}`
}

const ORD_UNITS = ['', 'ci', 'ci', 'cü', 'cü', 'ci', 'cı', 'ci', 'ci', 'cu'] // bir, iki, üç, dörd, beş, altı, yeddi, səkkiz, doqquz
const ORD_TENS = ['', 'cu', 'ci', 'cu', 'cı', 'ci', 'cı', 'ci', 'ci', 'cı'] // on, iyirmi, otuz, qırx, əlli, altmış, yetmiş, səksən, doxsan

/** An ordinal as written with digits: 1 → "1-ci", 3 → "3-cü", 6 → "6-cı", 10 → "10-cu", 100 → "100-cü". */
export function azOrdinal(n: number): string {
  const x = Math.abs(Math.trunc(n))
  const suffix =
    x % 10 !== 0 ? ORD_UNITS[x % 10] : x % 100 !== 0 ? ORD_TENS[(x / 10) % 10] : x % 1000 !== 0 ? 'cü' : 'ci'
  return `${n}-${suffix}`
}

/** A prize place: "1-ci yer", "2-ci yer", "3-cü yer". */
export const placeName = (place: number) => `${azOrdinal(place)} yer`
