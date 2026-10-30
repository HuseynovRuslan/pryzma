import { describe, expect, it } from 'vitest'
import { formatPrizeFund, parsePrizes, prizeFund } from './prizes'

describe('parsePrizes', () => {
  it('reads the places out of the text the admin typed', () => {
    const { places, note } = parsePrizes(
      '1-ci yer — 500 AZN + Sertifikat\n2-ci yer — 300 AZN + Sertifikat\n3-cü yer — 200 AZN + Sertifikat\n\n' +
        'Tətbiq imkanı\nSeçilmiş simvol və dizaynlar abadlıq layihələrində istifadə oluna bilər.',
    )

    expect(places).toEqual([
      { place: '1-ci yer', award: '500 AZN', extra: 'Sertifikat' },
      { place: '2-ci yer', award: '300 AZN', extra: 'Sertifikat' },
      { place: '3-cü yer', award: '200 AZN', extra: 'Sertifikat' },
    ])
    expect(note).toBe('Tətbiq imkanı\nSeçilmiş simvol və dizaynlar abadlıq layihələrində istifadə oluna bilər.')
  })

  it('accepts a colon and a place without anything extra', () => {
    expect(parsePrizes('1-ci yer: 1000 AZN').places).toEqual([
      { place: '1-ci yer', award: '1000 AZN', extra: null },
    ])
  })

  it('hands back text that names no places, so it can be shown as written', () => {
    const text = 'Mükafat fondu müsabiqədən sonra elan olunacaq.'
    expect(parsePrizes(text)).toEqual({ places: [], note: text })
  })
})

describe('prizeFund', () => {
  const places = (...awards: string[]) => awards.map((award, i) => ({ place: `${i + 1}-ci yer`, award, extra: null }))

  it('adds the places up, so the headline figure matches the list under it', () => {
    const { places: parsed } = parsePrizes(
      '1-ci yer — 250 AZN + Sertifikat\n2-ci yer — 150 AZN + Sertifikat\n3-cü yer — 100 AZN + Sertifikat',
    )
    expect(prizeFund(parsed)).toEqual({ amount: 500, unit: 'AZN' })
  })

  it('reads grouped thousands', () => {
    expect(prizeFund(places('1 000 AZN', '500 AZN'))).toEqual({ amount: 1500, unit: 'AZN' })
  })

  it('states no fund when a place is not money, or the currencies differ', () => {
    expect(prizeFund(places('250 AZN', 'Sərgidə iştirak'))).toBeNull()
    expect(prizeFund(places('250 AZN', '100 USD'))).toBeNull()
    expect(prizeFund([])).toBeNull()
  })

  it('writes the fund with the figure and currency held together', () => {
    const nbsp = String.fromCharCode(0xa0)
    expect(formatPrizeFund({ amount: 500, unit: 'AZN' })).toBe(`500${nbsp}AZN`)
    expect(formatPrizeFund({ amount: 12500, unit: 'AZN' })).toBe(`12${nbsp}500${nbsp}AZN`)
  })
})
