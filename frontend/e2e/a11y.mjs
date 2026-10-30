// Automated accessibility scan (axe-core, WCAG 2.1 A/AA rules) of the main public, participant and admin screens,
// including results/gallery after a published result. Sets up its data through the API on a fresh QA database.
// It is a practical check, not a certification: axe finds roughly a third to a half of real-world issues.
// Usage: BASE_URL=http://localhost:4174 QA_ADMIN_EMAIL=… QA_ADMIN_PASSWORD=… node e2e/a11y.mjs <outDir> <width>
import { chromium } from '@playwright/test'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

const base = process.env.BASE_URL ?? 'http://localhost:4174'
const out = process.argv[2] ?? 'e2e/screenshots'
const width = Number(process.argv[3] ?? 1440)
const axeSource = readFileSync(createRequire(import.meta.url).resolve('axe-core/axe.min.js'), 'utf8')
const fixture = (name) => fileURLToPath(new URL(`./fixtures/${name}`, import.meta.url))
mkdirSync(out, { recursive: true })
const CHEVRON = 'baki-kendleri-sevron-dizayni'

const browser = await chromium.launch({ channel: 'chromium' })
// bypassCSP only so the axe script can be injected; the app itself is unchanged.
const ctx = () => browser.newContext({ viewport: { width, height: 900 }, bypassCSP: true, reducedMotion: 'reduce' })
const report = []
let serious = 0

async function scan(page, name) {
  await page.waitForLoadState('networkidle')
  await page.addScriptTag({ content: axeSource })
  const result = await page.evaluate(async () =>
    axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'] } }))
  for (const v of result.violations) {
    const bad = v.impact === 'serious' || v.impact === 'critical'
    if (bad) serious++
    report.push({ page: name, id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.slice(0, 3).map((n) => n.target.join(' ')) })
  }
  console.log(`${result.violations.length ? '•' : '✓'} [${width}] ${name}: ${result.violations.length} violation(s)` +
    (result.violations.length ? ' — ' + result.violations.map((v) => `${v.id}(${v.impact})`).join(', ') : ''))
}

const api = async (page, method, url, body, token) =>
  page.evaluate(async ({ method, url, body, token }) => {
    const r = await fetch(url, {
      method,
      headers: { ...(body ? { 'Content-Type': 'application/json' } : {}), ...(token ? { Authorization: `Bearer ${token}` } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    })
    const t = await r.text()
    return t ? JSON.parse(t) : null
  }, { method, url, body, token })

try {
  const anon = await (await ctx()).newPage()
  for (const [name, path] of [
    ['home', '/'], ['projects', '/projects'], ['project', '/projects/baki-kendleri'], ['competitions', '/competitions'],
    ['competition', `/competitions/${CHEVRON}`], ['login', '/login'], ['register', '/register'],
    ['forgot-password', '/forgot-password'], ['results-empty', '/results'], ['terms', '/terms'], ['privacy', '/privacy'],
    ['not-found', '/yoxdur'],
  ]) {
    await anon.goto(base + path)
    await scan(anon, name)
  }

  // Participant with a submitted work (through the API), then their pages.
  const user = await (await ctx()).newPage()
  await user.goto(base + '/')
  const email = `a11y-${Date.now()}@meydan.test`
  const reg = await api(user, 'POST', '/api/auth/register', { firstName: 'Ülviyyə', lastName: 'Şahbazova', phone: '+994 50 111 22 33', email, password: 'meydan2026', confirmPassword: 'meydan2026', acceptTerms: true })
  const draft = await api(user, 'POST', '/api/submissions', { competitionSlug: CHEVRON, categorySlug: 'mastaga' }, reg.accessToken)
  await api(user, 'PUT', `/api/submissions/${draft.id}`, { answers: { title: 'Əlçatan iş', concept: 'Konsept', symbols: 'Simvol' } }, reg.accessToken)
  await user.evaluate(async ({ id, token, b64 }) => {
    const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0))
    const form = new FormData()
    form.append('fieldKey', 'files')
    form.append('file', new File([bytes], 'eskiz.png', { type: 'image/png' }))
    await fetch(`/api/submissions/${id}/files`, { method: 'POST', body: form, headers: { Authorization: `Bearer ${token}` } })
  }, { id: draft.id, token: reg.accessToken, b64: readFileSync(fixture('eskiz.png')).toString('base64') })
  // A second draft left open, to scan the form itself.
  // The API registration above already set the session cookie in this context.
  await user.goto(`${base}/submissions/${draft.id}`)
  await scan(user, 'submission-form')
  await user.goto(`${base}/submissions/${draft.id}?step=files`)
  await scan(user, 'submission-files')
  await api(user, 'POST', `/api/submissions/${draft.id}/submit`, null, reg.accessToken)
  await user.goto(base + '/my/submissions')
  await scan(user, 'my-submissions')

  // Admin screens, then publish a result for the public results/gallery scan.
  const admin = await (await ctx()).newPage()
  await admin.goto(base + '/login?next=%2Fadmin')
  await admin.getByLabel('E-mail').fill(process.env.QA_ADMIN_EMAIL)
  await admin.getByLabel('Şifrə', { exact: true }).fill(process.env.QA_ADMIN_PASSWORD)
  await admin.getByRole('button', { name: 'Daxil ol' }).click()
  await admin.waitForURL(/\/admin$/)
  const token = (await api(admin, 'POST', '/api/auth/login', { email: process.env.QA_ADMIN_EMAIL, password: process.env.QA_ADMIN_PASSWORD }))?.accessToken
  const comp = (await api(admin, 'GET', '/api/admin/competitions', null, token)).find((c) => c.slug === CHEVRON)
  await scan(admin, 'admin-dashboard')
  await admin.goto(`${base}/admin/submissions`)
  await scan(admin, 'admin-submissions')
  await admin.goto(`${base}/admin/submissions/${draft.id}`)
  await scan(admin, 'admin-submission-detail')
  for (const tab of ['settings', 'categories', 'form', 'results']) {
    await admin.goto(`${base}/admin/competitions/${comp.id}?tab=${tab}`)
    await scan(admin, `admin-competition-${tab}`)
  }
  await admin.goto(`${base}/admin/submissions/${draft.id}`)
  await admin.getByRole('button', { name: /üzrə qalib seç$/ }).click()
  await admin.getByRole('dialog').waitFor()
  await scan(admin, 'admin-winner-dialog')
  await admin.getByRole('button', { name: 'Bəli, qalib seç' }).click()
  await admin.getByText('Qalib seçildi.', { exact: false }).waitFor()
  await api(admin, 'PUT', `/api/admin/competitions/${comp.id}/status`, { status: 'Closed' }, token)
  await api(admin, 'POST', `/api/admin/competitions/${comp.id}/result/publish`, null, token)
  await admin.goto(`${base}/admin/competitions/${comp.id}?tab=results`)
  await admin.locator('.a-slot.is-won').first().waitFor()
  await scan(admin, 'admin-results-published')

  const ref = (await api(admin, 'GET', `/api/admin/submissions/${draft.id}`, null, token)).referenceNumber
  for (const [name, path] of [
    ['results', `/competitions/${CHEVRON}/results`], ['gallery', `/competitions/${CHEVRON}/gallery`],
    ['gallery-work', `/competitions/${CHEVRON}/gallery/${ref}`], ['results-list', '/results'],
    ['competition-after-results', `/competitions/${CHEVRON}`],
  ]) {
    await anon.goto(base + path)
    await scan(anon, name)
  }

  writeFileSync(`${out}/a11y-${width}.json`, JSON.stringify(report, null, 2))
  console.log(`axe: ${report.length} violation group(s), ${serious} serious/critical — details in a11y-${width}.json`)
  if (serious) process.exitCode = 1
} catch (err) {
  console.error(`✗ [${width}] ${err.message}`)
  process.exitCode = 1
} finally {
  await browser.close()
}
