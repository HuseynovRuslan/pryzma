// The admin-only vote analytics catalog: desktop and mobile previews and a sample A4 PDF (local QA database only).
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'

const base = process.env.BASE_URL ?? 'http://localhost:5173'
const out = process.argv[2] ?? '../design-review/vote-analytics'
const id = Number(process.env.QA_COMPETITION ?? 3)
if (!/localhost|127\.0\.0\.1/.test(base)) throw new Error('local only')
mkdirSync(out, { recursive: true })

const browser = await chromium.launch({ channel: 'chrome' }).catch(() => chromium.launch())
const login = async (ctx) => {
  const page = await ctx.newPage()
  await page.goto(base + '/')
  const res = await page.request.post(base + '/api/auth/login', {
    data: { email: process.env.QA_ADMIN_EMAIL, password: process.env.QA_ADMIN_PASSWORD },
  })
  if (!res.ok()) throw new Error(`login ${res.status()}`)
  return page
}
const ready = (page) => page.getByText(/Bütün şəkillər yükləndi/).first().waitFor({ timeout: 120000 })
const url = (q = '') => `${base}/admin/jury-print/${id}?doc=analytics${q}`

const desktop = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
const page = await login(desktop)
await page.goto(url(), { waitUntil: 'networkidle' })
await ready(page)
await page.screenshot({ path: `${out}/01-analytics-desktop.png` })
await page.locator('.va-sheet').first().screenshot({ path: `${out}/02-analytics-a4-page-1.png` })
console.log('desktop')
await page.goto(url('&only=invalidated'), { waitUntil: 'networkidle' })
await ready(page)
await page.locator('.va-sheet').first().screenshot({ path: `${out}/03-analytics-only-invalidated.png` })
await page.goto(url('&order=reference'), { waitUntil: 'networkidle' })
await ready(page)
await page.locator('.va-sheet').first().screenshot({ path: `${out}/04-analytics-reference-order.png` })
console.log('filters')

await page.goto(url(), { waitUntil: 'networkidle' })
await ready(page)
await page.emulateMedia({ media: 'print' })
await page.pdf({ path: `${out}/vote-analytics-sample.pdf`, preferCSSPageSize: true, printBackground: true })
await page.emulateMedia({ media: 'screen' })
console.log('pdf')

// The jury's own catalog of the same works: reference order, no vote.
await page.goto(`${base}/admin/jury-print/${id}?doc=catalog`, { waitUntil: 'networkidle' })
await ready(page)
const juryText = await page.locator('.pr-pages').innerText()
if (/etibarlı|etibarsız|audit/i.test(juryText)) throw new Error('the jury catalog shows vote data')
await page.locator('.jp-sheet').first().screenshot({ path: `${out}/05-jury-catalog-blind.png` })
console.log('jury catalog: no vote data')

const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true })
const mobile = await login(phone)
await mobile.goto(url(), { waitUntil: 'networkidle' })
await ready(mobile)
await mobile.screenshot({ path: `${out}/06-analytics-mobile-top.png` })
await mobile.locator('.va-sheet').first().scrollIntoViewIfNeeded()
await mobile.screenshot({ path: `${out}/07-analytics-mobile-page.png` })
const overflow = await mobile.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)
console.log('mobile', overflow ? 'page overflow!' : 'no page overflow')
await browser.close()
