// Design-review captures: home, project and competition pages on desktop (1440) and phone (390).
// Usage: node e2e/review.mjs [outDir]   (expects the dev stack on http://localhost:5173)
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'

const base = process.env.BASE_URL ?? 'http://localhost:5173'
const out = process.argv[2] ?? '../design-review'
mkdirSync(out, { recursive: true })

const devices = [
  { name: 'desktop', viewport: { width: 1440, height: 900 }, scale: 1 },
  { name: 'mobile', viewport: { width: 390, height: 844 }, scale: 2 },
]
const pages = [
  ['1-home', '/'],
  ['2-baki-kendleri', '/projects/baki-kendleri'],
  ['3-competition', '/competitions/baki-kendleri-sevron-dizayni'],
]

const browser = await chromium.launch({ channel: 'chromium' })
for (const d of devices) {
  const context = await browser.newContext({ viewport: d.viewport, deviceScaleFactor: d.scale, reducedMotion: 'reduce' })
  const page = await context.newPage()
  for (const [name, path] of pages) {
    await page.goto(base + path, { waitUntil: 'networkidle' })
    await page.evaluate(() => document.fonts.ready)
    // First screen exactly as a visitor lands on it, then the whole page.
    await page.screenshot({ path: `${out}/${name}-${d.name}-first-screen.png` })
    await page.screenshot({ path: `${out}/${name}-${d.name}-full.png`, fullPage: true })
  }

  // Competition page after a category is chosen: the selection state and the participation panel/bar.
  await page.goto(`${base}/competitions/baki-kendleri-sevron-dizayni?category=numune-2`, { waitUntil: 'networkidle' })
  await page.locator('#categories').scrollIntoViewIfNeeded()
  await page.evaluate(() => window.scrollBy(0, -120))
  await page.screenshot({ path: `${out}/3-competition-${d.name}-category-selected.png` })
  await context.close()
}
await browser.close()
console.log(`Saved to ${out}`)
