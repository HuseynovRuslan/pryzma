// Azerbaijani glyph audit against the running app, using the app's own font tokens.
// For every display/text weight in use it checks that each letter (Ə ə I ı İ i Ğ ğ Ş ş Ç ç Ö ö Ü ü):
//   1. comes from the family itself — measured against two different fallbacks; any width difference
//      means the browser substituted another font for that glyph;
//   2. (display) follows the width axis like its base letter, so condensed headlines stay even.
// Also saves a specimen of the test sentences on desktop and mobile.
// Usage: node e2e/glyphs.mjs [outDir]
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'

const base = process.env.BASE_URL ?? 'http://localhost:5173'
const out = process.argv[2] ?? 'e2e/screenshots'
mkdirSync(out, { recursive: true })

const SAMPLES = [
  'Bakı kəndləri üçün şevron dizayn müsabiqəsi',
  'İdeyanı meydana çıxar',
  'Müsabiqələr',
  'Qeydiyyat',
  'Şüvəlan Əmircan Bülbülə Görədil Kürdəxanı Pirşağı Maştağa',
  'Layihələr Kateqoriyalar Təkliflər İştirakçılar Qaralama Göndərilib Yoxlanılır Təsdiqlənib Rədd edilib',
  'Qalib Nəticələr Nəticələr açıqlanıb İşlər Qəsəbələr Təkliflər Əmircan Şüvəlan Kürdəxanı',
  'QALİB · NƏTİCƏLƏR AÇIQLANIB · İŞLƏR · ŞÜVƏLAN · KÜRDƏXANI · ƏMİRCAN · ÇÖL · ÖĞÜD',
  'MÜSABİQƏLƏR · ƏMİRCAN · ŞÜVƏLAN · KÜRDƏXANI · BÜLBÜLƏ · QEYDİYYAT',
]

const browser = await chromium.launch({ channel: 'chromium' })
let failures = 0

for (const [device, viewport] of [['desktop', { width: 1440, height: 900 }], ['mobile', { width: 390, height: 844 }]]) {
  const page = await browser.newPage({ viewport, deviceScaleFactor: device === 'mobile' ? 2 : 1 })
  await page.goto(base + '/', { waitUntil: 'networkidle' })

  const report = await page.evaluate(async (samples) => {
    const AZ = 'ƏəIıİiĞğŞşÇçÖöÜü'
    const PAIRS = [['ə', 'e'], ['Ə', 'E'], ['İ', 'I'], ['ı', 'i'], ['ğ', 'g'], ['Ğ', 'G'], ['ş', 's'], ['Ş', 'S'],
      ['ç', 'c'], ['Ç', 'C'], ['ö', 'o'], ['Ö', 'O'], ['ü', 'u'], ['Ü', 'U']]
    const css = getComputedStyle(document.documentElement)
    const first = (stack) => stack.split(',')[0].trim()
    const faces = [
      { role: 'display', family: first(css.getPropertyValue('--font-display')), weights: [700, 760, 800, 840, 860], stretch: '62%' },
      { role: 'text', family: first(css.getPropertyValue('--font-text')), weights: [400, 500, 600, 700], stretch: null },
    ]
    const ctx = document.createElement('canvas').getContext('2d')
    const results = []
    for (const f of faces) {
      for (const w of f.weights) await document.fonts.load(`${w} 64px ${f.family}`, AZ)
      const fallback = []
      for (const w of f.weights) {
        for (const ch of AZ) {
          ctx.font = `${w} 64px ${f.family}, monospace`
          const a = ctx.measureText(ch).width
          ctx.font = `${w} 64px ${f.family}, serif`
          if (Math.abs(a - ctx.measureText(ch).width) > 0.01) fallback.push(`${ch}@${w}`)
        }
      }
      const width = []
      if (f.stretch) {
        const el = document.createElement('span')
        el.style.cssText = `position:absolute;visibility:hidden;white-space:pre;font-size:100px;font-family:${f.family};font-weight:${f.weights.at(-1)}`
        document.body.appendChild(el)
        const w = (ch, st) => { el.style.fontStretch = st; el.textContent = ch.repeat(10); return el.getBoundingClientRect().width }
        for (const [az, b] of PAIRS) {
          const ra = w(az, f.stretch) / w(az, '100%')
          const rb = w(b, f.stretch) / w(b, '100%')
          if (Math.abs(ra - rb) > 0.04) width.push(`${az} ${ra.toFixed(2)} vs ${b} ${rb.toFixed(2)}`)
        }
        el.remove()
      }
      results.push({ ...f, fallback, width })
    }

    // Specimen overlay using the real tokens.
    const sheet = document.createElement('div')
    sheet.id = 'glyph-specimen'
    sheet.style.cssText = 'position:fixed;inset:0;z-index:9999;overflow:auto;background:var(--paper);padding:24px;color:var(--ink)'
    const rows = [
      ['h1 · display 860 / 62%', 'font-family:var(--font-display);font-weight:860;font-stretch:62%;font-size:clamp(28px,5vw,64px);line-height:1'],
      ['h2 · display 760 / 80%', 'font-family:var(--font-display);font-weight:760;font-stretch:80%;font-size:clamp(22px,3vw,36px);line-height:1.1'],
      ['text 400', 'font-family:var(--font-text);font-weight:400;font-size:17px'],
      ['text 600', 'font-family:var(--font-text);font-weight:600;font-size:17px'],
    ]
    for (const [label, style] of rows) {
      const h = document.createElement('p')
      h.textContent = label
      h.style.cssText = 'font:600 12px system-ui;margin:18px 0 6px;color:var(--muted)'
      sheet.appendChild(h)
      for (const s of samples) {
        const p = document.createElement('p')
        p.textContent = s
        p.style.cssText = style + ';margin:0 0 4px'
        sheet.appendChild(p)
      }
    }
    document.body.appendChild(sheet)
    return results
  }, SAMPLES)

  for (const r of report) {
    const ok = r.fallback.length === 0 && r.width.length === 0
    if (!ok) failures++
    console.log(`${ok ? 'PASS' : 'FAIL'} ${device} ${r.role} ${r.family} fallback=[${r.fallback.join(' ')}] width=[${r.width.join(' | ')}]`)
  }
  await page.screenshot({ path: `${out}/glyphs-${device}.png`, fullPage: false })
  await page.close()
}

await browser.close()
process.exit(failures ? 1 : 0)
