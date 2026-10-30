// Visual QA helper: screenshots every key page at the agreed breakpoints and reports horizontal overflow.
// Usage: node e2e/shots.mjs [outDir]   (expects the dev server on http://localhost:5173)
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'

const base = process.env.BASE_URL ?? 'http://localhost:5173'
const out = process.argv[2] ?? 'e2e/screenshots'
mkdirSync(out, { recursive: true })

const widths = [375, 430, 768, 1024, 1440]
const pages = [
  ['home', '/'],
  ['project', '/projects/baki-kendleri'],
  ['competition', '/competitions/baki-kendleri-sevron-dizayni'],
  ['competitions', '/competitions'],
  ['login', '/login'],
  ['register', '/register'],
  ['howto', '/how-to-participate'],
]

const only = process.env.ONLY?.split(',')
const browser = await chromium.launch({ channel: 'chromium' })
let problems = 0

for (const width of widths) {
  const context = await browser.newContext({
    viewport: { width, height: width < 768 ? 812 : 900 },
    deviceScaleFactor: 1,
    reducedMotion: 'reduce',
  })
  const page = await context.newPage()
  for (const [name, path] of pages) {
    if (only && !only.includes(name)) continue
    await page.goto(base + path, { waitUntil: 'networkidle' })
    await page.waitForTimeout(150)
    const overflow = await page.evaluate(() => {
      const doc = document.documentElement
      const offenders = [...document.querySelectorAll('body *')]
        .filter((el) => el.getBoundingClientRect().right > doc.clientWidth + 1)
        .slice(0, 5)
        .map((el) => `${el.tagName.toLowerCase()}.${[...el.classList].join('.')}`)
      return { scroll: doc.scrollWidth, client: doc.clientWidth, offenders }
    })
    if (overflow.scroll > overflow.client) {
      problems++
      console.log(`OVERFLOW ${name}@${width}: ${overflow.scroll} > ${overflow.client}`, overflow.offenders)
    }
    await page.screenshot({ path: `${out}/${name}-${width}.png`, fullPage: process.env.FULL === '1' })
  }
  await context.close()
}

await browser.close()
console.log(problems ? `${problems} overflow problem(s)` : 'No horizontal overflow at any breakpoint.')
process.exit(problems ? 1 : 0)
