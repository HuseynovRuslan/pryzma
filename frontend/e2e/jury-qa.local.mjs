// Local visual QA of the jury evaluation (InternalJuryRankingV1) against a throwaway QA database.
// Expects the API on :5080 and Vite on :5173; admin credentials come from QA_ADMIN_EMAIL / QA_ADMIN_PASSWORD.
// Never run against production: it creates an evaluation, invitations and scores.
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'

const base = process.env.BASE_URL ?? 'http://localhost:5173'
const out = process.argv[2] ?? '../design-review/jury-all-works'
const competitionId = Number(process.env.QA_COMPETITION ?? 3)
mkdirSync(out, { recursive: true })
/** "İş №90" from SUB-2026-00090, as the jury's screens and paper show it. */
const shortNumber = (reference) => `İş №${Number(reference.match(/(\d+)$/)[1])}`
if (!/localhost|127\.0\.0\.1/.test(base)) throw new Error('local only')

const browser = await chromium.launch({ channel: 'chrome' }).catch(() => chromium.launch())
const desktop = { viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, reducedMotion: 'reduce' }
const mobile = { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, reducedMotion: 'reduce' }

// ---- the admin --------------------------------------------------------------------------------------------------
const adminContext = await browser.newContext(desktop)
const adminPage = await adminContext.newPage()
await adminPage.goto(base + '/')
const login = await adminPage.request.post(base + '/api/auth/login', {
  data: { email: process.env.QA_ADMIN_EMAIL, password: process.env.QA_ADMIN_PASSWORD, rememberMe: false },
})
if (!login.ok()) throw new Error(`admin login ${login.status()}`)
const token = (await login.json()).accessToken
const admin = async (method, path, data) => {
  const res = await adminPage.request.fetch(`${base}/api/admin/competitions/${competitionId}/jury-evaluation${path}`, {
    method,
    data,
    headers: { Authorization: `Bearer ${token}` },
  })
  if (!res.ok()) throw new Error(`${method} ${path}: ${res.status()} ${await res.text()}`)
  return res.headers()['content-type']?.includes('json') ? res.json() : res.text()
}

const shot = async (page, name, opts = {}) => {
  await page.waitForTimeout(400)
  await page.screenshot({ path: `${out}/${name}.png`, ...opts })
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)
  console.log(`${name}${overflow ? '  ⚠ horizontal overflow' : ''}`)
}

// Before the switch: the tab offers the new format.
await adminPage.goto(`${base}/admin/competitions/${competitionId}?tab=jury`, { waitUntil: 'networkidle' })
await shot(adminPage, '01-admin-switch-desktop')

let e = await admin('GET', '')
if (!e.exists) e = await admin('POST', '')
if (e.status === 'Draft') e = await admin('POST', '/roster/freeze')
const jurors = e.jurors.map((j) => j.jurorId)
console.log(`roster ${e.rosterSize}, jurors ${jurors.length}`)

// ---- a juror, on a phone and on a laptop ------------------------------------------------------------------------
const invite = await admin('POST', `/jurors/${jurors[0]}/invitation`, { days: 14 })
const code = invite.code
const jurorPhone = await browser.newContext(mobile)
const phone = await jurorPhone.newPage()
await phone.goto(base + invite.link, { waitUntil: 'networkidle' })
await shot(phone, '02-juror-code-mobile')
await phone.getByRole('textbox', { name: 'Giriş kodu' }).fill(code)
await phone.getByRole('button', { name: 'Daxil ol' }).click()
await phone.getByRole('button', { name: 'Bəli, bu mənəm' }).waitFor()
await shot(phone, '03-juror-confirm-name-mobile')
await phone.getByRole('button', { name: 'Bəli, bu mənəm' }).click()
await phone.waitForURL('**/jury/evaluate')
await phone.waitForLoadState('networkidle')

// The juror's first 38 scores, as their screen saves them.
const dash = await (await phone.request.get(base + '/api/jury/dashboard')).json()
// In the juror's order (most valid votes first); `n` is a work's place in it, for the made-up scores.
const works = dash.works.map((w, i) => ({ ...w, n: i + 1 }))
const score = (g, j) => 1 + ((g * 7 + j * 3 + (g % 5) * j) % 10)
for (const w of works.slice(0, 38)) {
  const current = await (await phone.request.get(`${base}/api/jury/works/${w.submissionId}`)).json()
  const res = await phone.request.put(`${base}/api/jury/works/${w.submissionId}/score`, {
    data: { score: score(w.n, 1), comment: w.n % 9 === 0 ? 'Rəng həlli güclüdür.' : null, version: current.version },
  })
  if (!res.ok()) throw new Error(`score ${res.status()}`)
}
await phone.reload({ waitUntil: 'networkidle' })
await shot(phone, '04-juror-dashboard-mobile', { fullPage: false })
// A work with a picture (not a PDF) for the work screen.
const pictured = works.find((w, i) => i >= 38 && w.image && !w.image.pages) ?? works[39]
await phone.goto(`${base}/jury/evaluate/${pictured.submissionId}`, { waitUntil: 'networkidle' })
await shot(phone, '05-juror-work-mobile', { fullPage: true })

// Same juror on a laptop: the same session cookie, a different screen.
const jurorLaptop = await browser.newContext({ ...desktop, storageState: await jurorPhone.storageState() })
const laptop = await jurorLaptop.newPage()
await laptop.goto(`${base}/jury/evaluate`, { waitUntil: 'networkidle' })
await shot(laptop, '06-juror-dashboard-desktop')
await laptop.goto(`${base}/jury/evaluate/${pictured.submissionId}`, { waitUntil: 'networkidle' })
await laptop.keyboard.press('8')
await laptop.getByText(/Yadda saxlanıldı/).waitFor()
await shot(laptop, '07-juror-work-desktop')

// ---- the admin's view mid-way ---------------------------------------------------------------------------------
for (const j of jurors.slice(1)) await admin('POST', `/jurors/${j}/invitation`, { days: 14 })
const lines = (j, from, to) => works.slice(from, to).map((w) => ({ submissionId: w.submissionId, score: String(score(w.n, j)) }))
await admin('POST', '/paper/commit', { jurorId: jurors[1], lines: lines(2, 0, 60), resolutions: [] })
await adminPage.goto(`${base}/admin/competitions/${competitionId}?tab=jury`, { waitUntil: 'networkidle' })
await shot(adminPage, '08-admin-progress-desktop', { fullPage: true })
const adminPhoneContext = await browser.newContext({ ...mobile, storageState: await adminContext.storageState() })
const adminPhone = await adminPhoneContext.newPage()
await adminPhone.goto(`${base}/admin/competitions/${competitionId}?tab=jury`, { waitUntil: 'networkidle' })
await shot(adminPhone, '09-admin-progress-mobile')

// A paper sheet with a conflict, previewed (not saved).
await adminPage.getByLabel('Münsif', { exact: true }).selectOption(String(jurors[0]))
await adminPage.getByLabel(`${shortNumber(works[0].reference)} balı`).waitFor()
await adminPage.getByLabel(`${shortNumber(works[0].reference)} balı`).fill(String(score(1, 1) === 10 ? 9 : score(1, 1) + 1))
await adminPage.getByLabel(`${shortNumber(works[1].reference)} balı`).fill(String(score(2, 1)))
await adminPage.getByLabel(`${shortNumber(works[45].reference)} balı`).fill('7')
await adminPage.getByRole('button', { name: 'Yoxla (önizləmə)' }).click()
await adminPage.getByRole('region', { name: 'Önizləmə' }).waitFor()
await adminPage.getByRole('region', { name: 'Önizləmə' }).scrollIntoViewIfNeeded()
await shot(adminPage, '10-admin-paper-conflict-desktop')

// ---- everyone finishes ------------------------------------------------------------------------------------------
for (const w of works.slice(38)) {
  const current = await (await laptop.request.get(`${base}/api/jury/works/${w.submissionId}`)).json()
  const res = await laptop.request.put(`${base}/api/jury/works/${w.submissionId}/score`, {
    data: { score: score(w.n, 1), comment: null, version: current.version },
  })
  if (!res.ok()) throw new Error(`score ${res.status()} ${await res.text()}`)
}
await laptop.request.post(`${base}/api/jury/submit`)
await admin('POST', '/paper/commit', { jurorId: jurors[1], lines: lines(2, 60, 99), resolutions: [] })
await admin('POST', '/paper/commit', { jurorId: jurors[2], lines: lines(3, 0, 99), resolutions: [] })
await admin('POST', `/jurors/${jurors[1]}/lock`)
await admin('POST', `/jurors/${jurors[2]}/lock`)
await laptop.goto(`${base}/jury/evaluate`, { waitUntil: 'networkidle' })
await shot(laptop, '11-juror-locked-desktop')

// ---- the internal ranking -------------------------------------------------------------------------------------
const ranking = await admin('GET', '/ranking')
console.log(`ranking: complete ${ranking.complete}, ${ranking.rows.length} rows, ${ranking.tieGroups} tie groups`)
await adminPage.goto(`${base}/admin/competitions/${competitionId}?tab=jury`, { waitUntil: 'networkidle' })
await adminPage.getByRole('heading', { name: 'Daxili reytinq' }).scrollIntoViewIfNeeded()
await shot(adminPage, '12-admin-ranking-desktop')
const rankingPanel = adminPage.getByRole('region', { name: 'Daxili reytinq' })
await rankingPanel.screenshot({ path: `${out}/13-admin-ranking-panel-desktop.png` })
console.log('13-admin-ranking-panel-desktop')
await adminPage.getByRole('checkbox', { name: 'Yalnız bərabərliklər' }).check()
await rankingPanel.screenshot({ path: `${out}/14-admin-ranking-ties-only-desktop.png` })
console.log('14-admin-ranking-ties-only-desktop')
await adminPage.getByRole('checkbox', { name: 'Yalnız bərabərliklər' }).uncheck()
await adminPhone.goto(`${base}/admin/competitions/${competitionId}?tab=jury`, { waitUntil: 'networkidle' })
await adminPhone.getByRole('heading', { name: 'Daxili reytinq' }).scrollIntoViewIfNeeded()
await shot(adminPhone, '15-admin-ranking-mobile')
await adminPage.getByRole('heading', { name: 'Audit jurnalı' }).scrollIntoViewIfNeeded()
await shot(adminPage, '16-admin-audit-desktop')

// Nothing public: the competition's page and the public answers know nothing of the jury.
for (const path of [`/api/competitions/emircanin-simvolu`, '/api/competitions', '/api/results']) {
  const text = await (await adminPage.request.get(base + path)).text()
  if (/"(jury\w*|ranking|average|scoreSum|tieSize)"\s*:/i.test(text)) throw new Error(`${path} carries jury data`)
}
console.log('public answers: no jury data')

// ---- the printed documents --------------------------------------------------------------------------------------
const pdf = async (path, file) => {
  await adminPage.goto(base + path, { waitUntil: 'networkidle' })
  await adminPage.getByText(/Bütün şəkillər yükləndi|bərabərlik qrupu/).first().waitFor({ timeout: 120000 })
  await adminPage.emulateMedia({ media: 'print' })
  await adminPage.pdf({ path: `${out}/${file}`, preferCSSPageSize: true, printBackground: true })
  await adminPage.emulateMedia({ media: 'screen' })
  console.log(file)
}
await adminPage.goto(`${base}/admin/jury-print/${competitionId}?doc=catalog`, { waitUntil: 'networkidle' })
await adminPage.getByText(/Bütün şəkillər yükləndi/).waitFor({ timeout: 120000 })
await shot(adminPage, '19-print-catalog-screen-desktop')
await pdf(`/admin/jury-print/${competitionId}?doc=catalog`, 'jury-catalog.pdf')
await pdf(`/admin/jury-print/${competitionId}?doc=ranking`, 'internal-ranking.pdf')
await adminPage.goto(`${base}/admin/jury-print/${competitionId}?doc=ranking`, { waitUntil: 'networkidle' })
await adminPage.emulateMedia({ media: 'print' })
await adminPage.locator('.jp-ranking').first().screenshot({ path: `${out}/21-ranking-pdf-page-1.png` })
await adminPage.emulateMedia({ media: 'screen' })
console.log('21-ranking-pdf-page-1')

await browser.close()
console.log('done')
