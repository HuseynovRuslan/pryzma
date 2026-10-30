/**
 * Reads the places out of the prize text an admin typed, so they can be set the way prizes are read — the amount
 * first, then the place — instead of as three similar lines. Presentation only: the text itself is never changed,
 * and anything that is not a place (a note such as "Tətbiq imkanı") is handed back untouched.
 *
 * A place line looks like "1-ci yer — 500 AZN + Sertifikat": an Azerbaijani ordinal, "yer", a dash or colon, then
 * what is won. When nothing matches, the caller shows the text as it was written.
 */
export interface PrizePlace {
  /** "1-ci yer" — as the admin wrote it, not rebuilt from the number. */
  place: string
  /** "500 AZN" — what comes before the first "+". */
  award: string
  /** "Sertifikat" — what comes after it, if anything. */
  extra: string | null
}

export interface Prizes {
  places: PrizePlace[]
  /** Every line that is not a place, in the order and spacing it was typed. */
  note: string
}

const PLACE = /^(\d+\s*-\s*c[iıuü]\s+yer)\s*[—–:-]\s*(.+)$/i

export function parsePrizes(text: string): Prizes {
  const places: PrizePlace[] = []
  const rest: string[] = []

  for (const line of text.split('\n')) {
    const match = line.trim().match(PLACE)
    if (!match) {
      rest.push(line)
      continue
    }
    const [award, ...extras] = match[2].split('+')
    places.push({
      place: match[1].trim(),
      award: award.trim(),
      extra: extras.length > 0 ? extras.join('+').trim() : null,
    })
  }

  return { places, note: rest.join('\n').trim() }
}

/** What the places add up to, in the currency the admin wrote — "500 AZN". */
export interface PrizeFund {
  amount: number
  unit: string
}

// "250 AZN" or "1 000 AZN": the amount first, then the unit, the way every prize here is written. (\s covers the
// no-break space too.) "₼ 250" or "Sərgi" is not a sum of money this can add up.
const AWARD = /^(\d{1,3}(?:\s?\d{3})*|\d+)\s*(\p{L}+)$/u

/**
 * The prize fund, added up from the places — so the headline figure can never disagree with the list under it.
 * Given only when every place is a plain sum of money in one and the same currency; a place that is a trip, a
 * residency or an exhibition makes the fund something that cannot be stated as a number, and then there is none.
 */
export function prizeFund(places: PrizePlace[]): PrizeFund | null {
  if (places.length === 0) return null
  let amount = 0
  let unit: string | null = null
  for (const p of places) {
    const match = p.award.trim().match(AWARD)
    if (!match) return null
    if (unit !== null && match[2] !== unit) return null
    unit = match[2]
    amount += Number(match[1].replace(/\s/g, ''))
  }
  return unit ? { amount, unit } : null
}

/** Built from its code point so the source never holds an invisible character. */
const NBSP = String.fromCharCode(0xa0)

/** "500 AZN", joined by a no-break space so the figure and its currency never part at a line end. */
export function formatPrizeFund(fund: PrizeFund): string {
  // Grouped by hand: Intl's Azerbaijani data is incomplete in some mobile browsers (see lib/format.ts).
  const digits = String(Math.round(fund.amount)).replace(/\B(?=(\d{3})+(?!\d))/g, NBSP)
  return `${digits}${NBSP}${fund.unit}`
}
