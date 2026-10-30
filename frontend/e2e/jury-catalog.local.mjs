// The jury catalog — the printable evaluation form — on A4, for design review: three jurors, five jurors (the full
// 99-work PDF), seven (the fields wrap, none is left out); page size and page breaks checked, A4-proportion pictures,
// and the juror's informed screens. Local QA database only: it adds jurors and issues an invitation there.
// Expects the API on :5080 and Vite on :5173; admin credentials come from QA_ADMIN_EMAIL / QA_ADMIN_PASSWORD.
import { chromium } from '@playwright/test'
import { mkdirSync, readFileSync } from 'node:fs'

const base = process.env.BASE_URL ?? 'http://localhost:5173'
const out = process.env.QA_OUT ?? '../design-review/jury-catalog-a4'
const competitionId = Number(process.env.QA_COMPETITION ?? 3)
if (!/localhost|127\.0\.0\.1/.test(base)) throw new Error('local only')
mkdirSync(out, { recursive: true })

const browser = await chromium.launch({ channel: 'chrome' }).catch(() => chromium.launch())
// Twice the pixels of a 96 dpi page: an A4 sheet comes out 1588 × 2238 px, its own proportions.
const ctx = await browser.newContext({ viewport: { width: 1000, height: 1300 }, deviceScaleFactor: 2, reducedMotion: 'reduce' })
const page = await ctx.newPage()
await page.goto(base + '/')
const login = await page.request.post(base + '/api/auth/login', {
  data: { email: process.env.QA_ADMIN_EMAIL, password: process.env.QA_ADMIN_PASSWORD, rememberMe: false },
})
if (!login.ok()) throw new Error(`admin login ${login.status()}`)
const token = (await login.json()).accessToken
const admin = async (method, path, data) => {
  const res = await page.request.fetch(`${base}/api/admin/competitions/${competitionId}/jury-evaluation${path}`, {
    method,
    data,
    headers: { Authorization: `Bearer ${token}` },
  })
  if (!res.ok()) throw new Error(`${method} ${path}: ${res.status()} ${await res.text()}`)
  return res.headers()['content-type']?.includes('json') ? res.json() : res.text()
}

let e = await admin('GET', '')
if (!e.exists) e = await admin('POST', '')
if (e.status === 'Draft') e = await admin('POST', '/roster/freeze')
console.log(`roster ${e.rosterSize}, jurors ${e.jurors.length}, scores ${e.completedScores}`)

/** Page count and page size straight from the PDF's own page objects. */
function pdfFacts(path) {
  const raw = readFileSync(path).toString('latin1')
  const pages = (raw.match(/\/Type\s*\/Page(?![s\w])/g) ?? []).length
  const boxes = [...new Set((raw.match(/\/MediaBox\s*\[[^\]]+\]/g) ?? []).map((b) => b.replace(/\s+/g, ' ')))]
  return { pages, boxes, bytes: raw.length }
}

/** The catalog as it renders: every sheet A4, nothing outside its card or its sheet, nothing cut, no long reference. */
async function check(tag) {
  const r = await page.evaluate(() => {
    const mm = 96 / 25.4
    const problems = []
    const sheets = [...document.querySelectorAll('.jp-sheet')]
    let minFigure = Infinity
    let box = null
    sheets.forEach((s, i) => {
      const sr = s.getBoundingClientRect()
      if (Math.abs(sr.width - 210 * mm) > 1 || Math.abs(sr.height - 296 * mm) > 1) problems.push(`sheet ${i + 1}: ${sr.width}×${sr.height}`)
      const grid = s.querySelector('.jp-grid').getBoundingClientRect()
      const foot = s.querySelector('.jp-foot').getBoundingClientRect()
      if (grid.bottom > foot.top + 0.5) problems.push(`sheet ${i + 1}: cards run into the footer`)
      if (foot.bottom > sr.bottom + 0.5) problems.push(`sheet ${i + 1}: footer off the page`)
      for (const c of s.querySelectorAll('.jp-card')) {
        const cr = c.getBoundingClientRect()
        if (cr.bottom > grid.bottom + 0.5 || cr.right > sr.right + 0.5) problems.push(`sheet ${i + 1}: a card leaves the grid`)
        for (const el of c.querySelectorAll('.jp-card__top, .jp-card__title, .jp-card__vote, .jp-marks, .jp-mark__box')) {
          const er = el.getBoundingClientRect()
          if (er.bottom > cr.bottom + 0.5 || er.right > cr.right + 0.5) problems.push(`sheet ${i + 1}: ${el.className} cut (${c.getAttribute('aria-label')})`)
          if (el.scrollHeight > el.clientHeight + 1 && !el.classList.contains('jp-mark__box')) problems.push(`sheet ${i + 1}: ${el.className} overflows`)
        }
        minFigure = Math.min(minFigure, c.querySelector('.jp-card__fig').getBoundingClientRect().height / mm)
        const b = c.querySelector('.jp-mark__box')?.getBoundingClientRect()
        if (b && !box) box = { w: +(b.width / mm).toFixed(1), h: +(b.height / mm).toFixed(1) }
      }
    })
    const text = document.querySelector('.pr-pages').innerText
    const images = [...document.querySelectorAll('.jp-card__fig img')]
    return {
      sheets: sheets.length,
      cardsPerSheet: [...new Set(sheets.map((s) => s.querySelectorAll('.jp-card').length))],
      keysPerCard: [...new Set([...document.querySelectorAll('.jp-card')].map((c) => c.querySelectorAll('.jp-mark').length))],
      legendPerSheet: [...new Set(sheets.map((s) => s.querySelectorAll('.jp-legend__item').length))],
      images: `${images.filter((i) => i.complete && i.naturalWidth > 0).length}/${document.querySelectorAll('.jp-card').length}`,
      minFigureMm: +minFigure.toFixed(1),
      scoreBoxMm: box,
      longReferences: (text.match(/SUB-[\d-]+/g) ?? []).length,
      first: [...document.querySelectorAll('.jp-card')].slice(0, 4).map((c) => `${c.querySelector('.jp-card__pos').textContent} ${c.querySelector('.jp-card__no').textContent}`),
      problems,
    }
  })
  console.log(`${tag}: ${JSON.stringify(r)}`)
  if (r.problems.length > 0 || r.longReferences > 0) throw new Error(`${tag}: layout problems`)
  return r
}

async function catalog(tag, { shots = [1], pdf = true } = {}) {
  await page.goto(`${base}/admin/jury-print/${competitionId}?doc=catalog`, { waitUntil: 'networkidle' })
  await page.getByText(/Bütün şəkillər yükləndi/).first().waitFor({ timeout: 180000 })
  await page.waitForTimeout(500)
  await check(tag)
  // The pages as they print: the toolbar is gone, each sheet exactly A4.
  await page.emulateMedia({ media: 'print' })
  for (const n of shots) await page.locator('.jp-sheet').nth(n - 1).screenshot({ path: `${out}/${tag}-page-${String(n).padStart(2, '0')}.png` })
  if (pdf) {
    const path = `${out}/jury-catalog-${tag}.pdf`
    await page.pdf({ path, preferCSSPageSize: true, printBackground: true })
    console.log(`${tag} pdf: ${JSON.stringify(pdfFacts(path))}`)
  }
  await page.emulateMedia({ media: 'screen' })
}

// QA_ONLY=catalog: the catalog as the jurors stand now, checked, its first page and its PDF; then stop.
if (process.env.QA_ONLY === 'catalog') {
  await catalog(`${e.jurors.length}-jurors-now`, { shots: [1] })
  await browser.close()
  process.exit(0)
}

// QA_ONLY=juror: the juror's screens alone (the jurors added before stay; a removed juror's name cannot be added again).
if (process.env.QA_ONLY !== 'juror') {
  // Three jurors, as the final had them.
  const added = []
  while (e.jurors.length > 3) e = await admin('DELETE', `/jurors/${e.jurors[e.jurors.length - 1].jurorId}`)
  await catalog('3-jurors', { shots: [1] })

  // Five: two more added before any score (QA placeholder names, not the real roster).
  for (const label of ['Leyla Hüseynova', 'Kamran Rzayev']) e = await admin('POST', '/jurors', { label })
  await catalog('5-jurors', { shots: [1, 2, 13, 25] })

  // Seven: the fields wrap onto a second row of the same card; then back to five.
  for (const label of ['Səbinə Əhmədova', 'Tural Quliyev']) {
    e = await admin('POST', '/jurors', { label })
    added.push(e.jurors[e.jurors.length - 1].jurorId)
  }
  await catalog('7-jurors', { shots: [1], pdf: false })
  for (const id of added) e = await admin('DELETE', `/jurors/${id}`)
  console.log(`back to ${e.jurors.length} jurors`)
}

// The toolbar on screen, and the jury tab's print panel.
await page.goto(`${base}/admin/jury-print/${competitionId}?doc=catalog`, { waitUntil: 'networkidle' })
await page.getByText(/Bütün şəkillər yükləndi/).first().waitFor({ timeout: 180000 })
await page.screenshot({ path: `${out}/admin-print-view-screen.png` })
await page.goto(`${base}/admin/competitions/${competitionId}?tab=jury`, { waitUntil: 'networkidle' })
await page.getByRole('heading', { name: 'Çap' }).scrollIntoViewIfNeeded()
await page.locator('.fin-print').screenshot({ path: `${out}/admin-print-panel.png` })

// The juror's screens: the same order and numbers, informed.
const invite = await admin('POST', `/jurors/${e.jurors[0].jurorId}/invitation`, { days: 3 })
for (const [name, options] of [
  ['desktop', { viewport: { width: 1440, height: 900 } }],
  ['mobile', { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true }],
]) {
  const jc = await browser.newContext({ ...options, reducedMotion: 'reduce' })
  const jp = await jc.newPage()
  await jp.goto(base + invite.link, { waitUntil: 'networkidle' })
  await jp.getByRole('textbox', { name: 'Giriş kodu' }).fill(invite.code)
  await jp.getByRole('button', { name: 'Daxil ol' }).click()
  const yes = jp.getByRole('button', { name: 'Bəli, bu mənəm' })
  await yes.waitFor({ timeout: 15000 }).then(() => yes.click()).catch(() => {})
  await jp.waitForURL('**/jury/evaluate')
  await jp.waitForLoadState('networkidle')
  await jp.waitForTimeout(600)
  await jp.screenshot({ path: `${out}/juror-dashboard-${name}.png` })
  const list = await jp.locator('.jr-grid').innerText()
  if (/SUB-\d/.test(list)) throw new Error('juror list shows a long reference')
  await jp.locator('.jr-card').first().click()
  await jp.waitForLoadState('networkidle')
  await jp.waitForTimeout(800)
  await jp.screenshot({ path: `${out}/juror-work-${name}.png` })
  const overflow = await jp.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)
  console.log(`juror ${name}${overflow ? ' ⚠ horizontal overflow' : ''}`)
  await jc.close()
}
// Nothing scored by this run.
console.log(`scores after: ${(await admin('GET', '')).completedScores}`)
await browser.close()
