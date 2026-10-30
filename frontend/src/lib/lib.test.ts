import { describe, expect, it, beforeEach } from 'vitest'
import { authPath, participatePath, readIntent, safeNext, saveIntent } from '../auth/intent'
import { readableOn } from './accent'
import { daysLeft, formatDate, formatDayMonth, formatTime, shortCompetitionTitle } from './format'
import { SITE_URL, canonicalUrl } from './canonical'
import { normaliseAz } from './text'
import { azMobile, hasErrors, validatePhone, validateRegister } from './validation'

describe('safeNext', () => {
  it('keeps same-site relative paths', () => {
    expect(safeNext('/competitions/x/submit?category=a')).toBe('/competitions/x/submit?category=a')
  })

  it.each(['https://evil.example', '//evil.example', '/\\evil.example', 'javascript:alert(1)'])(
    'rejects open redirect %s',
    (next) => expect(safeNext(next, '/')).toBe('/'),
  )
})

describe('participation intent', () => {
  beforeEach(() => sessionStorage.clear())

  it('round-trips the chosen competition and category', () => {
    saveIntent({
      competitionSlug: 'baki-kendleri-sevron-dizayni',
      competitionTitle: 'Şevron',
      projectName: 'Bakı kəndləri',
      categorySlug: 'numune-1',
      categoryName: 'Nümunə 1',
      categoryLabel: 'Kəndlər',
    })
    expect(readIntent()?.categoryName).toBe('Nümunə 1')
  })

  it('builds encoded paths that survive the login detour', () => {
    const target = participatePath('baki-kendleri-sevron-dizayni', 'numune-1')
    expect(target).toBe('/competitions/baki-kendleri-sevron-dizayni/submit?category=numune-1')
    expect(authPath('login', target)).toBe(`/login?next=${encodeURIComponent(target)}`)
  })
})

describe('normaliseAz', () => {
  it('matches Azerbaijani letters typed on a Latin keyboard', () => {
    expect(normaliseAz('Maştağa')).toBe('mastaga')
    expect(normaliseAz('Mərdəkan')).toContain('merdekan')
    expect(normaliseAz('İÇƏRİŞƏHƏR')).toBe('iceriseher')
  })
})

describe('readableOn', () => {
  it('picks light text on dark accents and dark text on light ones', () => {
    expect(readableOn('#56652A')).toBe('#f3f4f0')
    expect(readableOn('#F0B323')).toBe('#10262b')
  })
})

describe('formatDate', () => {
  it('formats in Baku time with Azerbaijani month names', () => {
    expect(formatDate('2026-10-01T20:30:00Z')).toBe('2 oktyabr 2026')
    expect(formatDate(null)).toBe('')
  })
})

describe('validateRegister', () => {
  it('requires every field and the terms', () => {
    const errors = validateRegister({
      firstName: '',
      lastName: '',
      phone: '',
      email: 'x',
      password: 'short',
      confirmPassword: 'other',
      acceptTerms: false,
    })
    expect(Object.values(errors).filter(Boolean)).toHaveLength(7)
  })

  it('passes a valid form', () => {
    const errors = validateRegister({
      firstName: 'Aysel',
      lastName: 'Məmmədova',
      phone: '+994 50 123 45 67',
      email: 'aysel@example.com',
      password: 'secret123',
      confirmPassword: 'secret123',
      acceptTerms: true,
    })
    expect(hasErrors(errors)).toBe(false)
  })
})

describe('azMobile', () => {
  it('reads every usual way of writing a mobile as one number', () => {
    for (const typed of ['501234567', '050 123 45 67', '(050) 123-45-67', '+994 50 123 45 67', '+994 050 123 45 67', '00994 50 123 45 67'])
      expect(azMobile(typed)).toBe('501234567')
    for (const code of ['10', '51', '55', '60', '70', '77', '99']) expect(azMobile(`0${code} 123 45 67`)).toBe(`${code}1234567`)
  })

  it('refuses landlines, unknown codes, wrong lengths and foreign numbers', () => {
    for (const typed of ['', 'abc', '+994 12 123 45 67', '018 123 45 67', '+994 59 123 45 67', '+994 50 123 45 6', '05012345678', '+7 912 345 67 89'])
      expect(azMobile(typed)).toBeNull()
    expect(validatePhone('+994 12 444 55 66')).toMatch(/Azərbaycan mobil nömrəsini yazın/)
    expect(validatePhone('050 444 55 66')).toBeUndefined()
  })
})

describe('Azerbaijani case endings', () => {
  it('follows vowel harmony for "from", "in" and "of"', async () => {
    const { azAblative, azLocative, azGenitive, azAccusative } = await import('./text')
    expect(azAblative('qəsəbə')).toBe('qəsəbədən')
    expect(azAblative('kateqoriya')).toBe('kateqoriyadan')
    expect(azAblative('kənd')).toBe('kənddən')
    expect(azLocative('qəsəbə')).toBe('qəsəbədə')
    expect(azLocative('nominasiya')).toBe('nominasiyada')
    expect(azGenitive('qəsəbə')).toBe('qəsəbənin')
    expect(azGenitive('kateqoriya')).toBe('kateqoriyanın')
    expect(azGenitive('kənd')).toBe('kəndin')
    expect(azGenitive('rayon')).toBe('rayonun')
    expect(azGenitive('bölgü')).toBe('bölgünün')
    // The plural labels the filter button names: "Bütün nominasiyaları göstər", "Bütün qəsəbələri göstər".
    expect(azAccusative('nominasiyalar')).toBe('nominasiyaları')
    expect(azAccusative('qəsəbələr')).toBe('qəsəbələri')
    expect(azAccusative('kateqoriya')).toBe('kateqoriyanı')
    expect(azAccusative('bölgü')).toBe('bölgünü')
  })

  it('writes "N of them" the way the number is spoken', async () => {
    const { azCountOf } = await import('./text')
    expect([1, 2, 3, 4, 5, 6, 7, 8, 9].map(azCountOf)).toEqual(['1-i', '2-si', '3-ü', '4-ü', '5-i', '6-sı', '7-si', '8-i', '9-u'])
    expect([10, 17, 20, 30, 40, 42, 50, 59, 60, 70, 80, 90].map(azCountOf)).toEqual([
      '10-u', '17-si', '20-si', '30-u', '40-ı', '42-si', '50-si', '59-u', '60-ı', '70-i', '80-i', '90-ı',
    ])
    expect([0, 100, 200, 1000].map(azCountOf)).toEqual(['0-ı', '100-ü', '200-ü', '1000-i'])
  })
})

describe('shortCompetitionTitle', () => {
  it('keeps the name and drops the kind of contest after it', () => {
    expect(shortCompetitionTitle('“Novxanının simvolu” yaradıcılıq müsabiqəsi')).toBe('Novxanının simvolu')
    expect(shortCompetitionTitle('“Əmircanın simvolu” yaradıcılıq müsabiqəsi')).toBe('Əmircanın simvolu')
  })

  it('understands straight and angle quotation marks too', () => {
    expect(shortCompetitionTitle('"Laçın loqosu" müsabiqəsi')).toBe('Laçın loqosu')
    expect(shortCompetitionTitle('« Xəzərin nəfəsi » plakat müsabiqəsi')).toBe('Xəzərin nəfəsi')
  })

  it('returns a title written without quotation marks whole', () => {
    expect(shortCompetitionTitle('Bakı kəndləri: şevron dizaynı')).toBe('Bakı kəndləri: şevron dizaynı')
    expect(shortCompetitionTitle('  Adsız  ')).toBe('Adsız')
  })
})

describe('deadline formatting', () => {
  // Əmircan's real deadline: 19:59 UTC is 23:59 in Baku, the last minute of 3 October.
  const deadline = '2026-10-03T19:59:00+00:00'

  it('gives the day and the minute in Baku time', () => {
    expect(formatDayMonth(deadline)).toBe('3 oktyabr')
    expect(formatTime(deadline)).toBe('23:59')
    expect(formatDayMonth(null)).toBe('')
    expect(formatTime('not a date')).toBe('')
  })

  it('counts whole Baku days, the last day itself being 0', () => {
    expect(daysLeft(deadline, new Date('2026-09-28T08:00:00Z'))).toBe(5)
    // Late on 2 October in Baku (20:30 UTC is already 00:30 on the 3rd there): the last day has begun.
    expect(daysLeft(deadline, new Date('2026-10-02T20:30:00Z'))).toBe(0)
    expect(daysLeft(deadline, new Date('2026-10-02T19:00:00Z'))).toBe(1)
  })

  it('says nothing once the deadline has passed', () => {
    expect(daysLeft(deadline, new Date('2026-10-03T20:00:00Z'))).toBeNull()
    expect(daysLeft(null)).toBeNull()
  })
})

describe('canonicalUrl', () => {
  it('names the site and the path, and nothing a campaign added', () => {
    expect(canonicalUrl('/')).toBe('https://pryzma.az/')
    expect(canonicalUrl('/emircan')).toBe('https://pryzma.az/emircan')
    expect(canonicalUrl('/competitions/emircanin-simvolu')).toBe('https://pryzma.az/competitions/emircanin-simvolu')
  })

  it('treats a path with and without its trailing slash as one page', () => {
    expect(canonicalUrl('/competitions/')).toBe('https://pryzma.az/competitions')
    expect(canonicalUrl('/competitions//')).toBe('https://pryzma.az/competitions')
  })

  it('never carries the query string: every ad would otherwise be its own page', () => {
    // useCanonical passes location.pathname, which holds no query — this pins that the builder adds none either.
    expect(canonicalUrl('/emircan')).not.toContain('?')
    expect(SITE_URL).toBe('https://pryzma.az')
  })
})
