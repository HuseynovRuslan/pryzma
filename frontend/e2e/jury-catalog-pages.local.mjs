// Pictures of the jury catalog pages holding the given works ("İş №512,505"), as they print. Local QA database only.
import { chromium } from '@playwright/test'

const base = process.env.BASE_URL ?? 'http://localhost:5173'
const out = process.env.QA_OUT ?? '../design-review/jury-catalog-a4'
const competitionId = Number(process.env.QA_COMPETITION ?? 3)
const numbers = (process.env.QA_WORKS ?? '512,505').split(',').map((n) => `İş №${n.trim()}`)
if (!/localhost|127\.0\.0\.1/.test(base)) throw new Error('local only')

const browser = await chromium.launch({ channel: 'chrome' }).catch(() => chromium.launch())
const page = await (await browser.newContext({ viewport: { width: 1000, height: 1300 }, deviceScaleFactor: 2 })).newPage()
await page.goto(base + '/')
const login = await page.request.post(base + '/api/auth/login', {
  data: { email: process.env.QA_ADMIN_EMAIL, password: process.env.QA_ADMIN_PASSWORD, rememberMe: false },
})
if (!login.ok()) throw new Error(`admin login ${login.status()}`)
await page.goto(`${base}/admin/jury-print/${competitionId}?doc=catalog`, { waitUntil: 'networkidle' })
await page.getByText(/Bütün şəkillər yükləndi/).first().waitFor({ timeout: 180000 })
await page.emulateMedia({ media: 'print' })
const done = new Set()
for (const number of numbers) {
  const index = await page.evaluate((n) => [...document.querySelectorAll('.jp-sheet')].findIndex((s) => [...s.querySelectorAll('.jp-card__no')].some((x) => x.textContent === n)), number)
  if (index < 0 || done.has(index)) continue
  done.add(index)
  await page.locator('.jp-sheet').nth(index).screenshot({ path: `${out}/5-jurors-page-${String(index + 1).padStart(2, '0')}.png` })
  console.log(`${number}: page ${index + 1}`)
}
await browser.close()
